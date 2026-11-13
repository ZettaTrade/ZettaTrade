namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Sound;
using ZettaTrade.Plugins.Abstractions;

/// <summary>
/// The engine's end-to-end facts (T-096): an armed alert fires on its exact
/// edge and stays quiet on the near-miss (the paired no-fire tick is proven
/// processed by the FIFO observation route when the eventual fire lands),
/// the fire is persistent with the tick's own stamp, a reloaded fired
/// roster never fires again, the snooze re-arms stream-side (the re-arm
/// tick evaluates nothing, the next one evaluates), the percent move
/// evaluates against its window's oldest reference, a sound failure
/// degrades into a still-completed fire, and garbage input is refused
/// before anything persists. All exercises run OFFLINE: the fake adapter is
/// the only tick source.
/// </summary>
public sealed class AlertEngineTests
{
    private static readonly Symbol BtcUsdt = Symbol.Parse("BTC/USDT");

    /// <summary>The UTC base the test clock pins and every fixture timestamps shifts of.</summary>
    private static readonly DateTimeOffset Base = new(2026, 3, 2, 12, 0, 0, TimeSpan.Zero);

    /// <summary>How long a queued fire may take to surface in this standalone suite (a wait budget, never wall-clock semantics).</summary>
    private static readonly TimeSpan EventWaitBudget = TimeSpan.FromSeconds(10);

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Base;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class TestSettings : ISettingsService
    {
        private readonly Dictionary<string, string> _store = [];

        public List<string> Saved { get; } = new();

        public Task<T> GetAsync<T>(string section, string key, T defaultValue = default!)
        {
            _store.TryGetValue($"{section}\0{key}", out var value);
            return Task.FromResult(value is null ? defaultValue : (T)(object)value);
        }

        public Task SetAsync<T>(string section, string key, T value)
        {
            var text = (string)(object?)value!;
            _store[$"{section}\0{key}"] = text;
            Saved.Add(text);
            return Task.CompletedTask;
        }

        public IObservable<SettingsChange> Changes => Observable.Empty<SettingsChange>();

        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class TestAdapter : IMarketDataAdapter
    {
        public string Id => "demo";

        public string Name => "Demo";

        public bool IsConnected => true;

        public ExchangeCapabilities Capabilities => ExchangeCapabilities.MarketData;

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<IReadOnlyList<Symbol>> GetSymbolsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Symbol>>([BtcUsdt]);

        public IObservable<Ticker> SubscribeTicker(Symbol symbol) =>
            Tickers.GetOrAdd(symbol.Normalized, _ => new Subject<Ticker>());

        public IObservable<OrderBook> SubscribeOrderBook(Symbol symbol, int depth = 20) => Observable.Empty<OrderBook>();

        public IObservable<MarketTrade> SubscribeTrades(Symbol symbol) => Observable.Empty<MarketTrade>();

        public Task<IReadOnlyList<Bar>> GetBarsAsync(
            Symbol symbol,
            TimeFrame timeFrame,
            DateTimeOffset from,
            DateTimeOffset? to = null,
            int limit = 500,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref seedCalls);
            return Task.FromResult(SeededBars);
        }

        /// <summary>The bars the one-shot M1 history seed returns (empty: the honest no-history page).</summary>
        public IReadOnlyList<Bar> SeededBars { get; set; } = [];

        /// <summary>How many history fetches the engine ran (the seed's one-shot pin).</summary>
        public int SeedCalls => seedCalls;

        private int seedCalls;

        public ConcurrentDictionary<string, Subject<Ticker>> Tickers { get; } = new();
    }

    private sealed class TestSound : IAlertSoundPlayer
    {
        public int Played { get; private set; }

        public bool ThrowOnPlay { get; set; }

        public void Play()
        {
            Played++;
            if (ThrowOnPlay)
            {
                throw new InvalidOperationException("The platform tone refused to sound (test fixture).");
            }
        }
    }

    /// <summary>The indicator catalog fixture: the scripted implementations it carries resolve by name, a non-positive period resolves to nothing.</summary>
    private sealed class TestCatalog : IIndicatorCatalog
    {
        public TestCatalog(params IIndicator[] indicators) => Indicators = indicators;

        public IIndicator[] Indicators { get; }

        public IReadOnlyCollection<string> LoadedNames => Indicators.Select(indicator => indicator.Name).ToArray();

        public IIndicator? Create(string name, int period) =>
            period <= 0 ? null : Indicators.FirstOrDefault(indicator => indicator.Name == name);
    }

    /// <summary>
    /// The scripted indicator implementation (T-203's canned stream): the
    /// values align by bar index exactly as the plugin contract demands, the
    /// script's tail holds past its end (a longer series never breaks the
    /// alignment), and a null value is the warmup stretch the cross waits
    /// through.
    /// </summary>
    private sealed class ScriptedIndicator : IIndicator
    {
        public ScriptedIndicator(string name, params decimal?[] values)
        {
            Name = name;
            Values = values;
        }

        public string Name { get; }

        private decimal?[] Values { get; }

        public IReadOnlyList<decimal?> Apply(IReadOnlyList<IndicatorBar> bars)
        {
            var aligned = new decimal?[bars.Count];
            for (var index = 0; index < aligned.Length; index++)
            {
                aligned[index] = index < Values.Length ? Values[index] : Values[^1];
            }

            return aligned;
        }
    }

    private sealed record Rig(AlertEngine Engine, TestSettings Settings, TestAdapter Adapter, TestSound Sound, TestClock Clock);

    private static Rig BuildRig(
        TestSettings? settings = null,
        TestAdapter? adapter = null,
        IIndicatorCatalog? indicators = null)
    {
        settings ??= new TestSettings();
        adapter ??= new TestAdapter();
        var sound = new TestSound();
        var clock = new TestClock();
        var engine = new AlertEngine(
            settings,
            adapter,
            indicators ?? new TestCatalog(),
            sound,
            clock,
            NullLogger<AlertEngine>.Instance);
        return new Rig(engine, settings, adapter, sound, clock);
    }

    private static AlertDraft PriceDraft(decimal target, AlertTrigger trigger) => new(
        SymbolName: BtcUsdt.Normalized,
        Kind: PriceAlertKind.Price,
        Trigger: trigger,
        TargetPrice: target,
        ThresholdPercent: 0m,
        WindowMinutes: 0,
        Note: string.Empty);

    private static AlertDraft PercentDraft(decimal threshold, int windowMinutes) => new(
        SymbolName: BtcUsdt.Normalized,
        Kind: PriceAlertKind.PercentMove,
        Trigger: AlertTrigger.At,
        TargetPrice: 0m,
        ThresholdPercent: threshold,
        WindowMinutes: windowMinutes,
        Note: string.Empty);

    private static AlertDraft CrossDraft(
        string indicatorName,
        AlertCrossSource source,
        decimal level,
        AlertTrigger edge = AlertTrigger.Above,
        int period = 14,
        string symbolName = "BTC/USDT") => new(
        SymbolName: symbolName,
        Kind: PriceAlertKind.IndicatorCross,
        Trigger: edge,
        TargetPrice: 0m,
        ThresholdPercent: 0m,
        WindowMinutes: 0,
        Note: string.Empty,
        Indicator: new AlertIndicatorSpec(indicatorName, period, source, level));

    private static (ConcurrentQueue<AlertEvent> Census, TaskCompletionSource<AlertEvent> FirstFire) WireFiredLatch(
        AlertEngine engine,
        AlertDefinition? owner = null)
    {
        var census = new ConcurrentQueue<AlertEvent>();
        var firstFire = new TaskCompletionSource<AlertEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Events.Subscribe(
            @event =>
            {
                census.Enqueue(@event);
                Settle(firstFire, @event, owner);
            },
            ex => firstFire.TrySetException(ex));
        return (census, firstFire);
    }

    /// <summary>The latch settles on the owner's fire (or any fire when no owner is named).</summary>
    private static void Settle(
        TaskCompletionSource<AlertEvent> firstFire,
        AlertEvent @event,
        AlertDefinition? owner)
    {
        if (@event.Kind != AlertEventKind.Fired)
        {
            return;
        }

        if (owner is null || @event.Definition!.Id == owner.Id)
        {
            firstFire.TrySetResult(@event);
        }
    }

    private static void PushTick(TestAdapter adapter, decimal price, DateTimeOffset occurred) =>
        adapter.Tickers[BtcUsdt.Normalized].OnNext(new Ticker(
            BtcUsdt,
            price,
            price,
            price,
            1m,
            0m,
            occurred));

    [Fact]
    public async Task AboveEdge_FiresStrictlyAfterTheTarget_NeverAtIt()
    {
        var rig = BuildRig();
        await rig.Engine.InitializeAsync();
        await rig.Engine.CreateAsync(PriceDraft(110m, AlertTrigger.Above));
        var (census, firstFire) = WireFiredLatch(rig.Engine);

        PushTick(rig.Adapter, 110m, Base.AddMinutes(1));
        PushTick(rig.Adapter, 110.00000001m, Base.AddMinutes(2));

        var fired = await firstFire.Task.WaitAsync(EventWaitBudget);
        fired.Definition!.LastFiredUtc.Should().Be(Base.AddMinutes(2), "the fire stamps the qualifying tick's own moment");
        rig.Sound.Played.Should().Be(1);
        CountFires(census).Should().Be(1);
        rig.Engine.Definitions.Should().ContainSingle()
            .Which.Status.Should().Be(AlertStatus.Fired);
        rig.Settings.Saved.Should().NotBeEmpty();
        rig.Settings.Saved[^1].Should().Contain("\"Fired\"", "the fire persisted");
        await rig.Engine.DisposeAsync();
    }

    [Fact]
    public async Task BelowEdge_FiresStrictlyUnderTheTarget_NeverAtIt()
    {
        var rig = BuildRig();
        await rig.Engine.InitializeAsync();
        await rig.Engine.CreateAsync(PriceDraft(110m, AlertTrigger.Below));
        var (census, firstFire) = WireFiredLatch(rig.Engine);

        PushTick(rig.Adapter, 110m, Base.AddMinutes(1));
        PushTick(rig.Adapter, 109.99999999m, Base.AddMinutes(2));

        (await firstFire.Task.WaitAsync(EventWaitBudget)).Definition!.LastFiredUtc
            .Should().Be(Base.AddMinutes(2));
        CountFires(census).Should().Be(1);
        rig.Engine.Definitions.Should().ContainSingle()
            .Which.Status.Should().Be(AlertStatus.Fired);
        await rig.Engine.DisposeAsync();
    }

    [Fact]
    public async Task AtEdge_FiresOnExactEquality_WithThePairedNearMiss()
    {
        var rig = BuildRig();
        await rig.Engine.InitializeAsync();
        await rig.Engine.CreateAsync(PriceDraft(110m, AlertTrigger.At));
        var (census, firstFire) = WireFiredLatch(rig.Engine);

        PushTick(rig.Adapter, 110.00000001m, Base.AddMinutes(1));
        PushTick(rig.Adapter, 110m, Base.AddMinutes(2));

        (await firstFire.Task.WaitAsync(EventWaitBudget)).Definition!.LastFiredUtc
            .Should().Be(Base.AddMinutes(2), "the exact tick fires; the near-miss never did");
        CountFires(census).Should().Be(1);
        await rig.Engine.DisposeAsync();
    }

    [Fact]
    public async Task PercentMove_FiresExactlyAtTheThreshold()
    {
        var rig = BuildRig();
        await rig.Engine.InitializeAsync();
        var created = await rig.Engine.CreateAsync(PercentDraft(10m, 15));
        var (census, firstFire) = WireFiredLatch(rig.Engine, created);

        PushTick(rig.Adapter, 100m, Base.AddMinutes(1));
        PushTick(rig.Adapter, 110m, Base.AddMinutes(2));

        (await firstFire.Task.WaitAsync(EventWaitBudget)).Definition!.LastFiredUtc
            .Should().Be(Base.AddMinutes(2), "the equality boundary belongs to the alert");
        CountFires(census).Should().Be(1);
        rig.Engine.Definitions.Should().ContainSingle()
            .Which.Status.Should().Be(AlertStatus.Fired);
        await rig.Engine.DisposeAsync();
    }

    [Fact]
    public async Task PercentMove_FiresAgainstTheOldestInWindowReference()
    {
        var rig = BuildRig();
        await rig.Engine.InitializeAsync();
        await rig.Engine.CreateAsync(PercentDraft(10m, 15));
        var (census, firstFire) = WireFiredLatch(rig.Engine);

        PushTick(rig.Adapter, 100m, Base.AddMinutes(0));
        PushTick(rig.Adapter, 100.1m, Base.AddMinutes(1));
        PushTick(rig.Adapter, 130m, Base.AddMinutes(2));

        (await firstFire.Task.WaitAsync(EventWaitBudget)).Definition!.LastFiredUtc
            .Should().Be(Base.AddMinutes(2), "the reference is the oldest in-window entry (100), not the freshest");
        CountFires(census).Should().Be(1, "the 0.1% pair never fired");
        await rig.Engine.DisposeAsync();
    }

    [Fact]
    public async Task SnoozedAlert_ReArmsStreamSide_TheReArmTickEvaluatesNothing()
    {
        var rig = BuildRig();
        await rig.Engine.InitializeAsync();
        var created = await rig.Engine.CreateAsync(PriceDraft(110m, AlertTrigger.Above));
        await rig.Engine.SnoozeAsync(created.Id);
        var (census, firstFire) = WireFiredLatch(rig.Engine, created);

        PushTick(rig.Adapter, 200m, Base.AddMinutes(10));
        PushTick(rig.Adapter, 200m, Base.AddMinutes(16));
        PushTick(rig.Adapter, 200m, Base.AddMinutes(17));

        (await firstFire.Task.WaitAsync(EventWaitBudget)).Definition!.LastFiredUtc
            .Should().Be(Base.AddMinutes(17), "the window tick re-armed, the next tick fired");
        CountFires(census).Should().Be(1);
        await rig.Engine.DisposeAsync();
    }

    [Fact]
    public async Task ReloadedFiredRoster_NeverFiresAgain()
    {
        var seed = new TestSettings();
        var firstRig = BuildRig(seed);
        await firstRig.Engine.InitializeAsync();
        var reloadedId = (await firstRig.Engine.CreateAsync(PriceDraft(110m, AlertTrigger.Above))).Id;
        var (firstCensus, firstFire) = WireFiredLatch(firstRig.Engine);
        PushTick(firstRig.Adapter, 111m, Base.AddMinutes(1));
        await firstFire.Task.WaitAsync(EventWaitBudget);
        CountFires(firstCensus).Should().Be(1);
        await firstRig.Engine.DisposeAsync();

        var secondRig = BuildRig(seed);
        await secondRig.Engine.InitializeAsync();
        secondRig.Engine.Definitions.Should().ContainSingle()
            .Which.Status.Should().Be(AlertStatus.Fired, "the fired roster reloads read-only");
        var liveOwner = await secondRig.Engine.CreateAsync(PriceDraft(500m, AlertTrigger.Above));
        var (census, fire) = WireFiredLatch(secondRig.Engine, liveOwner);

        PushTick(secondRig.Adapter, 600m, Base.AddMinutes(50));

        (await fire.Task.WaitAsync(EventWaitBudget)).Definition!.Id
            .Should().Be(liveOwner.Id, "the reload's fire belongs to the live alert");
        CountFires(census).Should().Be(1, "the reloaded fired alert never re-fires");
        secondRig.Engine.Definitions.Single(definition => definition.Id == reloadedId).Status
            .Should().Be(AlertStatus.Fired);
        await secondRig.Engine.DisposeAsync();
    }

    [Fact]
    public async Task SoundFailure_DegradesIntoACompletedFire()
    {
        var rig = BuildRig();
        rig.Sound.ThrowOnPlay = true;
        await rig.Engine.InitializeAsync();
        await rig.Engine.CreateAsync(PriceDraft(110m, AlertTrigger.Above));
        var (census, firstFire) = WireFiredLatch(rig.Engine);

        PushTick(rig.Adapter, 111m, Base.AddMinutes(1));

        (await firstFire.Task.WaitAsync(EventWaitBudget)).Definition!.Status
            .Should().Be(AlertStatus.Fired, "a failed beep never breaks an evaluation");
        rig.Sound.Played.Should().Be(1);
        CountFires(census).Should().Be(1);
        await rig.Engine.DisposeAsync();
    }

    [Fact]
    public async Task InvalidDraft_RefusedBeforeAnythingPersists()
    {
        var rig = BuildRig();
        await rig.Engine.InitializeAsync();

        var refuse = async () => await rig.Engine.CreateAsync(PriceDraft(0m, AlertTrigger.Above));
        (await refuse.Should().ThrowAsync<ArgumentException>())
            .WithMessage("*strictly positive target price*");

        rig.Engine.Definitions.Should().BeEmpty();
        rig.Settings.Saved.Should().BeEmpty();
        await rig.Engine.DisposeAsync();
    }

    [Fact]
    public async Task AGarbageSymbolSpelling_RefusesBeforeAnythingPersists()
    {
        var rig = BuildRig();
        await rig.Engine.InitializeAsync();
        var draft = new AlertDraft(
            SymbolName: "not a symbol",
            Kind: PriceAlertKind.Price,
            Trigger: AlertTrigger.Above,
            TargetPrice: 110m,
            ThresholdPercent: 0m,
            WindowMinutes: 0,
            Note: string.Empty);

        var refuse = async () => await rig.Engine.CreateAsync(draft);
        (await refuse.Should().ThrowAsync<ArgumentException>())
            .WithMessage("*not parsable under the alerts vocabulary*");

        rig.Engine.Definitions.Should().BeEmpty("a refused spelling never joins the roster");
        rig.Settings.Saved.Should().BeEmpty();
        await rig.Engine.DisposeAsync();
    }

    [Fact]
    public async Task Snooze_RefusesTheFiredAlert()
    {
        var rig = BuildRig();
        await rig.Engine.InitializeAsync();
        var created = await rig.Engine.CreateAsync(PriceDraft(110m, AlertTrigger.Above));
        var (_, firstFire) = WireFiredLatch(rig.Engine, created);
        PushTick(rig.Adapter, 111m, Base.AddMinutes(1));
        await firstFire.Task.WaitAsync(EventWaitBudget);

        var refuse = async () => await rig.Engine.SnoozeAsync(created.Id);
        (await refuse.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*terminal*");

        await rig.Engine.DisposeAsync();
    }

    [Fact]
    public async Task Delete_RemovesTheDefinition_AndPersistsTheSkeleton()
    {
        var rig = BuildRig();
        await rig.Engine.InitializeAsync();
        var created = await rig.Engine.CreateAsync(PriceDraft(110m, AlertTrigger.Above));

        await rig.Engine.DeleteAsync(created.Id);

        rig.Engine.Definitions.Should().BeEmpty();
        rig.Settings.Saved.Should().HaveCount(2);
        rig.Settings.Saved[^1].Should().Be(AlertCodec.Encode(new List<AlertDefinition>()), "the delete persisted the honest skeleton");
        await rig.Engine.DisposeAsync();
    }

    [Fact]
    public async Task UnknownId_OpsRefuse()
    {
        var rig = BuildRig();
        await rig.Engine.InitializeAsync();
        var unknown = Guid.NewGuid();
        var draft = PriceDraft(1m, AlertTrigger.Above);

        (await FluentActions
            .Invoking(() => rig.Engine.UpdateAsync(unknown, draft))
            .Should().ThrowAsync<InvalidOperationException>())
            .WithMessage($"*'{unknown}'*", "the edit names no roster member");
        (await FluentActions
            .Invoking(() => rig.Engine.DeleteAsync(unknown))
            .Should().ThrowAsync<InvalidOperationException>())
            .WithMessage($"*'{unknown}'*");
        (await FluentActions
            .Invoking(() => rig.Engine.SnoozeAsync(unknown))
            .Should().ThrowAsync<InvalidOperationException>())
            .WithMessage($"*'{unknown}'*");

        rig.Engine.Definitions.Should().BeEmpty("a refused operation never wrote the roster");
        rig.Settings.Saved.Should().BeEmpty();
        await rig.Engine.DisposeAsync();
    }

    #region Indicator Cross (T-203)

    [Fact]
    public async Task AnIndicatorValueCross_FiresOnTheCrossingClose_ExactlyOnce()
    {
        var rig = BuildRig(indicators: new TestCatalog(new ScriptedIndicator("TESTIND", 60m, 65m, 71m)));
        await rig.Engine.InitializeAsync();
        var created = await rig.Engine.CreateAsync(CrossDraft("TESTIND", AlertCrossSource.IndicatorValue, 70m));
        var (census, firstFire) = WireFiredLatch(rig.Engine, created);

        // Ticks m+1..m+4: each arriving tick closes the previous minute's
        // candle. The crossing close is m+3's candle (65 -> 71 against the
        // level 70), learned when the m+4 tick arrives; the earlier closes
        // ride FIFO ahead of it, so a single census fire proves they stayed
        // quiet.
        PushTick(rig.Adapter, 100m, Base.AddMinutes(1));
        PushTick(rig.Adapter, 100m, Base.AddMinutes(2));
        PushTick(rig.Adapter, 100m, Base.AddMinutes(3));
        PushTick(rig.Adapter, 100m, Base.AddMinutes(4));

        (await firstFire.Task.WaitAsync(EventWaitBudget)).Definition!.LastFiredUtc
            .Should().Be(Base.AddMinutes(4), "the fire stamps the crossing close's own completion");
        CountFires(census).Should().Be(1, "the warmup and the level-holding closes never fire");
        rig.Engine.Definitions.Should().ContainSingle()
            .Which.Status.Should().Be(AlertStatus.Fired);
        rig.Sound.Played.Should().Be(1);
        rig.Settings.Saved[^1].Should().Contain("\"Fired\"", "the fire persisted");
        await rig.Engine.DisposeAsync();
    }

    [Fact]
    public async Task TheCrosssEqualityBoundary_BelongsToTheArrivingClose()
    {
        var rig = BuildRig(indicators: new TestCatalog(new ScriptedIndicator("TESTIND", 69m, 70m)));
        await rig.Engine.InitializeAsync();
        var created = await rig.Engine.CreateAsync(CrossDraft("TESTIND", AlertCrossSource.IndicatorValue, 70m));
        var (_, firstFire) = WireFiredLatch(rig.Engine, created);

        PushTick(rig.Adapter, 100m, Base.AddMinutes(1));
        PushTick(rig.Adapter, 100m, Base.AddMinutes(2));
        PushTick(rig.Adapter, 100m, Base.AddMinutes(3));

        (await firstFire.Task.WaitAsync(EventWaitBudget)).Definition!.LastFiredUtc
            .Should().Be(Base.AddMinutes(3), "69 -> 70 is a cross: the equality boundary belongs to the arriving close");
        await rig.Engine.DisposeAsync();
    }

    [Fact]
    public async Task AlreadyAtTheLevel_NeverFires_TheLaterRealCrossStillDoes()
    {
        var rig = BuildRig(indicators: new TestCatalog(new ScriptedIndicator("TESTIND", 70m, 71m, 65m, 71m)));
        await rig.Engine.InitializeAsync();
        var created = await rig.Engine.CreateAsync(CrossDraft("TESTIND", AlertCrossSource.IndicatorValue, 70m));
        var (census, firstFire) = WireFiredLatch(rig.Engine, created);

        // 70 -> 71 never left the level (no cross); the much later 65 -> 71
        // is the real cross — its fire proves the earlier close stayed quiet.
        PushTick(rig.Adapter, 100m, Base.AddMinutes(1));
        PushTick(rig.Adapter, 100m, Base.AddMinutes(2));
        PushTick(rig.Adapter, 100m, Base.AddMinutes(3));
        PushTick(rig.Adapter, 100m, Base.AddMinutes(4));
        PushTick(rig.Adapter, 100m, Base.AddMinutes(5));

        (await firstFire.Task.WaitAsync(EventWaitBudget)).Definition!.LastFiredUtc
            .Should().Be(Base.AddMinutes(5), "only the 65 -> 71 pair is a cross");
        CountFires(census).Should().Be(1, "a value already at the level never crosses it by rising");
        await rig.Engine.DisposeAsync();
    }

    [Fact]
    public async Task WarmupNullCloses_NeverFire_TheCrossWaitsForTwoComputableCloses()
    {
        var rig = BuildRig(indicators: new TestCatalog(new ScriptedIndicator("TESTIND", null, null, 65m, 71m)));
        await rig.Engine.InitializeAsync();
        var created = await rig.Engine.CreateAsync(CrossDraft("TESTIND", AlertCrossSource.IndicatorValue, 70m));
        var (census, firstFire) = WireFiredLatch(rig.Engine, created);

        PushTick(rig.Adapter, 100m, Base.AddMinutes(1));
        PushTick(rig.Adapter, 100m, Base.AddMinutes(2));
        PushTick(rig.Adapter, 100m, Base.AddMinutes(3));
        PushTick(rig.Adapter, 100m, Base.AddMinutes(4));
        PushTick(rig.Adapter, 100m, Base.AddMinutes(5));

        (await firstFire.Task.WaitAsync(EventWaitBudget)).Definition!.LastFiredUtc
            .Should().Be(Base.AddMinutes(5), "the first computable pair (65 -> 71) is the cross");
        CountFires(census).Should().Be(1, "the warmup stretch's null pairs never fire");
        await rig.Engine.DisposeAsync();
    }

    [Fact]
    public async Task AnUnresolvableIndicator_PausesAtTheFirstClose_ItCannotHonor()
    {
        var rig = BuildRig(); // the empty catalog: no plugin carries the name
        await rig.Engine.InitializeAsync();
        await rig.Engine.CreateAsync(CrossDraft("GHOST", AlertCrossSource.IndicatorValue, 70m));
        var rosterChanged = WireRosterLatch(rig.Engine);

        // Two closes: the first lands the opening bar, the second is the
        // first close the engine can actually evaluate — and it pauses there.
        PushTick(rig.Adapter, 100m, Base.AddMinutes(1));
        PushTick(rig.Adapter, 100m, Base.AddMinutes(2));
        PushTick(rig.Adapter, 100m, Base.AddMinutes(3));

        await rosterChanged.Task.WaitAsync(EventWaitBudget);
        rig.Engine.Definitions.Should().ContainSingle()
            .Which.Status.Should().Be(AlertStatus.Paused, "the close the plugin set cannot honor pauses, never guesses");
        await AwaitPersistedAsync(rig.Settings, "\"Paused\"");

        // Further closes change nothing: the stall is not per-close churn.
        PushTick(rig.Adapter, 100m, Base.AddMinutes(4));
        rig.Engine.Definitions.Single().Status.Should().Be(AlertStatus.Paused);
        await rig.Engine.DisposeAsync();
    }

    [Fact]
    public async Task APausedIndicatorAlert_ReArms_WhenTheSessionResolvesItsIndicator()
    {
        var store = new TestSettings();
        var firstRig = BuildRig(store); // the plugin set misses the indicator
        await firstRig.Engine.InitializeAsync();
        await firstRig.Engine.CreateAsync(CrossDraft("TESTIND", AlertCrossSource.IndicatorValue, 70m));
        var rosterChanged = WireRosterLatch(firstRig.Engine);
        PushTick(firstRig.Adapter, 100m, Base.AddMinutes(1));
        PushTick(firstRig.Adapter, 100m, Base.AddMinutes(2));
        PushTick(firstRig.Adapter, 100m, Base.AddMinutes(3));
        await rosterChanged.Task.WaitAsync(EventWaitBudget);
        firstRig.Engine.Definitions.Should().ContainSingle()
            .Which.Status.Should().Be(AlertStatus.Paused);
        await firstRig.Engine.DisposeAsync();

        // The next session loads the plugin: the roster load re-arms the
        // stored Paused alert and the evaluation is live again.
        var secondRig = BuildRig(store, indicators: new TestCatalog(new ScriptedIndicator("TESTIND", 60m, 65m, 71m)));
        await secondRig.Engine.InitializeAsync();
        var reArmed = secondRig.Engine.Definitions.Should().ContainSingle().Subject;
        reArmed.Status.Should().Be(AlertStatus.Armed, "a session whose plugin set resolves the indicator re-arms the stall");
        var (census, firstFire) = WireFiredLatch(secondRig.Engine, reArmed);

        PushTick(secondRig.Adapter, 100m, Base.AddMinutes(1));
        PushTick(secondRig.Adapter, 100m, Base.AddMinutes(2));
        PushTick(secondRig.Adapter, 100m, Base.AddMinutes(3));
        PushTick(secondRig.Adapter, 100m, Base.AddMinutes(4));

        (await firstFire.Task.WaitAsync(EventWaitBudget)).Definition!.Id
            .Should().Be(reArmed.Id, "the re-armed alert evaluates again");
        CountFires(census).Should().Be(1);
        await secondRig.Engine.DisposeAsync();
    }

    [Fact]
    public async Task ThePriceSource_FiresWhenTheCloseCrossesTheIndicatorValue()
    {
        var rig = BuildRig(indicators: new TestCatalog(new ScriptedIndicator("TESTMA", 100m, 100m, 95m, 95m)));
        await rig.Engine.InitializeAsync();
        var created = await rig.Engine.CreateAsync(CrossDraft("TESTMA", AlertCrossSource.Price, 0m));
        var (census, firstFire) = WireFiredLatch(rig.Engine, created);

        // Candles close 99, 99, 99, then 96: close - indicator flips from
        // strictly negative (99 - 100) to at-or-positive (99 - 95) on the
        // third close — the price crossed above its moving level.
        PushTick(rig.Adapter, 99m, Base.AddMinutes(1));
        PushTick(rig.Adapter, 99m, Base.AddMinutes(2));
        PushTick(rig.Adapter, 99m, Base.AddMinutes(3));
        PushTick(rig.Adapter, 96m, Base.AddMinutes(4));
        PushTick(rig.Adapter, 96m, Base.AddMinutes(5));

        (await firstFire.Task.WaitAsync(EventWaitBudget)).Definition!.LastFiredUtc
            .Should().Be(Base.AddMinutes(4), "the sign flip of close - indicator is the cross");
        CountFires(census).Should().Be(1);
        await rig.Engine.DisposeAsync();
    }

    [Fact]
    public async Task TheHistorySeed_BindsTheFirstClose_AndTheSeededTailYieldsToTheSessionClose()
    {
        var adapter = new TestAdapter();
        var minute = Base;
        adapter.SeededBars =
        [
            new Bar(minute.AddMinutes(-2), 60m, 61m, 59m, 60m, 1m),
            new Bar(minute.AddMinutes(-1), 65m, 66m, 64m, 65m, 1m),
            new Bar(minute, 75m, 76m, 74m, 75m, 1m),
        ];
        var rig = BuildRig(adapter: adapter, indicators: new TestCatalog(new ScriptedIndicator("TESTIND", 60m, 65m, 75m, 71m)));
        await rig.Engine.InitializeAsync();
        var created = await rig.Engine.CreateAsync(CrossDraft("TESTIND", AlertCrossSource.IndicatorValue, 70m));
        var (census, firstFire) = WireFiredLatch(rig.Engine, created);

        // The session's own close of the SAME minute the seed's tail bar
        // displayed (12:00): the tail yields to it, so the evaluated pair is
        // 65 -> 71 — the cross fires on the FIRST session close. Without the
        // yield the pair would be 75 -> 71 (no cross) and nothing would fire.
        PushTick(adapter, 71m, minute.AddSeconds(30));
        PushTick(adapter, 71m, minute.AddMinutes(1));

        (await firstFire.Task.WaitAsync(EventWaitBudget)).Definition!.LastFiredUtc
            .Should().Be(minute.AddMinutes(1), "the crossing close is the displayed minute's own candle");
        CountFires(census).Should().Be(1, "the seeded history made the first close evaluable, exactly once");
        adapter.SeedCalls.Should().Be(1, "the history seed is a one-shot per live period");

        // Another cross alert on the same symbol reuses the seeded series:
        // no second fetch.
        await rig.Engine.CreateAsync(CrossDraft("TESTIND", AlertCrossSource.IndicatorValue, 60m, AlertTrigger.Below));
        adapter.SeedCalls.Should().Be(1);
        await rig.Engine.DisposeAsync();
    }

    [Fact]
    public async Task AnIndicatorCrossDraft_WithTheAtEdge_IsRefused()
    {
        var rig = BuildRig();
        await rig.Engine.InitializeAsync();

        var refuse = async () => await rig.Engine.CreateAsync(
            CrossDraft("TESTIND", AlertCrossSource.IndicatorValue, 70m, AlertTrigger.At));
        (await refuse.Should().ThrowAsync<ArgumentException>())
            .WithMessage("*Above or Below*");

        rig.Engine.Definitions.Should().BeEmpty();
        rig.Settings.Saved.Should().BeEmpty();
        await rig.Engine.DisposeAsync();
    }

    [Fact]
    public async Task AnIndicatorCrossDraft_WithMissingFacts_IsRefused()
    {
        var rig = BuildRig();
        await rig.Engine.InitializeAsync();

        var noSpec = async () => await rig.Engine.CreateAsync(new AlertDraft(
            SymbolName: BtcUsdt.Normalized,
            Kind: PriceAlertKind.IndicatorCross,
            Trigger: AlertTrigger.Above,
            TargetPrice: 0m,
            ThresholdPercent: 0m,
            WindowMinutes: 0,
            Note: string.Empty));
        (await noSpec.Should().ThrowAsync<ArgumentException>())
            .WithMessage("*needs its indicator facts*");

        var zeroPeriod = async () => await rig.Engine.CreateAsync(
            CrossDraft("TESTIND", AlertCrossSource.IndicatorValue, 70m, period: 0));
        (await zeroPeriod.Should().ThrowAsync<ArgumentException>())
            .WithMessage("*strictly positive period*");

        rig.Engine.Definitions.Should().BeEmpty("a refused draft never joins the roster");
        await rig.Engine.DisposeAsync();
    }

    #endregion Indicator Cross (T-203)

    /// <summary>The latch settles on the first roster change (the pause path's own publish; wire it after the create).</summary>
    private static TaskCompletionSource<AlertEvent> WireRosterLatch(AlertEngine engine)
    {
        var latch = new TaskCompletionSource<AlertEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Events.Subscribe(
            @event =>
            {
                if (@event.Kind == AlertEventKind.RosterChanged)
                {
                    latch.TrySetResult(@event);
                }
            },
            ex => latch.TrySetException(ex));
        return latch;
    }

    /// <summary>
    /// Wait until one roster persist carried the marker state: the pause's
    /// own publish precedes its persist, so the fact polls the store within
    /// the same budget instead of assuming the write landed.
    /// </summary>
    private static async Task AwaitPersistedAsync(TestSettings settings, string marker)
    {
        var deadline = DateTimeOffset.UtcNow + EventWaitBudget;
        while (!settings.Saved.Any(saved => saved.Contains(marker, StringComparison.Ordinal)))
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"the roster never persisted the {marker} state within the budget");
            }

            await Task.Delay(10);
        }
    }

    private static int CountFires(IEnumerable<AlertEvent> census) =>
        census.Count(@event => @event.Kind == AlertEventKind.Fired);
}
