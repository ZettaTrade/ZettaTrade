namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
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
/// The engine's end-to-end facts (T-096, T-204): an armed alert fires on its exact
/// edge and stays quiet on the near-miss (the paired no-fire tick is proven
/// processed by the FIFO observation route when the eventual fire lands),
/// the fire is persistent with the tick's own stamp, a reloaded fired
/// roster never fires again, the snooze re-arms stream-side (the re-arm
/// tick evaluates nothing, the next one evaluates), the percent move
/// evaluates against its window's oldest reference, a sound failure
/// degrades into a still-completed fire, and garbage input is refused
/// before anything persists. The T-204 fire mechanics ride the same
/// offline rig: a per-crossing re-arm waits for a fresh crossing, a
/// cooldown re-arms into its snooze window and refires after it, a silent
/// alert fires without the beep, an edit forgets the re-arm gate, and every
/// fire bridges its toast into the notification center (DND and the source
/// mute included). All exercises run OFFLINE: the fake adapter is
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

        public IObservable<MarketTrade> SubscribeTrades(Symbol symbol) =>
            Observable.Create<MarketTrade>(observer =>
            {
                var subject = Trades.GetOrAdd(symbol.Normalized, _ => new Subject<MarketTrade>());
                Interlocked.Increment(ref tradeSubscriptions);
                var subscription = subject.Subscribe(observer);

                return Disposable.Create(() =>
                {
                    Interlocked.Increment(ref tradeUnsubscriptions);
                    subscription.Dispose();
                });
            });

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

        /// <summary>The per-symbol trade subjects (T-245): the whale-print route's fake wire.</summary>
        public ConcurrentDictionary<string, Subject<MarketTrade>> Trades { get; } = new();

        /// <summary>How many trade subscriptions the engine opened (the route's lifecycle pin).</summary>
        public int TradeSubscriptions => tradeSubscriptions;

        /// <summary>How many trade subscriptions the engine dropped.</summary>
        public int TradeUnsubscriptions => tradeUnsubscriptions;

        private int tradeSubscriptions;

        private int tradeUnsubscriptions;
    }

    private sealed class TestSound : IAlertSoundPlayer
    {
        public int Played { get; private set; }

        public bool ThrowOnPlay { get; set; }

        /// <summary>
        /// The T-220 burst facts' switch: when armed, Play() dispatches the
        /// tone off the caller's path (the real player's new shape — the
        /// call returns immediately) and the rig awaits/releases the
        /// in-flight playbacks through the fixture.
        /// </summary>
        public bool DispatchInBackground { get; set; }

        /// <summary>The dispatched playbacks nobody released (the burst fact's in-flight probe).</summary>
        private readonly ConcurrentQueue<TaskCompletionSource> _backgroundPlaybacks = new();

        public void Play()
        {
            Played++;
            if (ThrowOnPlay)
            {
                throw new InvalidOperationException("The platform tone refused to sound (test fixture).");
            }

            if (DispatchInBackground)
            {
                // The real player's T-220 shape: the caller is back
                // immediately, the tone completes in the background (the
                // rig releases it when the fact's ordering is pinned).
                _backgroundPlaybacks.Enqueue(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            }
        }

        /// <summary>Has the fixture dispatched a playback nothing waited for (the burst fact's probe)?</summary>
        public bool HasUnreleasedBackgroundPlaybacks => _backgroundPlaybacks.IsEmpty is false;

        /// <summary>Release every dispatched playback and await them (the rig's await point — the determinism the seam owes the facts).</summary>
        public async Task ReleaseBackgroundPlaybacksAsync()
        {
            var playbacks = new List<Task>();
            while (_backgroundPlaybacks.TryDequeue(out var playback))
            {
                playback.TrySetResult();
                playbacks.Add(playback.Task);
            }

            await Task.WhenAll(playbacks).ConfigureAwait(false);
        }
    }

    /// <summary>The engine's logger spy: records the level + message + exception triples (the degrade lane's probe).</summary>
    private sealed class SpyLogger : ILogger<AlertEngine>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
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

    private sealed record Rig(AlertEngine Engine, TestSettings Settings, TestAdapter Adapter, TestSound Sound, TestClock Clock, NotificationCenter Center);

    private static Rig BuildRig(
        TestSettings? settings = null,
        TestAdapter? adapter = null,
        IIndicatorCatalog? indicators = null,
        ILogger<AlertEngine>? logger = null)
    {
        settings ??= new TestSettings();
        adapter ??= new TestAdapter();
        var sound = new TestSound();
        var clock = new TestClock();
        var center = new NotificationCenter(settings, NullLogger<NotificationCenter>.Instance);
        var engine = new AlertEngine(
            settings,
            adapter,
            indicators ?? new TestCatalog(),
            sound,
            center,
            clock,
            logger ?? NullLogger<AlertEngine>.Instance);
        return new Rig(engine, settings, adapter, sound, clock, center);
    }

    private static AlertDraft PriceDraft(decimal target, AlertTrigger trigger) => new(
        SymbolName: BtcUsdt.Normalized,
        Kind: PriceAlertKind.Price,
        Trigger: trigger,
        TargetPrice: target,
        ThresholdPercent: 0m,
        WindowMinutes: 0,
        Note: string.Empty);

    private static AlertDraft ReArmingPriceDraft(
        decimal target,
        AlertTrigger trigger,
        AlertReArmMode reArm,
        int cooldownMinutes = 0,
        AlertSoundChoice sound = AlertSoundChoice.Beep) => new(
        SymbolName: BtcUsdt.Normalized,
        Kind: PriceAlertKind.Price,
        Trigger: trigger,
        TargetPrice: target,
        ThresholdPercent: 0m,
        WindowMinutes: 0,
        Note: string.Empty,
        ReArm: reArm,
        CooldownMinutes: cooldownMinutes,
        Sound: sound);

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

    private static AlertDraft FundingDraft(
        decimal thresholdPercent,
        AlertTrigger edge,
        string symbolName = "BTC/USDT") => new(
        SymbolName: symbolName,
        Kind: PriceAlertKind.FundingCross,
        Trigger: edge,
        TargetPrice: thresholdPercent,
        ThresholdPercent: 0m,
        WindowMinutes: 0,
        Note: string.Empty);

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
        adapter.Tickers.GetOrAdd(BtcUsdt.Normalized, _ => new Subject<Ticker>())
            .OnNext(new Ticker(
                BtcUsdt,
                price,
                price,
                price,
                1m,
                0m,
                occurred));

    /// <summary>The funding tick (T-240): the wire's rate rides the ticker's own funding member (nothing = the honest absence).</summary>
    private static void PushFundingTick(TestAdapter adapter, decimal? fundingRate, DateTimeOffset occurred) =>
        adapter.Tickers[BtcUsdt.Normalized].OnNext(new Ticker(
            BtcUsdt,
            100m,
            100m,
            100m,
            1m,
            0m,
            occurred,
            FundingRate: fundingRate));

    /// <summary>The whale draft (T-245): the notional threshold lives on TargetPrice in absolute quote units, the edge is the fixed Above.</summary>
    private static AlertDraft WhaleDraft(decimal notionalThreshold, string symbolName = "BTC/USDT") => new(
        SymbolName: symbolName,
        Kind: PriceAlertKind.WhalePrint,
        Trigger: AlertTrigger.Above,
        TargetPrice: notionalThreshold,
        ThresholdPercent: 0m,
        WindowMinutes: 0,
        Note: string.Empty);

    /// <summary>The trade print (T-245): served price × served size on the fake trade wire.</summary>
    private static void PushTrade(TestAdapter adapter, decimal price, decimal quantity, DateTimeOffset occurred) =>
        adapter.Trades[BtcUsdt.Normalized].OnNext(new MarketTrade
        {
            Id = $"print-{price}-{quantity}",
            Symbol = BtcUsdt,
            Side = OrderSide.Buy,
            Price = new Price(price),
            Quantity = new Quantity(quantity),
            Timestamp = occurred,
        });

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
        var soundLogger = new SpyLogger();
        var rig = BuildRig(logger: soundLogger);
        rig.Sound.ThrowOnPlay = true;
        await rig.Engine.InitializeAsync();
        await rig.Engine.CreateAsync(PriceDraft(110m, AlertTrigger.Above));
        var (census, firstFire) = WireFiredLatch(rig.Engine);

        PushTick(rig.Adapter, 111m, Base.AddMinutes(1));

        (await firstFire.Task.WaitAsync(EventWaitBudget)).Definition!.Status
            .Should().Be(AlertStatus.Fired, "a failed beep never breaks an evaluation");
        rig.Sound.Played.Should().Be(1);
        CountFires(census).Should().Be(1);
        soundLogger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("degraded away", StringComparison.Ordinal) &&
            entry.Exception is InvalidOperationException,
            "the engine's degrade lane logs the failed beep exactly as today");
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
    public async Task AFundingCrossAbove_FiresOnTheRealCross_NeverOnTheFirstRate()
    {
        var rig = BuildRig();
        await rig.Engine.InitializeAsync();
        var created = await rig.Engine.CreateAsync(FundingDraft(0.05m, AlertTrigger.Above));
        var (census, firstFire) = WireFiredLatch(rig.Engine, created);

        // The first rate-carrying tick owns no previous (the honest
        // two-sample crossing); the second pair 0.01% -> 0.06% crosses
        // above the 0.05% threshold.
        PushFundingTick(rig.Adapter, 0.0001m, Base.AddMinutes(1));
        PushFundingTick(rig.Adapter, 0.0006m, Base.AddMinutes(2));

        (await firstFire.Task.WaitAsync(EventWaitBudget)).Definition!.LastFiredUtc
            .Should().Be(Base.AddMinutes(2), "the fire stamps the crossing tick's own moment");
        CountFires(census).Should().Be(1, "the first observation never fired — no previous to cross from");
        await rig.Engine.DisposeAsync();
    }

    [Fact]
    public async Task AFundingCrossBelow_FiresOnTheMirror()
    {
        var rig = BuildRig();
        await rig.Engine.InitializeAsync();
        var created = await rig.Engine.CreateAsync(FundingDraft(-0.05m, AlertTrigger.Below));
        var (census, firstFire) = WireFiredLatch(rig.Engine, created);

        PushFundingTick(rig.Adapter, 0.0001m, Base.AddMinutes(1));
        PushFundingTick(rig.Adapter, -0.0006m, Base.AddMinutes(2));

        (await firstFire.Task.WaitAsync(EventWaitBudget)).Definition!.LastFiredUtc
            .Should().Be(Base.AddMinutes(2), "the mirror edge crosses from above to at-or-below");
        CountFires(census).Should().Be(1);
        await rig.Engine.DisposeAsync();
    }

    [Fact]
    public async Task ARatelessTick_IsTheHonestAbsence_TheTrackedPairSurvives()
    {
        var rig = BuildRig();
        await rig.Engine.InitializeAsync();
        var created = await rig.Engine.CreateAsync(FundingDraft(0.05m, AlertTrigger.Above));
        var (census, firstFire) = WireFiredLatch(rig.Engine, created);

        // The middle tick carries no rate: it contributes nothing (never a
        // fabricated zero) and the tracked previous survives it, so the
        // 0.01% -> 0.06% pair still crosses on the third tick.
        PushFundingTick(rig.Adapter, 0.0001m, Base.AddMinutes(1));
        PushFundingTick(rig.Adapter, null, Base.AddMinutes(2));
        PushFundingTick(rig.Adapter, 0.0006m, Base.AddMinutes(3));

        (await firstFire.Task.WaitAsync(EventWaitBudget)).Definition!.LastFiredUtc
            .Should().Be(Base.AddMinutes(3));
        CountFires(census).Should().Be(1, "the rateless tick neither fired nor broke the pair");
        await rig.Engine.DisposeAsync();
    }

    [Fact]
    public async Task AFundingDraft_WithTheMeaninglessAtEdge_IsRefused()
    {
        var rig = BuildRig();
        await rig.Engine.InitializeAsync();

        var act = () => rig.Engine.CreateAsync(FundingDraft(0.05m, AlertTrigger.At));

        await act.Should().ThrowAsync<ArgumentException>(
            "a cross reads only the Above or Below edge; the editor never offers what it would refuse");
        rig.Engine.Definitions.Should().BeEmpty("the refused draft never landed");
        await rig.Engine.DisposeAsync();
    }

    #region Whale Print (T-245)

    [Fact]
    public async Task AWhalePrint_FiresStrictlyAboveTheNotional_NeverAtIt()
    {
        var rig = BuildRig();
        await rig.Engine.InitializeAsync();
        await rig.Engine.CreateAsync(WhaleDraft(100_000m));
        var (census, firstFire) = WireFiredLatch(rig.Engine);

        // 100 × 1000 = 100 000 exactly: the edge is strictly above, no fire.
        PushTrade(rig.Adapter, 100m, 1000m, Base.AddMinutes(1));
        // 100 × 1000.01 = 100 001: the qualifying print.
        PushTrade(rig.Adapter, 100m, 1000.01m, Base.AddMinutes(2));

        var fired = await firstFire.Task.WaitAsync(EventWaitBudget);
        fired.Definition!.Kind.Should().Be(PriceAlertKind.WhalePrint);
        fired.Observation!.OccurredUtc.Should().Be(Base.AddMinutes(2), "the fire stamps the qualifying print's own moment");
        fired.Observation!.Quantity.Should().Be(1000.01m, "the route carries the print's served size");
        CountFires(census).Should().Be(1);
        rig.Sound.Played.Should().Be(1);
        rig.Settings.Saved[^1].Should().Contain("\"Fired\"", "the fire persisted");
        rig.Engine.Definitions.Should().ContainSingle()
            .Which.Status.Should().Be(AlertStatus.Fired);
        await rig.Engine.DisposeAsync();
    }

    [Fact]
    public async Task AWhalePrint_TheTradeRouteNeverFeedsTheOtherKinds()
    {
        var rig = BuildRig();
        await rig.Engine.InitializeAsync();
        await rig.Engine.CreateAsync(PriceDraft(110m, AlertTrigger.Above));
        var whale = await rig.Engine.CreateAsync(WhaleDraft(50_000m));
        var (census, firstFire) = WireFiredLatch(rig.Engine, whale);

        // A print whose price alone would satisfy the price level (120 > 110)
        // AND whose notional satisfies the whale: only the whale judges it —
        // a print is not a tick.
        PushTrade(rig.Adapter, 120m, 600m, Base.AddMinutes(1));

        var fired = await firstFire.Task.WaitAsync(EventWaitBudget);
        fired.Definition!.Id.Should().Be(whale.Id, "the whale is the only kind on the trade route");
        CountFires(census).Should().Be(1);
        rig.Engine.Definitions.Single(definition => definition.Kind == PriceAlertKind.Price)
            .Status.Should().Be(AlertStatus.Armed, "the price level never saw the trade row");
        await rig.Engine.DisposeAsync();
    }

    [Fact]
    public async Task AWhalePrint_TheTickerRouteNeverReachesIt()
    {
        var rig = BuildRig();
        await rig.Engine.InitializeAsync();
        var whale = await rig.Engine.CreateAsync(WhaleDraft(50_000m));
        var (census, firstFire) = WireFiredLatch(rig.Engine, whale);

        // Ticks with a huge price: a tick carries no size — it never judges
        // the whale, no matter how big the price reads.
        PushTick(rig.Adapter, 999_999m, Base.AddMinutes(1));
        PushTick(rig.Adapter, 999_999m, Base.AddMinutes(2));
        // The qualifying print AFTER the ticks: the FIFO proves the ticks
        // were processed without firing.
        PushTrade(rig.Adapter, 100m, 600m, Base.AddMinutes(3));

        var fired = await firstFire.Task.WaitAsync(EventWaitBudget);
        fired.Observation!.OccurredUtc.Should().Be(Base.AddMinutes(3), "the fire is the print's own stamp — the ticks stayed silent");
        CountFires(census).Should().Be(1);
        await rig.Engine.DisposeAsync();
    }

    [Fact]
    public async Task AWhalePrint_TheTradeSubscriptionFollowsTheRoster()
    {
        var rig = BuildRig();
        await rig.Engine.InitializeAsync();
        rig.Adapter.TradeSubscriptions.Should().Be(0, "a whale-free roster holds no trade subscription");

        var whale = await rig.Engine.CreateAsync(WhaleDraft(50_000m));
        rig.Adapter.TradeSubscriptions.Should().Be(1, "the armed whale subscribes its symbol's trade stream");
        rig.Adapter.Trades.Keys.Should().ContainSingle().Which.Should().Be("BTC/USDT");
        rig.Adapter.Tickers.Should().BeEmpty("a whale-only symbol rides the trade route alone, no ticker");

        await rig.Engine.DeleteAsync(whale.Id);
        rig.Adapter.TradeUnsubscriptions.Should().Be(1, "the delete drops the trade subscription with the roster entry");
        await rig.Engine.DisposeAsync();
    }

    [Fact]
    public async Task AZeroQuantityPrint_EvaluatesNothing_AndTheRouteSurvives()
    {
        var rig = BuildRig();
        await rig.Engine.InitializeAsync();
        await rig.Engine.CreateAsync(WhaleDraft(50_000m));
        var (census, firstFire) = WireFiredLatch(rig.Engine);

        // The wire's malformed row: a served size of zero is no trade fact —
        // the route drops it, nothing is fabricated.
        rig.Adapter.Trades[BtcUsdt.Normalized].OnNext(new MarketTrade
        {
            Id = "print-zero",
            Symbol = BtcUsdt,
            Side = OrderSide.Buy,
            Price = new Price(1_000_000m),
            Quantity = new Quantity(0m),
            Timestamp = Base.AddMinutes(1),
        });

        // A healthy print right after: the route survived the malformed row.
        PushTrade(rig.Adapter, 100m, 600m, Base.AddMinutes(2));

        var fired = await firstFire.Task.WaitAsync(EventWaitBudget);
        fired.Observation!.OccurredUtc.Should().Be(Base.AddMinutes(2), "only the healthy print evaluated");
        CountFires(census).Should().Be(1);
        await rig.Engine.DisposeAsync();
    }

    #endregion

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

    #region Re-arm mechanics (T-204)

    [Fact]
    public async Task PerCross_ReArmsToArmed_AndRefiresOnTheNextFreshCrossing()
    {
        var rig = BuildRig();
        await rig.Engine.InitializeAsync();
        var created = await rig.Engine.CreateAsync(ReArmingPriceDraft(110m, AlertTrigger.Above, AlertReArmMode.PerCross));
        var (census, firstFire) = WireFiredLatch(rig.Engine, created);

        // The unknown gate state fires on the first hit (the T-096 arming
        // semantics), then the fire re-arms to Armed.
        PushTick(rig.Adapter, 111m, Base.AddMinutes(1));
        await WaitFiresAsync(census, 1);
        var definition = rig.Engine.Definitions.Single(item => item.Id == created.Id);
        definition.Status.Should().Be(AlertStatus.Armed, "the per-cross fire re-arms immediately, never terminally");
        definition.LastFiredUtc.Should().Be(Base.AddMinutes(1));

        // The level holds above its edge: the held mark blocks every tick.
        PushTick(rig.Adapter, 111.5m, Base.AddMinutes(2));
        PushTick(rig.Adapter, 112m, Base.AddMinutes(3));
        CountFires(census).Should().Be(1, "a level the price never left is not a fresh crossing");

        // The price leaves the edge and crosses again: the fresh hit fires.
        PushTick(rig.Adapter, 105m, Base.AddMinutes(4));
        PushTick(rig.Adapter, 111m, Base.AddMinutes(5));
        await WaitFiresAsync(census, 2);
        var second = census.Where(@event => @event.Kind == AlertEventKind.Fired).Last();
        second.Definition!.LastFiredUtc.Should().Be(Base.AddMinutes(5), "the fresh crossing owns the second fire");
        second.Definition!.Status.Should().Be(AlertStatus.Armed, "the second fire re-arms the same way");
        rig.Sound.Played.Should().Be(2, "every fire of a beep alert sounds its beep");
        await rig.Engine.DisposeAsync();
    }

    [Fact]
    public async Task Cooldown_ReArmsIntoItsWindow_AndRefiresAfterIt()
    {
        var rig = BuildRig();
        await rig.Engine.InitializeAsync();
        var created = await rig.Engine.CreateAsync(ReArmingPriceDraft(
            110m,
            AlertTrigger.Above,
            AlertReArmMode.Cooldown,
            cooldownMinutes: 5));
        var (census, firstFire) = WireFiredLatch(rig.Engine, created);

        PushTick(rig.Adapter, 111m, Base.AddMinutes(1));
        await WaitFiresAsync(census, 1);
        var definition = rig.Engine.Definitions.Single(item => item.Id == created.Id);
        definition.Status.Should().Be(AlertStatus.Snoozed, "the cooldown fire re-arms into its snooze window");
        definition.SnoozedUntilUtc.Should().Be(Base.AddMinutes(6), "the window starts at the fire's own stamp");

        // The window holds the alert quiet even while the condition stays reached.
        PushTick(rig.Adapter, 112m, Base.AddMinutes(2));
        PushTick(rig.Adapter, 112m, Base.AddMinutes(4));
        CountFires(census).Should().Be(1, "the cooldown's suppression is its snooze window");

        // The expiry tick re-arms (it evaluates nothing); the next hit — the
        // gate's entry died with the fire — fires again immediately.
        PushTick(rig.Adapter, 112m, Base.AddMinutes(6));
        PushTick(rig.Adapter, 112m, Base.AddMinutes(7));
        await WaitFiresAsync(census, 2);
        var second = census.Where(@event => @event.Kind == AlertEventKind.Fired).Last();
        second.Definition!.LastFiredUtc.Should().Be(Base.AddMinutes(7));
        rig.Engine.Definitions.Single(item => item.Id == created.Id).Status
            .Should().Be(AlertStatus.Snoozed, "the second fire re-arms its own window");
        await rig.Engine.DisposeAsync();
    }

    [Fact]
    public async Task AnEdit_ClearsTheReArmGate_TheNextHitFires()
    {
        var rig = BuildRig();
        await rig.Engine.InitializeAsync();
        var created = await rig.Engine.CreateAsync(ReArmingPriceDraft(110m, AlertTrigger.Above, AlertReArmMode.PerCross));
        var (census, firstFire) = WireFiredLatch(rig.Engine, created);

        PushTick(rig.Adapter, 111m, Base.AddMinutes(1));
        await WaitFiresAsync(census, 1);

        // The edit re-arms and forgets the held mark: the next hit fires
        // even though the level never left (the arming semantics restart).
        await rig.Engine.UpdateAsync(created.Id, ReArmingPriceDraft(100m, AlertTrigger.Above, AlertReArmMode.PerCross));
        PushTick(rig.Adapter, 111m, Base.AddMinutes(2));
        await WaitFiresAsync(census, 2);
        await rig.Engine.DisposeAsync();
    }

    [Fact]
    public async Task ASilentAlert_FiresWithoutTheBeep()
    {
        var rig = BuildRig();
        await rig.Engine.InitializeAsync();
        await rig.Engine.CreateAsync(ReArmingPriceDraft(
            110m,
            AlertTrigger.Above,
            AlertReArmMode.OneShot,
            sound: AlertSoundChoice.Silent));
        var (census, firstFire) = WireFiredLatch(rig.Engine);

        PushTick(rig.Adapter, 111m, Base.AddMinutes(1));

        (await firstFire.Task.WaitAsync(EventWaitBudget)).Definition!.Status
            .Should().Be(AlertStatus.Fired, "the silent choice changes the sound alone, never the fire");
        rig.Sound.Played.Should().Be(0, "a silent alert never touches the beep seam");
        CountFires(census).Should().Be(1);
        await rig.Engine.DisposeAsync();
    }

    [Fact]
    public async Task ACooldownDraft_WithANonPositiveWindow_IsRefused()
    {
        var rig = BuildRig();
        await rig.Engine.InitializeAsync();

        var refuse = async () => await rig.Engine.CreateAsync(ReArmingPriceDraft(
            110m,
            AlertTrigger.Above,
            AlertReArmMode.Cooldown,
            cooldownMinutes: 0));
        (await refuse.Should().ThrowAsync<ArgumentException>())
            .WithMessage("*strictly positive cooldown*");

        rig.Engine.Definitions.Should().BeEmpty("a refused draft never joins the roster");
        rig.Settings.Saved.Should().BeEmpty();
        await rig.Engine.DisposeAsync();
    }

    [Fact]
    public async Task ADraft_WithAnUnknownReArmMode_IsRefused()
    {
        var rig = BuildRig();
        await rig.Engine.InitializeAsync();
        var ghostDraft = new AlertDraft(
            SymbolName: BtcUsdt.Normalized,
            Kind: PriceAlertKind.Price,
            Trigger: AlertTrigger.Above,
            TargetPrice: 110m,
            ThresholdPercent: 0m,
            WindowMinutes: 0,
            Note: string.Empty,
            ReArm: (AlertReArmMode)999);

        var refuse = async () => await rig.Engine.CreateAsync(ghostDraft);
        (await refuse.Should().ThrowAsync<ArgumentOutOfRangeException>())
            .WithMessage("*no known re-arm mode*");

        rig.Engine.Definitions.Should().BeEmpty("an unparsable enum value is an error, never a defaulted mode");
        await rig.Engine.DisposeAsync();
    }

    #endregion Re-arm mechanics (T-204)

    #region Toast bridge (T-204)

    /// <summary>How long a bridge toast may take to surface through the short test window (the same wait budget discipline).</summary>
    private static readonly TimeSpan BridgeToastWindow = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// A settle delay long enough for a toast the lane WOULD deliver to have
    /// delivered (the coalescing window plus scheduling slack under load).
    /// </summary>
    private static readonly TimeSpan BridgeSettleDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// An in-memory reactive settings double with TYPED reads (the bridge
    /// tests' DND flag and mute booleans ride it beside the engine's roster
    /// strings): writes land in the store and announce themselves on the
    /// change stream, exactly the reactive settings canon in miniature.
    /// </summary>
    private sealed class ReactiveSettings : ISettingsService
    {
        private readonly Dictionary<string, object?> _store = new(StringComparer.Ordinal);
        private readonly Subject<SettingsChange> _changes = new();

        public IObservable<SettingsChange> Changes => _changes;

        public Task<T> GetAsync<T>(string section, string key, T defaultValue = default!)
        {
            if (_store.TryGetValue($"{section}\u0001{key}", out var value) && value is T typed)
            {
                return Task.FromResult(typed);
            }

            return Task.FromResult(defaultValue);
        }

        public Task SetAsync<T>(string section, string key, T value)
        {
            _store[$"{section}\u0001{key}"] = value;
            _changes.OnNext(new SettingsChange(section, key, default, value));
            return Task.CompletedTask;
        }

        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed record BridgeRig(AlertEngine Engine, ReactiveSettings Settings, TestAdapter Adapter, TestSound Sound, TestClock Clock, NotificationCenter Center);

    private static BridgeRig BuildBridgeRig(
        ReactiveSettings? settings = null,
        TestAdapter? adapter = null,
        IIndicatorCatalog? indicators = null)
    {
        settings ??= new ReactiveSettings();
        adapter ??= new TestAdapter();
        var sound = new TestSound();
        var clock = new TestClock();
        var center = new NotificationCenter(settings, NullLogger<NotificationCenter>.Instance, BridgeToastWindow);
        var engine = new AlertEngine(
            settings,
            adapter,
            indicators ?? new TestCatalog(),
            sound,
            center,
            clock,
            NullLogger<AlertEngine>.Instance);
        return new BridgeRig(engine, settings, adapter, sound, clock, center);
    }

    [Fact]
    public async Task AFire_BridgesTheAlertToast_WithTheFactsTheRowNeeds()
    {
        var rig = BuildBridgeRig();
        var raw = new List<Notification>();
        using var rawSubscription = rig.Center.Events.Subscribe(raw.Add, ex => throw ex);
        await rig.Engine.InitializeAsync();
        var created = await rig.Engine.CreateAsync(PriceDraft(110m, AlertTrigger.Above));
        var (census, firstFire) = WireFiredLatch(rig.Engine, created);

        PushTick(rig.Adapter, 111m, Base.AddMinutes(1));
        await firstFire.Task.WaitAsync(EventWaitBudget);

        var toast = raw.Should().ContainSingle(n => n.Kind == NotificationKind.AlertFired).Subject;
        toast.SymbolName.Should().Be(BtcUsdt.Normalized);
        toast.Quantity.Should().Be(111m, "the fired observation's price is the toast's structured fact");
        toast.OccurredUtc.Should().Be(Base.AddMinutes(1));
        toast.Route.Should().Be(NotificationRoute.None, "a fire toast's click only dismisses");
        toast.Alert.Should().NotBeNull("the row composes the alert's own condition line from the definition");
        toast.Alert!.Id.Should().Be(created.Id);
        rig.Center.HistorySnapshot.Should().Contain(toast, "the raw publish lands the history row first");
        CountFires(census).Should().Be(1);
        await rig.Engine.DisposeAsync();
    }

    [Fact]
    public async Task AFire_ToastsWhenTheLaneIsFree_AndReachesTheDisplayLane()
    {
        var rig = BuildBridgeRig();
        var toasts = new List<Notification>();
        using var toastsSubscription = rig.Center.Toasts.Subscribe(toasts.Add, ex => throw ex);
        await rig.Engine.InitializeAsync();
        await rig.Engine.CreateAsync(PriceDraft(110m, AlertTrigger.Above));
        var (census, firstFire) = WireFiredLatch(rig.Engine);

        PushTick(rig.Adapter, 111m, Base.AddMinutes(1));
        await firstFire.Task.WaitAsync(EventWaitBudget);

        var deadline = DateTimeOffset.UtcNow + EventWaitBudget;
        while (toasts.Count == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        toasts.Should().ContainSingle("the unmuted, non-DND fire toasts");
        toasts[0].Kind.Should().Be(NotificationKind.AlertFired);
        toasts[0].Alert.Should().NotBeNull();
        CountFires(census).Should().Be(1);
        await rig.Engine.DisposeAsync();
    }

    [Fact]
    public async Task AFire_DuringDoNotDisturb_KeepsItsHistoryRow_AndSkipsTheToast()
    {
        var settings = new ReactiveSettings();
        await settings.SetAsync(NotificationSettingsKeys.Section, NotificationSettingsKeys.DoNotDisturb, true);
        var rig = BuildBridgeRig(settings);
        var toasts = new List<Notification>();
        using var toastsSubscription = rig.Center.Toasts.Subscribe(toasts.Add, ex => throw ex);
        await rig.Engine.InitializeAsync();
        await rig.Engine.CreateAsync(PriceDraft(110m, AlertTrigger.Above));
        var (census, firstFire) = WireFiredLatch(rig.Engine);

        PushTick(rig.Adapter, 111m, Base.AddMinutes(1));
        await firstFire.Task.WaitAsync(EventWaitBudget);
        await Task.Delay(BridgeSettleDelay);

        toasts.Should().BeEmpty("the active Do-Not-Disturb window swallows the toast");
        rig.Center.HistorySnapshot.Should().ContainSingle(n => n.Kind == NotificationKind.AlertFired, "the fire still records its honest history row");
        CountFires(census).Should().Be(1, "the display suppression never touches the fire itself");
        await rig.Engine.DisposeAsync();
    }

    [Fact]
    public async Task TheMutedAlertSource_SkipsTheToast_AndHistoryKeepsRecording()
    {
        var settings = new ReactiveSettings();
        await settings.SetAsync(NotificationSettingsKeys.Section, NotificationSettingsKeys.MuteAlerts, true);
        var rig = BuildBridgeRig(settings);
        var toasts = new List<Notification>();
        using var toastsSubscription = rig.Center.Toasts.Subscribe(toasts.Add, ex => throw ex);
        await rig.Engine.InitializeAsync();
        await rig.Engine.CreateAsync(PriceDraft(110m, AlertTrigger.Above));
        var (census, firstFire) = WireFiredLatch(rig.Engine);

        PushTick(rig.Adapter, 111m, Base.AddMinutes(1));
        await firstFire.Task.WaitAsync(EventWaitBudget);
        await Task.Delay(BridgeSettleDelay);

        toasts.Should().BeEmpty("the alert-fire mute holds the display lane for the source");
        rig.Center.HistorySnapshot.Should().ContainSingle(n => n.Kind == NotificationKind.AlertFired, "a mute silences the popup, never the record");
        CountFires(census).Should().Be(1);
        await rig.Engine.DisposeAsync();
    }

    [Fact]
    public async Task TheDoNotDisturbFlipBack_CountsTheMissedFire_InExactlyOneSummary()
    {
        var settings = new ReactiveSettings();
        await settings.SetAsync(NotificationSettingsKeys.Section, NotificationSettingsKeys.DoNotDisturb, true);
        var rig = BuildBridgeRig(settings);
        var toasts = new List<Notification>();
        using var toastsSubscription = rig.Center.Toasts.Subscribe(toasts.Add, ex => throw ex);
        await rig.Engine.InitializeAsync();
        await rig.Engine.CreateAsync(PriceDraft(110m, AlertTrigger.Above));
        var (_, firstFire) = WireFiredLatch(rig.Engine);

        PushTick(rig.Adapter, 111m, Base.AddMinutes(1));
        await firstFire.Task.WaitAsync(EventWaitBudget);
        await settings.SetAsync(NotificationSettingsKeys.Section, NotificationSettingsKeys.DoNotDisturb, false);

        var deadline = DateTimeOffset.UtcNow + EventWaitBudget;
        while (!toasts.Any(n => n.Kind == NotificationKind.DoNotDisturbSummary) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        toasts.Should().ContainSingle("the flip back emits exactly one summary for the missed span");
        toasts[0].Kind.Should().Be(NotificationKind.DoNotDisturbSummary);
        toasts[0].Count.Should().Be(1, "exactly one fire was missed");
        await rig.Engine.DisposeAsync();
    }

    #endregion Toast bridge (T-204)

    #region The non-blocking beep (T-220)

    [Fact]
    public async Task ABeepBurst_TheNextEvaluationSurfacesWhileTheFirstPlaybackIsStillInFlight()
    {
        var rig = BuildRig();
        rig.Sound.DispatchInBackground = true;
        await rig.Engine.InitializeAsync();
        var first = await rig.Engine.CreateAsync(ReArmingPriceDraft(110m, AlertTrigger.Above, AlertReArmMode.OneShot));
        var second = await rig.Engine.CreateAsync(ReArmingPriceDraft(111m, AlertTrigger.Above, AlertReArmMode.OneShot));
        var (census, _) = WireFiredLatch(rig.Engine);

        // The burst: the first fire dispatches its tone, the second fires
        // while the first playback is still parked — the consumer never
        // waits for the beep (the T-220 acceptance; the parked playback
        // stands in for any tone duration, the kernel32 180 ms included —
        // an engine that ever joined the playback would hang this fact
        // into the budget's timeout instead).
        PushTick(rig.Adapter, 111m, Base.AddMinutes(1));
        await WaitFiresAsync(census, 1);
        PushTick(rig.Adapter, 112m, Base.AddMinutes(2));
        await WaitFiresAsync(census, 2);
        var secondFire = census.Where(@event => @event.Kind == AlertEventKind.Fired).Last();
        secondFire.Definition!.Id.Should().Be(second.Id, "the second evaluation surfaced behind the burst's first fire");
        rig.Sound.HasUnreleasedBackgroundPlaybacks.Should().BeTrue(
            "the first playback was never awaited — the second fire surfaced while it was still in flight");
        rig.Sound.Played.Should().Be(2, "every fire of a beep alert dispatches its tone");

        // The rig awaits the dispatched playbacks (the determinism the seam owes the facts).
        await rig.Sound.ReleaseBackgroundPlaybacksAsync();
        await rig.Engine.DisposeAsync();
    }

    #endregion The non-blocking beep (T-220)

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

    /// <summary>
    /// Wait until the census counts the named number of fires: the FIFO
    /// consumer delivers one fire per evaluation, so a multi-fire fact polls
    /// the census within the same budget instead of assuming timing.
    /// </summary>
    private static async Task WaitFiresAsync(ConcurrentQueue<AlertEvent> census, int count)
    {
        var deadline = DateTimeOffset.UtcNow + EventWaitBudget;
        while (CountFires(census) < count)
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"the engine never reached {count} fire(s) within the budget");
            }

            await Task.Delay(10);
        }
    }

    private static int CountFires(IEnumerable<AlertEvent> census) =>
        census.Count(@event => @event.Kind == AlertEventKind.Fired);
}
