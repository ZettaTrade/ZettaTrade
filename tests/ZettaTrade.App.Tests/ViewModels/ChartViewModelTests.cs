namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using FluentAssertions;
using Livery.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Export;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>The half-day-agnostic status byte ReactiveUI.Primitives names RxVoid.</summary>
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// Chart ViewModel behavior on fake market data adapters without any
/// activation path (the workspace fact: WhenActivated never fires for
/// nested-panel DataContexts): the bootstrap runs at construction, the
/// selection drives the raw bars machine (Switch cancels the previous load)
/// and an exchange switch re-targets both the symbols bootstrap and the bars
/// load at the new adapter, cancelling the in-flight load of the previous
/// one. UI-thread hopping is not exercised — the assignments land through
/// ObserveOnUiThread, which a test process does not pump; every fact below
/// asserts only marshal-invariant state (the adapter's call records and the
/// raw Rx paths), and the marshalled deliveries are covered by the real
/// dispatcher in the acceptance probe. The preselection DECISION is pinned
/// in its pure form via <see cref="ChartViewModel.Preselect"/>, and the
/// bootstrap apply is landed synchronously through
/// <see cref="ChartViewModel.ApplyBootstrapForFactsAsync"/> where a fact
/// needs the exchange's list on the VM.
/// </summary>
/// <remarks>
/// Runs inside <see cref="AvaloniaDispatcherSerialCollection"/> (the
/// process-wide Avalonia dispatcher pin this class's static constructor
/// establishes must stay exclusive).
/// </remarks>
[Collection("AvaloniaDispatcherSerial")]
public sealed class ChartViewModelTests
{
    #region Fake Adapter

    /// <summary>
    /// Market data fake with a configurable identity, symbol list and
    /// per-symbol bars. Bars loads delay by <see cref="_barDelayMs"/> and
    /// honour the token, so the Rx Switch cancellation of a previous load is
    /// observable.
    /// </summary>
    private sealed class FakeMarketData : IMarketDataAdapter
    {
        private readonly int _barDelayMs;
        private readonly List<Symbol> _failingSymbols;
        private readonly IReadOnlyList<Symbol> _symbolList;
        private readonly decimal _btcClose;

        public FakeMarketData(
            int barDelayMs = 0,
            IEnumerable<string>? failingSymbols = null,
            string id = "fake",
            string name = "Fake",
            IReadOnlyList<string>? symbolNames = null,
            decimal btcClose = 100m)
        {
            _barDelayMs = barDelayMs;
            _failingSymbols = (failingSymbols ?? []).Select(Symbol.Parse).ToList();
            _symbolList = symbolNames is null
                ? [Symbol.Parse("BTC/USDT"), Symbol.Parse("ETH/BTC"), Symbol.Parse("SOL/USDC")]
                : symbolNames.Select(Symbol.Parse).ToList();
            _btcClose = btcClose;
            Id = id;
            Name = name;
        }

        public List<Symbol> SymbolsRequested { get; } = new();

        public List<Symbol> BarsRequested { get; } = new();

        public List<TimeFrame> BarsTimeFrames { get; } = new();

        public List<CancellationToken> BarsCancellationTokens { get; } = new();

        /// <summary>The request window (from/to/limit) of every bars load,
        /// in call order: the paging facts verify the fetch shape against
        /// the delivered series' head.</summary>
        public List<BarsWindow> BarsWindowRequests { get; } = new();

        /// <summary>One request window record: the adapter's from/to/limit.</summary>
        public sealed record BarsWindow(DateTimeOffset From, DateTimeOffset? To, int Limit);

        public string Id { get; }

        public string Name { get; }

        public bool IsConnected => true;

        public ExchangeCapabilities Capabilities => ExchangeCapabilities.MarketData;

        /// <summary>
        /// Optional gate for the symbols fetch: while set, the fetch hangs
        /// — a fact parks a bootstrap inside its persisted-read → fetch
        /// window and lands the mid-flight pick exactly there (BUG-016).
        /// </summary>
        public TaskCompletionSource? SymbolsGate { get; set; }

        public async Task<IReadOnlyList<Symbol>> GetSymbolsAsync(CancellationToken ct = default)
        {
            SymbolsRequested.Add(Symbol.Parse("BTC/USDT"));

            if (SymbolsGate is { } gate)
            {
                await gate.Task;
            }

            return _symbolList;
        }

        public async Task<IReadOnlyList<Bar>> GetBarsAsync(
            Symbol symbol,
            TimeFrame timeFrame,
            DateTimeOffset from,
            DateTimeOffset? to = null,
            int limit = 500,
            CancellationToken ct = default)
        {
            BarsWindowRequests.Add(new BarsWindow(from, to, limit));
            BarsRequested.Add(symbol);
            BarsTimeFrames.Add(timeFrame);
            BarsCancellationTokens.Add(ct);

            if (_failingSymbols.Contains(symbol))
            {
                throw new InvalidOperationException("no bars for this symbol");
            }

            if (_barDelayMs > 0)
            {
                await Task.Delay(_barDelayMs, ct);
            }

            // The standing behavior: one bar at the requested start edge.
            // The symbol is marked in the close so delivered bars are verified
            // by value, not by timing.
            var close = CloseFor(symbol);

            return [new Bar(from, close - 1, close + 1, close - 2, close, 3)];
        }

        private decimal CloseFor(Symbol symbol) =>
            symbol.Normalized switch
            {
                "BTC/USDT" => _btcClose,
                "ETH/BTC" => 200m,
                _ => 300m,
            };

        #region Unused Market Data Surface

        public IObservable<Ticker> SubscribeTicker(Symbol symbol) => Observable.Never<Ticker>();

        public IObservable<MarketTrade> SubscribeTrades(Symbol symbol) => Observable.Never<MarketTrade>();

        public IObservable<OrderBook> SubscribeOrderBook(Symbol symbol, int depth = 20) =>
            Observable.Never<OrderBook>();

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        #endregion
    }

    #endregion

    #region Composition

    /// <summary>
    /// An ICandleCache fake for the chart facts: every read serves the
    /// pre-written page (deterministically, inline), every write-through is
    /// recorded, and both arms can fault on demand — the cache-void and
    /// fault worlds are explicit facts, never implicit substitutes.
    /// </summary>
    private sealed class FakeCandleCache : ICandleCache
    {
        #region Private Fields

        /// <summary>The delivery sequence markers (the write-through facts),
        /// synchronized with the facts' own delivery markers.</summary>
        private readonly IReadOnlyList<Bar> _served;

        #endregion Private Fields

        #region Properties

        /// <summary>When set, every cache read faults (the chart must keep
        /// the fetch page as its only delivery).</summary>
        public Exception? ReadFault { get; set; }

        /// <summary>When set, every write-through faults (the load must
        /// still deliver its page — the cache is bookkeeping, not truth).</summary>
        public Exception? WriteFault { get; set; }

        /// <summary>The recorded write-throughs, in call order.</summary>
        public List<WriteThroughStamp> Writes { get; } = new();

        /// <summary>The arm markers appended at every cache call: the
        /// ordering facts read this together with their own delivery log.</summary>
        public ConcurrentQueue<string> Sequence { get; } = new();

        #endregion Properties

        #region Records

        /// <summary>One recorded write-through: the key triple and the bars.</summary>
        public sealed record WriteThroughStamp(string AdapterId, string Symbol, TimeFrame TimeFrame, IReadOnlyList<Bar> Bars);

        #endregion Records

        #region Constructors

        public FakeCandleCache(IReadOnlyList<Bar> served) => _served = served;

        #endregion Constructors

        #region Public Methods

        public Task<IReadOnlyList<Bar>> GetLatestAsync(
            string adapterId,
            Symbol symbol,
            TimeFrame timeFrame,
            int limit,
            CancellationToken ct = default)
        {
            Sequence.Enqueue("cache-read");
            if (ReadFault is { } fault)
            {
                return Task.FromException<IReadOnlyList<Bar>>(fault);
            }

            return Task.FromResult(_served);
        }

        public Task WriteThroughAsync(
            string adapterId,
            Symbol symbol,
            TimeFrame timeFrame,
            IReadOnlyList<Bar> bars,
            CancellationToken ct = default)
        {
            Sequence.Enqueue("cache-write");
            Writes.Add(new WriteThroughStamp(adapterId, symbol.Normalized, timeFrame, bars));
            if (WriteFault is { } fault)
            {
                return Task.FromException(fault);
            }

            return Task.CompletedTask;
        }

        #endregion Public Methods
    }

    private static ActiveExchangeService CreateService(FakeMarketData first, FakeMarketData second)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
        return new ActiveExchangeService(
            [first, second],
            settings,
            NullLogger<ActiveExchangeService>.Instance);
    }

    private static ChartViewModel CreateChart(
        ActiveExchangeService service,
        ISettingsService? settings = null,
        ICsvExportService? export = null,
        ILocalizationManager? localization = null,
        ICandleCache? candleCache = null)
    {
        var timeFrameSettings = settings;
        if (timeFrameSettings is null)
        {
            timeFrameSettings = Substitute.For<ISettingsService>();
            timeFrameSettings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(string.Empty);
            timeFrameSettings.SetAsync(
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<string>())
                .Returns(Task.CompletedTask);
        }

        return new(
            service,
            new SymbolListProvider(service, NullLogger<SymbolListProvider>.Instance),
            timeFrameSettings,
            new EmptyIndicatorCatalog(),
            export ?? Substitute.For<ICsvExportService>(),
            localization ?? Substitute.For<ILocalizationManager>(),
            // The cache-void default: an empty cached page, silent writes —
            // the facts that never touch the cache stay the pre-T-085 world.
            candleCache ?? new FakeCandleCache([]),
            NullLogger<ChartViewModel>.Instance);
    }

    /// <summary>
    /// The catalog stub the chart facts run against: no plugin resolves,
    /// so the overlay publishes stay empty and every pre-existing fact is
    /// untouched by the indicator seam (the pipeline facts cover the
    /// computing paths with their own fakes).
    /// </summary>
    private sealed class EmptyIndicatorCatalog : IIndicatorCatalog
    {
        public IReadOnlyCollection<string> LoadedNames { get; } = [];

        public IIndicator? Create(string name, int period) => null;
    }

    #endregion

    #region Tests

    /// <summary>
    /// The chart facts are UI-marshal invariant by contract: the bootstrap
    /// apply rides <c>ObserveOnUiThread</c>, and the process-wide dispatcher
    /// ownership is claimed ONCE by <see cref="AvaloniaDispatcherPump"/> —
    /// every test thread is off-owner, so the apply mode is deterministically
    /// "posted and never pumped" (an xUnit method can otherwise run on any
    /// thread pool thread, making the same facts order-dependent). Nothing
    /// in these facts drains the dispatcher; the pre-drain frozen state is
    /// every assert's world.
    /// </summary>
    static ChartViewModelTests() => AvaloniaDispatcherPump.EnsureInstalled();

    [Fact]
    public void Ctor_StartsTheSymbolsLoadWithoutActivation()
    {
        var marketData = new FakeMarketData();
        var chart = CreateChart(CreateService(marketData, new FakeMarketData(id: "fake2", name: "Fake2")));

        marketData.SymbolsRequested.Should().ContainSingle(
            "the bootstrap runs in the constructor, no WhenActivated involved");
    }

    [Fact]
    public void Selections_FlowThroughTheRawStream()
    {
        var chart = CreateChart(CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")));
        var selections = new List<Symbol>();

        using var subscription = chart.SymbolSelections.Subscribe(selections.Add);

        chart.SelectedSymbol = Symbol.Parse("ETH/BTC");
        chart.SelectedSymbol = Symbol.Parse("SOL/USDC");

        selections.Should().BeEquivalentTo(
            [Symbol.Parse("ETH/BTC"), Symbol.Parse("SOL/USDC")],
            options => options.WithStrictOrdering());
    }

    /// <summary>
    /// The settings save that hides the pair whose chart is shown switches
    /// the chart to the first visible pair and persists the switch: the live
    /// marks change lands through the synchronous changes stream, the rebuild
    /// drops the hidden row outright, and the rebase assignment rides the
    /// selection stream into the workspace settings key (a restart never
    /// re-opens the hidden chart either).
    /// </summary>
    [Fact]
    public async Task TheHiddenShownChart_RebasesToTheFirstVisiblePair_OnTheSave()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
        settings.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);
        var changes = new Subject<SettingsChange>();
        settings.Changes.Returns(changes);

        var marketData = new FakeMarketData();
        var chart = CreateChart(
            CreateService(marketData, new FakeMarketData(id: "fake2", name: "Fake2")),
            settings);

        // The posted bootstrap apply is never pumped (foreign-owned test
        // dispatcher): the fact lands it through the facts seam instead.
        await chart.StartBootstrapForFactsAsync(marketData);

        chart.SelectedSymbol.Should().Be(
            Symbol.Parse("BTC/USDT"),
            "the empty workspace preference falls to the first visible entry");
        chart.AvailableSymbols.Select(row => row.Symbol.Normalized).Should()
            .Equal("BTC/USDT", "ETH/BTC", "SOL/USDC");

        // The user hides the SHOWN pair in the settings pairs tab and saves.
        changes.OnNext(new SettingsChange(
            UiSettingsKeys.WorkspaceSection,
            UiSettingsKeys.WorkspaceHiddenSymbols,
            null,
            """{"fake":["BTC/USDT"]}"""));

        chart.SelectedSymbol.Should().Be(
            Symbol.Parse("ETH/BTC"),
            "the hidden shown chart re-bases to the first visible pair");
        chart.AvailableSymbols.Select(row => row.Symbol.Normalized).Should().Equal("ETH/BTC", "SOL/USDC");
        await settings.Received(1).SetAsync(
            UiSettingsKeys.WorkspaceSection,
            UiSettingsKeys.WorkspaceSymbolId,
            "ETH/BTC");
    }

    [Fact]
    public void BarsUpdates_DeliverTheLatestSelection()
    {
        var marketData = new FakeMarketData();
        var chart = CreateChart(CreateService(marketData, new FakeMarketData(id: "fake2", name: "Fake2")));
        var delivered = new List<IReadOnlyList<Bar>>();

        using var subscription = chart.BarsUpdates.Subscribe(delivered.Add);

        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");

        marketData.BarsRequested.Should().ContainSingle().Which.Should().Be(Symbol.Parse("BTC/USDT"));
        delivered.Should().ContainSingle();
        delivered[0].Should().ContainSingle().Which.Close.Should().Be(100m);
    }

    [Fact]
    public async Task FastSwitch_CancelsThePreviousBarsLoad()
    {
        var marketData = new FakeMarketData(barDelayMs: 30);
        var chart = CreateChart(CreateService(marketData, new FakeMarketData(id: "fake2", name: "Fake2")));
        var latest = new TaskCompletionSource<IReadOnlyList<Bar>>(TaskCreationOptions.RunContinuationsAsynchronously);

        // At delivery time the winning load's lease must still be alive; a
        // completed load ends its own token later (Rx detaches on completion).
        var winnerCancelledAtDelivery = default(bool?);
        using var subscription = chart.BarsUpdates.Subscribe(
            bars =>
            {
                winnerCancelledAtDelivery = marketData.BarsCancellationTokens[1].IsCancellationRequested;
                latest.TrySetResult(bars);
            });

        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        chart.SelectedSymbol = Symbol.Parse("ETH/BTC");

        var winnerBars = await latest.Task.WaitAsync(TimeSpan.FromSeconds(5));

        winnerBars.Should().ContainSingle().Which.Close.Should().Be(200m);

        // The Switch dropped the BTC load: its token is cancelled and its bars
        // never win the race.
        marketData.BarsRequested.Should().HaveCount(2);
        marketData.BarsCancellationTokens[0].IsCancellationRequested.Should().BeTrue(
            "the previous load is cancelled with the switch");
        winnerCancelledAtDelivery.Should().BeFalse(
            "the winning load is still alive when its bars arrive");
    }

    [Fact]
    public void BarsLoadFailure_KeepsTheMachineAlive()
    {
        var marketData = new FakeMarketData(failingSymbols: ["BTC/USDT"]);
        var chart = CreateChart(CreateService(marketData, new FakeMarketData(id: "fake2", name: "Fake2")));
        var delivered = new List<IReadOnlyList<Bar>>();

        using var subscription = chart.BarsUpdates.Subscribe(delivered.Add);

        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        chart.SelectedSymbol = Symbol.Parse("ETH/BTC");

        marketData.BarsRequested.Should().HaveCount(2, "both selections attempted a load");
        delivered.Should().ContainSingle(
            "only the healthy symbol delivers bars; the failure must not end the machine");
        delivered[0].Should().ContainSingle().Which.Close.Should().Be(200m);
    }

    /// <summary>The open time of the cached stub page's single bar.</summary>
    private static readonly DateTimeOffset CachedBaseTime = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The T-085 bootstrap shape: the cached page delivers FIRST (rendered
    /// instantly on a restart) and the network refresh rides behind it; the
    /// served set persists write-through BEFORE the refresh page delivers.
    /// </summary>
    [Fact]
    public async Task BarsLoad_ServesTheCachedPageFirst_AndTheRefreshRidesWriteThroughBehind()
    {
        var marketData = new FakeMarketData(barDelayMs: 50);
        var cached = new Bar[] { new(CachedBaseTime, 91m, 97m, 90m, 94m, 12.5m) };
        var cache = new FakeCandleCache(cached);
        var chart = CreateChart(
            CreateService(marketData, new FakeMarketData(id: "fake2", name: "Fake2")),
            candleCache: cache);
        var delivered = new List<IReadOnlyList<Bar>>();
        var refresh = new TaskCompletionSource<IReadOnlyList<Bar>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writesAtRefreshDelivery = 0;

        using var subscription = chart.BarsUpdates.Subscribe(
            bars =>
            {
                delivered.Add(bars);
                if (delivered.Count == 2)
                {
                    writesAtRefreshDelivery = cache.Writes.Count;
                    refresh.TrySetResult(bars);
                }
            });

        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");

        var refreshed = await refresh.Task.WaitAsync(TimeSpan.FromSeconds(5));

        delivered.Should().HaveCount(2, "the cache page and the refresh page both deliver");
        delivered[0].Should().BeEquivalentTo(cached, "the cached page renders instantly");
        delivered[1].Should().BeEquivalentTo(refreshed, "the refresh page is the trailing delivery");
        refreshed[0].Close.Should().Be(100m, "the refresh page is the adapter's own bars, not the cache's");
        writesAtRefreshDelivery.Should().Be(1, "the write-through completed before the refresh page delivered");
        cache.Writes.Should().ContainSingle("the served set rides write-through");
        cache.Writes[0].AdapterId.Should().Be("fake");
        cache.Writes[0].Symbol.Should().Be("BTC/USDT");
        cache.Writes[0].TimeFrame.Should().Be(TimeFrame.H1);
        cache.Writes[0].Bars.Should().BeEquivalentTo(refreshed, "the persisted set is exactly the delivered one");
    }

    /// <summary>
    /// An offline-style start: the cache serves and the fetch fails — the
    /// cached bars stay the chart's world and nothing writes through.
    /// </summary>
    [Fact]
    public void BarsLoad_WhenTheFetchFails_TheCachedBarsStand()
    {
        var marketData = new FakeMarketData(failingSymbols: ["BTC/USDT"]);
        var cached = new Bar[] { new(CachedBaseTime, 91m, 97m, 90m, 94m, 12.5m) };
        var cache = new FakeCandleCache(cached);
        var chart = CreateChart(
            CreateService(marketData, new FakeMarketData(id: "fake2", name: "Fake2")),
            candleCache: cache);
        var delivered = new List<IReadOnlyList<Bar>>();

        using var subscription = chart.BarsUpdates.Subscribe(delivered.Add);

        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");

        delivered.Should().ContainSingle("the failing fetch ends its own data, not the cached delivery");
        delivered[0].Should().BeEquivalentTo(cached, "the cached page stands as the chart's world");
        cache.Writes.Should().BeEmpty("nothing the adapter served is persisted");
        marketData.BarsRequested.Should().ContainSingle("the fetch was still attempted");
    }

    /// <summary>
    /// A faulted cache never breaks the chart: a read fault leaves the
    /// fetch page as the only delivery, a write fault still records the
    /// attempt and delivers the page — the cache is bookkeeping, never a
    /// source of truth.
    /// </summary>
    [Fact]
    public void BarsLoad_WhenTheCacheFaults_TheFetchPageStillDelivers()
    {
        var marketData = new FakeMarketData();
        var cache = new FakeCandleCache([])
        {
            ReadFault = new InvalidOperationException("cache read failed"),
            WriteFault = new InvalidOperationException("cache write failed"),
        };
        var chart = CreateChart(
            CreateService(marketData, new FakeMarketData(id: "fake2", name: "Fake2")),
            candleCache: cache);
        var delivered = new List<IReadOnlyList<Bar>>();

        using var subscription = chart.BarsUpdates.Subscribe(delivered.Add);

        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");

        delivered.Should().ContainSingle("a read fault serves nothing, a write fault delivers anyway");
        delivered[0].Should().ContainSingle().Which.Close.Should().Be(100m);
        cache.Writes.Should().ContainSingle("the faulting write-through still records the served set");
        marketData.BarsRequested.Should().ContainSingle("the load survived both cache faults");
    }

    [Fact]
    public async Task ExchangeSwitch_MidLoad_CancelsThePreviousAdapterAndDeliversTheNewOne()
    {
        var slow = new FakeMarketData(barDelayMs: 60);
        var fast = new FakeMarketData(barDelayMs: 0, id: "fake2", name: "Fake2", btcClose: 777m);
        var service = CreateService(slow, fast);
        var chart = CreateChart(service);
        var delivered = new TaskCompletionSource<IReadOnlyList<Bar>>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = chart.BarsUpdates.Subscribe(bars => delivered.TrySetResult(bars));

        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        await Task.Delay(20); // let the slow load actually start

        // Act: switch to the second exchange while the slow load is in flight.
        await service.SwitchToAsync("fake2");

        var bars = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        bars.Should().ContainSingle().Which.Close.Should().Be(
            777m,
            "the delivered bars belong to the new exchange");
        slow.BarsRequested.Should().ContainSingle().Which.Should().Be(Symbol.Parse("BTC/USDT"));
        fast.BarsRequested.Should().ContainSingle().Which.Should().Be(Symbol.Parse("BTC/USDT"));
        slow.BarsCancellationTokens[0].IsCancellationRequested.Should().BeTrue(
            "the Switch dropped the in-flight load of the previous exchange");
    }

    [Fact]
    public async Task ExchangeSwitch_ReloadsSymbolsAndKeepsTheListedSelection()
    {
        var first = new FakeMarketData();
        var second = new FakeMarketData(id: "fake2", name: "Fake2");
        var service = CreateService(first, second);
        var chart = CreateChart(service);

        second.SymbolsRequested.Should().BeEmpty("the bootstrap belongs to the active exchange before the switch");

        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");

        // Act
        await service.SwitchToAsync("fake2");

        // Assert
        second.SymbolsRequested.Should().ContainSingle("the symbol bootstrap re-runs per exchange change");
        second.BarsRequested.Should().ContainSingle().Which.Should().Be(
            Symbol.Parse("BTC/USDT"),
            "the listed selection survives and reloads bars on the new exchange");
    }

    /// <summary>
    /// BUG-016, the reversed completions: a bootstrap whose epoch a newer
    /// switch already superseded delivers LATE and must drop — nothing of
    /// the OLD exchange lands after the NEW one's list and preselection
    /// (the pre-fix apply took whatever completion arrived last).
    /// </summary>
    [Fact]
    public async Task BootstrapSymbols_AStaleDelivery_NeverClobbersTheNewestExchange()
    {
        var standard = new FakeMarketData();
        var exotic = new FakeMarketData(
            id: "fake2",
            name: "Fake2",
            symbolNames: ["DOGE/USDT", "LTC/USDT"]);
        var service = CreateService(standard, exotic);
        var chart = CreateChart(service);

        // The REAL switch: the provider cache re-targets the new exchange
        // and the switch's own bootstrap starts (its delivery rides the
        // marshalled hop — inert in a test process — with the epoch it
        // claimed at the start).
        await service.SwitchToAsync("fake2");

        // The newest bootstrap (the fact's own start after the switch)
        // lands: the exotic list owns the combo, latestEpoch is its claim.
        var latestEpoch = await chart.StartBootstrapForFactsAsync(exotic);
        chart.AvailableSymbols.Select(row => row.Symbol.Normalized).Should()
            .Equal(new[] { "DOGE/USDT", "LTC/USDT" });

        // A delivery claiming the PREVIOUS epoch — exactly the switch's
        // bootstrap whose fetch outlived this newer start — is stale and
        // drops; the standard list never lands.
        await chart.ApplyBootstrapForFactsAsync(standard, epoch: latestEpoch - 1);

        chart.AvailableSymbols.Select(row => row.Symbol.Normalized).Should()
            .Equal(
                new[] { "DOGE/USDT", "LTC/USDT" },
                "the stale delivery is dropped, so nothing of the old exchange lands");
        chart.SelectedSymbol.Should().Be(
            Symbol.Parse("DOGE/USDT"),
            "the newest exchange's preselection stands");
    }

    /// <summary>
    /// BUG-016, the mid-flight pick: with the persisted preference already
    /// read and the symbol fetch parked, a user pick lands INSIDE the
    /// bootstrap's window — the read-before-fetch preference is deferred
    /// (it stays stored) and the delivery keeps the explicit pick. The
    /// pre-fix load let the stale preference decide (it is still listed)
    /// and overwrote the user's pick.
    /// </summary>
    [Fact]
    public async Task BootstrapSymbols_AUserPickDuringTheFetch_SupersedesThePersistedPreference()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("ETH/BTC");
        settings.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);

        var marketData = new FakeMarketData();

        // The gate is in place BEFORE the construction: the constructor's
        // own bootstrap parks inside its fetch (its persisted preference is
        // already read), so the whole fetch window stays open for the fact.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        marketData.SymbolsGate = gate;

        var chart = CreateChart(CreateService(marketData, new FakeMarketData(id: "fake2", name: "Fake2")), settings);

        // A real load against the parked window: its persisted preference
        // read completes synchronously, then the symbols fetch hangs on the
        // same gate.
        var decision = chart.LoadBootstrapForFactsAsync(marketData);

        // The USER picks while the fetch is in flight (the pick persists —
        // but the delivery must not honor a substitution of it).
        chart.SelectedSymbol = Symbol.Parse("SOL/USDC");

        gate.SetResult();
        var bootstrap = await decision;

        bootstrap.PersistedSymbol.Should().Be(
            Symbol.Parse("ETH/BTC"),
            "the raw read stays recorded for the warnings");

        bootstrap.PersistedDeferred.Should().BeTrue(
            "the pick raced the fetch; the preference is deferred, not a fall-through");

        bootstrap.Selection.Should().Be(
            Symbol.Parse("SOL/USDC"),
            "an explicit pick beats the read-before-fetch preference");
    }

    /// <summary>
    /// The complement of the mid-flight pick: without one racing the fetch,
    /// the persisted preference keeps its first choice (the deferral is
    /// scoped to the pick, never a blanket suppression).
    /// </summary>
    [Fact]
    public async Task BootstrapSymbols_ThePersistedPreference_WeighsWhenNoPickRacedTheFetch()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("ETH/BTC");
        settings.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);

        var chart = CreateChart(CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")), settings);

        var bootstrap = await chart.LoadBootstrapForFactsAsync(new FakeMarketData());

        bootstrap.PersistedDeferred.Should().BeFalse("no pick raced the fetch");
        bootstrap.Selection.Should().Be(
            Symbol.Parse("ETH/BTC"),
            "the persisted preference stays first choice while the list carries it");
    }

    [Fact]
    public void Preselect_DecidesTheSelectionForTheNextExchange()
    {
        var listed = new[] { Symbol.Parse("BTC/USDT"), Symbol.Parse("ETH/BTC") };
        var empty = Array.Empty<Symbol>();

        // Still listed: the user's context survives the switch.
        ChartViewModel.Preselect(Symbol.Parse("BTC/USDT"), null, listed)
            .Should().Be(Symbol.Parse("BTC/USDT"));
        ChartViewModel.Preselect(Symbol.Parse("ETH/BTC"), null, listed)
            .Should().Be(Symbol.Parse("ETH/BTC"));

        // Not listed or absent: the first entry owns the workspace default.
        ChartViewModel.Preselect(Symbol.Parse("DOGE/USDT"), null, listed)
            .Should().Be(Symbol.Parse("BTC/USDT"));
        ChartViewModel.Preselect(null, null, listed)
            .Should().Be(Symbol.Parse("BTC/USDT"));

        // An empty list clears the selection with no substitute.
        ChartViewModel.Preselect(Symbol.Parse("BTC/USDT"), null, empty)
            .Should().BeNull();
    }

    /// <summary>
    /// The workspace preference is the FIRST choice: while the exchange's
    /// list carries the persisted symbol it beats even a still-listed
    /// current selection; an unlisted or absent preference falls through to
    /// the preserved-current → first-entry → empty branches with no silent
    /// substitute.
    /// </summary>
    [Fact]
    public void Preselect_ThePersistedPreferenceWinsWhenTheListCarriesIt()
    {
        var listed = new[] { Symbol.Parse("BTC/USDT"), Symbol.Parse("ETH/BTC") };

        // Listed preference beats everything, including a listed current.
        ChartViewModel.Preselect(Symbol.Parse("BTC/USDT"), Symbol.Parse("ETH/BTC"), listed)
            .Should().Be(Symbol.Parse("ETH/BTC"));
        ChartViewModel.Preselect(null, Symbol.Parse("ETH/BTC"), listed)
            .Should().Be(Symbol.Parse("ETH/BTC"));

        // Unlisted preference falls through to the preserved current when
        // the list still carries it, otherwise to the first entry.
        ChartViewModel.Preselect(Symbol.Parse("BTC/USDT"), Symbol.Parse("DOGE/USDT"), listed)
            .Should().Be(Symbol.Parse("BTC/USDT"));
        ChartViewModel.Preselect(Symbol.Parse("SOL/USDC"), Symbol.Parse("DOGE/USDT"), listed)
            .Should().Be(Symbol.Parse("BTC/USDT"));
        ChartViewModel.Preselect(null, Symbol.Parse("DOGE/USDT"), listed)
            .Should().Be(Symbol.Parse("BTC/USDT"));

        // An empty list leaves nothing to select, preference or not.
        ChartViewModel.Preselect(Symbol.Parse("BTC/USDT"), Symbol.Parse("ETH/BTC"), Array.Empty<Symbol>())
            .Should().BeNull();
    }

    /// <summary>
    /// A satisfied choice is delivered as the LIST's own instance, so the
    /// combo's SelectedItem always points at an item of its own source (the
    /// persisted string and the previous exchange's list parse into fresh
    /// instances — value-equal, never the same reference).
    /// </summary>
    [Fact]
    public void Preselect_TheSatisfiedChoiceIsTheListOwnInstance()
    {
        var listed = new[] { Symbol.Parse("BTC/USDT"), Symbol.Parse("ETH/BTC") };

        ChartViewModel.Preselect(null, Symbol.Parse("ETH/BTC"), listed)
            .Should().BeSameAs(listed[1]);
        ChartViewModel.Preselect(Symbol.Parse("BTC/USDT"), null, listed)
            .Should().BeSameAs(listed[0]);
    }

    /// <summary>
    /// The persisted-symbol parse contract: a non-blank normalized symbol
    /// string (any case, trimmed) yields its Symbol; blank values and
    /// malformed inputs yield null — the parse never throws, the caller logs
    /// the fall-through instead of substituting silently.
    /// </summary>
    [Theory]
    [InlineData("ETH/BTC", "ETH/BTC")]
    [InlineData("eth/btc", "ETH/BTC")]
    [InlineData("  ETH/BTC  ", "ETH/BTC")]
    [InlineData("AAPL", "AAPL")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("/USDT", null)]
    public void TryParseStoredSymbol_PinsTheRestoreContract(string? stored, string? expected) =>
        ChartViewModel.TryParseStoredSymbol(stored).Should().Be(
            expected is null ? null : Symbol.Parse(expected));

    /// <summary>
    /// The history depth is pinned per time frame: the same bar count walks
    /// back at each frame's own duration (the exchange-standard depth — W1
    /// gets its 200 weeks, M1 a fresh 200 minutes) instead of one fixed
    /// calendar window that starved the coarse frames and dated the fine
    /// ones. Monthly and weekly frames use nominal calendar lengths.
    /// </summary>
    [Theory]
    [InlineData(TimeFrame.M1, 200)]
    [InlineData(TimeFrame.M5, 1_000)]
    [InlineData(TimeFrame.M15, 3_000)]
    [InlineData(TimeFrame.M30, 6_000)]
    [InlineData(TimeFrame.H1, 12_000)]
    [InlineData(TimeFrame.H4, 48_000)]
    [InlineData(TimeFrame.D1, 288_000)]
    [InlineData(TimeFrame.W1, 2_016_000)]
    [InlineData(TimeFrame.MN1, 8_640_000)]
    public void HistoryStart_WalksBackTheNominalBarDepth_PerTimeFrame(
        TimeFrame timeFrame,
        double expectedMinutes)
    {
        var now = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        var start = ChartViewModel.HistoryStart(now, timeFrame, 200);
        var expected = now - TimeSpan.FromMinutes(expectedMinutes);

        // Day-scale calendar arithmetic rounds at the double tick (the
        // DateTimeOffset add path multiplies its milliseconds as doubles);
        // one-tenth of a microsecond of tolerated drift is still six orders
        // of magnitude below a single one-minute bar.
        Math.Abs((start - expected).Ticks)
            .Should()
            .BeLessThanOrEqualTo(1000L, "the walk-back is bar count times bar duration, at clock precision");
    }

    [Fact]
    public void HistoryStart_TheOldSevenDayH4Window_YieldsFortyTwoBarsWalkedBack()
    {
        var now = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

        // The deprecated 7-day calendar window held 42 H4 bars; that same
        // bar count walked back at the frame's own four-hour bars spans
        // exactly those 7 days — the depth is frame and count, not calendar.
        ChartViewModel.HistoryStart(now, TimeFrame.H4, 42)
            .Should().Be(now - TimeSpan.FromHours(168));
    }

    [Fact]
    public void HistoryStart_UnknownTimeFrame_IsAnErrorNotADefault()
    {
        Action act = () => ChartViewModel.HistoryStart(
            DateTimeOffset.UtcNow,
            (TimeFrame)999,
            200);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void SelectedTimeFrame_DefaultsToH1_WithThePickerCatalog()
    {
        var chart = CreateChart(CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")));

        chart.SelectedTimeFrame.Should().Be(
            TimeFrame.H1,
            "today's hardcoded behavior is the default until a restore applies");
        chart.TimeFrames.Should().Equal(
            TimeFrame.M1,
            TimeFrame.M5,
            TimeFrame.M15,
            TimeFrame.M30,
            TimeFrame.H1,
            TimeFrame.H4,
            TimeFrame.D1,
            TimeFrame.W1,
            TimeFrame.MN1);
    }

    /// <summary>
    /// The persisted <c>UI.ChartTimeFrame</c> choice applies at the startup
    /// walk, BEFORE the first load: the first bars request already carries
    /// the restored frame (no H1 flash). A restore never re-persists the
    /// value it just read.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_RestoresThePersistedTimeFrame_BeforeTheFirstLoad()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("M15");
        var marketData = new FakeMarketData();
        var chart = CreateChart(CreateService(marketData, new FakeMarketData(id: "fake2", name: "Fake2")), settings);
        var delivered = new List<IReadOnlyList<Bar>>();

        using var subscription = chart.BarsUpdates.Subscribe(delivered.Add);

        await chart.InitializeAsync();

        chart.SelectedTimeFrame.Should().Be(TimeFrame.M15);
        await settings.DidNotReceive().SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());

        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");

        marketData.BarsTimeFrames.Should().ContainSingle().Which.Should().Be(
            TimeFrame.M15,
            "the restored frame owns the first load");
        delivered.Should().ContainSingle();
    }

    /// <summary>
    /// Every non-name stored value (absent, blank, numeric, garbage) keeps
    /// the running default instead of substituting a time frame silently —
    /// the unrecognized value is logged, not applied.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("9")]
    [InlineData("25")]
    public async Task InitializeAsync_UnrecognizedPersistedChoice_KeepsTheDefaultH1(string? storedValue)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(storedValue ?? string.Empty);
        var chart = CreateChart(CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")), settings);

        await chart.InitializeAsync();

        chart.SelectedTimeFrame.Should().Be(TimeFrame.H1);
    }

    /// <summary>
    /// The persisted <c>UI.ChartUse12HourClock</c> applies at the startup
    /// walk, and the walk never writes the value back (the chart pipeline is
    /// a pure consumer of the clock setting — the settings window writes it).
    /// </summary>
    [Fact]
    public async Task InitializeAsync_RestoresThePersistedClockFormat_WithoutRewritingIt()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("M15");
        settings.GetAsync<bool>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(true);
        var chart = CreateChart(CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")), settings);

        chart.Use12HourClock.Should().BeFalse("the default stays the 24-hour clock until the walk applies the stored choice");

        await chart.InitializeAsync();

        chart.Use12HourClock.Should().BeTrue();
        await settings.DidNotReceive().SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    /// <summary>
    /// No persisted clock choice (the third-party default read) keeps the
    /// 24-hour clock the axis always printed before the preference existed.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_NoPersistedClockChoice_KeepsThe24HourDefault()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
        settings.GetAsync<bool>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(UiSettingsKeys.DefaultUse12HourClock);
        var chart = CreateChart(CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")), settings);

        await chart.InitializeAsync();

        chart.Use12HourClock.Should().BeFalse();
    }

    /// <summary>
    /// BUG-015, the restore matrix: the persisted clock applies in EVERY
    /// restore path — the time-frame restore's early returns no longer skip
    /// it. Case: no persisted time frame at all.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_NoPersistedTimeFrame_StillAppliesThePersistedClockFormat()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
        settings.GetAsync<bool>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(true);
        var chart = CreateChart(CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")), settings);

        await chart.InitializeAsync();

        chart.Use12HourClock.Should().BeTrue("the clock restore is no longer gated on the time-frame restore");
        await settings.DidNotReceive().SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    /// <summary>
    /// BUG-015, the restore matrix: case — the stored time frame is not a
    /// supported name. The unparsable path keeps H1 and still applies the
    /// persisted clock (the old tail restore never reached this branch).
    /// </summary>
    [Fact]
    public async Task InitializeAsync_UnparsablePersistedTimeFrame_StillAppliesThePersistedClockFormat()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("garbage");
        settings.GetAsync<bool>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(true);
        var chart = CreateChart(CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")), settings);

        await chart.InitializeAsync();

        chart.SelectedTimeFrame.Should().Be(TimeFrame.H1);
        chart.Use12HourClock.Should().BeTrue("the unparsable time frame skips the restore, not the clock");
        await settings.DidNotReceive().SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    /// <summary>
    /// BUG-015, the restore matrix — THE COMMON CASE: the persisted time
    /// frame equals the default H1, so the old restore's equal-value early
    /// return fired on every ordinary restart and silently reverted the
    /// persisted 12-hour clock. The clock must apply here too.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_PersistedDefaultTimeFrame_StillAppliesThePersistedClockFormat()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("H1");
        settings.GetAsync<bool>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(true);
        var chart = CreateChart(CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")), settings);

        chart.Use12HourClock.Should().BeFalse("the default stands until the walk applies the stored choice");

        await chart.InitializeAsync();

        chart.SelectedTimeFrame.Should().Be(TimeFrame.H1);
        chart.Use12HourClock.Should().BeTrue("a restart on the default time frame must not revert the persisted clock");
        await settings.DidNotReceive().SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    /// <summary>
    /// A settings-service change re-applies the clock format WITHOUT a
    /// restart: matching keys apply, unrelated setting changes and non-bool
    /// values are dropped (the applied choice stays in charge).
    /// </summary>
    [Fact]
    public void TheSettingsServiceChange_AppliesTheClockFormatLive()
    {
        var changes = new Subject<SettingsChange>();
        var settings = Substitute.For<ISettingsService>();
        settings.Changes.Returns(changes);
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
        settings.GetAsync<bool>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(UiSettingsKeys.DefaultUse12HourClock);
        var chart = CreateChart(CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")), settings);

        chart.Use12HourClock.Should().BeFalse();

        changes.OnNext(new SettingsChange(UiSettingsKeys.Section, UiSettingsKeys.ChartUse12HourClock, false, true));

        chart.Use12HourClock.Should().BeTrue("a settings change must reach the chart without a restart");

        changes.OnNext(new SettingsChange(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartTimeFrame,
            "M1",
            "M5"));
        changes.OnNext(new SettingsChange(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartUse12HourClock,
            true,
            "true"));

        chart.Use12HourClock.Should().BeTrue("an unrelated key and a non-bool value both leave the clock format alone");
    }

    [Fact]
    public async Task SelectedTimeFrame_Change_ReloadsTheChartAndPersistsTheChoice()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
        settings.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);
        var marketData = new FakeMarketData();
        var chart = CreateChart(CreateService(marketData, new FakeMarketData(id: "fake2", name: "Fake2")), settings);
        var delivered = new List<IReadOnlyList<Bar>>();

        using var subscription = chart.BarsUpdates.Subscribe(delivered.Add);

        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        chart.SelectedTimeFrame = TimeFrame.M5;

        marketData.BarsRequested.Should().HaveCount(2, "a time frame change is a reload trigger, not a no-op");
        marketData.BarsTimeFrames.Should().Equal([TimeFrame.H1, TimeFrame.M5]);
        delivered.Should().HaveCount(2);
        delivered[1].Should().ContainSingle().Which.Close.Should().Be(100m);

        // The user flip persists the enum name into the UI section.
        await settings.Received().SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartTimeFrame,
            "M5");
    }

    /// <summary>
    /// Every user selection persists the normalized symbol into the
    /// Workspace section — the same value the next cold start restores as
    /// the workspace preference (the settings service serves the in-memory
    /// value immediately, even with the debounce window open).
    /// </summary>
    [Fact]
    public async Task SelectedSymbol_Change_PersistsTheWorkspaceSymbol()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
        settings.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);
        var chart = CreateChart(CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")), settings);

        chart.SelectedSymbol = Symbol.Parse("ETH/BTC");
        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");

        await settings.Received().SetAsync(
            UiSettingsKeys.WorkspaceSection,
            UiSettingsKeys.WorkspaceSymbolId,
            "ETH/BTC");
        await settings.Received().SetAsync(
            UiSettingsKeys.WorkspaceSection,
            UiSettingsKeys.WorkspaceSymbolId,
            "BTC/USDT");
    }

    /// <summary>
    /// An identity echo (the same instance re-raised through the two-way
    /// combo binding) writes nothing: RaiseAndSetIfChanged suppresses the
    /// raise, so the persistence pipeline sees no emission at all.
    /// </summary>
    [Fact]
    public async Task SelectedSymbol_ReAssigningTheSameInstance_WritesOnce()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
        settings.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);
        var chart = CreateChart(CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")), settings);

        chart.SelectedSymbol = Symbol.Parse("ETH/BTC");
        var unchanged = chart.SelectedSymbol;
        chart.SelectedSymbol = unchanged;

        await settings.Received(1).SetAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>());
    }

    /// <summary>
    /// A cleared selection never erases the preference: the null assignment
    /// passes no writer, so the last confirmed choice stays persisted for
    /// the next cold start.
    /// </summary>
    [Fact]
    public async Task SelectedSymbol_ClearedByTheBootstrapEcho_PersistsNothing()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
        settings.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);
        var chart = CreateChart(CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")), settings);

        chart.SelectedSymbol = Symbol.Parse("ETH/BTC");
        chart.SelectedSymbol = null;

        await settings.Received().SetAsync(
            UiSettingsKeys.WorkspaceSection,
            UiSettingsKeys.WorkspaceSymbolId,
            "ETH/BTC");
        await settings.Received(1).SetAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>());
    }

    [Fact]
    public async Task FastTimeFrameFlip_DropsTheInFlightLoadAndDeliversTheWinner()
    {
        var marketData = new FakeMarketData(barDelayMs: 30);
        var chart = CreateChart(CreateService(marketData, new FakeMarketData(id: "fake2", name: "Fake2")));
        var latest = new TaskCompletionSource<IReadOnlyList<Bar>>(TaskCreationOptions.RunContinuationsAsynchronously);

        // At delivery time the winning load's lease must still be alive (the
        // same receipt contract as the exchange-switch cancellation test);
        // the winner is the THIRD load: H1 initial, M5 superseded, D1 wins.
        var supersededCancelledAtDelivery = default(bool?);
        using var subscription = chart.BarsUpdates.Subscribe(bars =>
        {
            supersededCancelledAtDelivery = marketData.BarsCancellationTokens[2].IsCancellationRequested;
            latest.TrySetResult(bars);
        });

        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        chart.SelectedTimeFrame = TimeFrame.M5;
        chart.SelectedTimeFrame = TimeFrame.D1;

        var winnerBars = await latest.Task.WaitAsync(TimeSpan.FromSeconds(5));

        winnerBars.Should().ContainSingle().Which.Close.Should().Be(100m);
        marketData.BarsRequested.Should().HaveCount(3, "every flip requested its own reload");
        marketData.BarsTimeFrames.Should().Equal([TimeFrame.H1, TimeFrame.M5, TimeFrame.D1]);
        marketData.BarsCancellationTokens[1].IsCancellationRequested.Should().BeTrue(
            "the Switch dropped the in-flight M5 load; only the D1 winner runs");
        supersededCancelledAtDelivery.Should().BeFalse(
            "the winning load is still alive when its bars arrive");
    }

    /// <summary>
    /// The persisted-value parse contract: enum names (any case, trimmed)
    /// restore their time frame; absent, blank, numeric or unrecognized
    /// values produce null (the caller keeps the default and logs).
    /// </summary>
    [Theory]
    [InlineData("M1", TimeFrame.M1)]
    [InlineData("m15", TimeFrame.M15)]
    [InlineData("  H4  ", TimeFrame.H4)]
    [InlineData("MN1", TimeFrame.MN1)]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("9", null)]
    [InlineData("garbage", null)]
    public void ParseStoredTimeFrame_PinsTheRestoreContract(string? stored, TimeFrame? expected) =>
        ChartViewModel.ParseStoredTimeFrame(stored).Should().Be(expected);

    #region Older History Paging

    /// <summary>The boot depth on the default H1 frame (hours).</summary>
    private const int InitialDepthHours = 200;

    [Fact]
    public void BarsLoad_FetchesTheFullInitialDepthAndTheFreshEdge()
    {
        var marketData = new FakeMarketData();
        var chart = CreateChart(CreateService(marketData, new FakeMarketData(id: "fake2", name: "Fake2")));

        // The fact drives its own load: the bootstrap preselection applies
        // through a marshalled subscriber (inline in a solo run, posted into
        // an unpumped queue in a full-suite run), so only an explicit
        // assignment makes the asserted fetch land deterministically. The
        // raw load machine subscribes inline — the record is on the adapter
        // before the statement returns, in every dispatcher world.
        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");

        // The boot shape: the H1 depth walked back from now, running to the
        // live edge, and the limit one over the depth (the boundary candle
        // the merge dedupes — a saturated limit would cut the fresh edge).
        marketData.BarsRequested[^1].Should().Be(Symbol.Parse("BTC/USDT"));
        marketData.BarsTimeFrames[^1].Should().Be(TimeFrame.H1);
        var window = marketData.BarsWindowRequests[^1];
        window.Limit.Should().Be(InitialDepthHours + 1);
        window.To.Should().BeNull("the initial load always runs to the live edge");
        window.From.Should().BeCloseTo(
            DateTimeOffset.UtcNow.AddHours(-InitialDepthHours),
            TimeSpan.FromHours(2),
            "the H1 depth walks 200 candles back (a small clock slack)");
    }

    [Fact]
    public async Task PagingRequest_BeforeAnyDelivery_DropsQuietly()
    {
        var marketData = new FakeMarketData(barDelayMs: 30);
        var chart = CreateChart(CreateService(marketData, new FakeMarketData(id: "fake2", name: "Fake2")));

        // An explicit assignment makes the recorded load deterministic (see
        // BarsLoad above). The 30 ms delay moves the load's completion off
        // the test thread, so its delivery lands as a posted marshal into an
        // unpumped queue — Bars stays empty in every dispatcher world.
        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        var recorded = marketData.BarsWindowRequests.Count;
        recorded.Should().BePositive("the fact's own load must be in flight");

        // Act: a paging request while no delivery has ever landed.
        await chart.RequestOlderBarsAsync();

        marketData.BarsWindowRequests.Should().HaveCount(
            recorded,
            "a request before any delivery has no target and drops quietly");
        marketData.BarsWindowRequests.Should().OnlyContain(
            window => window.To == null,
            "no paging window (To = the series head) was ever fetched");
    }

    [Fact]
    public void IndicatorConfig_ASettingsChange_PublishesTheRecompute()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(UiSettingsKeys.DefaultChartIndicators);
        var changes = new Subject<SettingsChange>();
        settings.Changes.Returns(changes);

        var chart = CreateChart(
            CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")),
            settings);

        // The raw publish path is marshal-free (the compute is pure), so the
        // fact reads it directly - the marshalled property assignment stays
        // unpumped in a test process and is covered by the probe.
        var publications = new List<IReadOnlyList<ChartIndicatorLine>>();
        using var subscription = chart.IndicatorLinesUpdates.Subscribe(publications.Add);

        changes.OnNext(new SettingsChange(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartIndicators,
            null,
            UiSettingsKeys.DefaultChartIndicators));

        publications.Should().ContainSingle("the config change recomputes against the current series");
        publications[0].Should().BeEmpty("no bars are loaded in the marshal-invariant world");
    }

    [Fact]
    public void IndicatorConfig_AnEchoedChange_StillRecomputes()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(UiSettingsKeys.DefaultChartIndicators);
        var changes = new Subject<SettingsChange>();
        settings.Changes.Returns(changes);

        var chart = CreateChart(
            CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")),
            settings);
        var publications = new List<IReadOnlyList<ChartIndicatorLine>>();
        using var subscription = chart.IndicatorLinesUpdates.Subscribe(publications.Add);

        // The VM's own save writes the config back: the changes stream
        // echoes it and the recompute runs again (the same echo-absorption
        // contract the clock format follows - a recompute is idempotent and
        // cheap, no echo suppression needed).
        changes.OnNext(new SettingsChange(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartIndicators,
            UiSettingsKeys.DefaultChartIndicators,
            UiSettingsKeys.DefaultChartIndicators));

        publications.Should().HaveCount(1);
    }

    /// <summary>
    /// The rows a rebuild or a bootstrap lists, pinned in its pure form
    /// (T-039): favorites pin to the head of the list (the exchange's own
    /// order inside each group), the hidden pairs leave the everyday list
    /// unconditionally — a hidden symbol never takes the chart, the
    /// settings tab's reveals are its only way back.
    /// </summary>
    [Fact]
    public void BuildSymbolRows_FavoritesPinToTheHeadStably()
    {
        var symbols = new[] { Symbol.Parse("BTC/USDT"), Symbol.Parse("ETH/BTC"), Symbol.Parse("SOL/USDC") };
        var favorites = new HashSet<string>(StringComparer.Ordinal) { "ETH/BTC", "BTC/USDT" };

        var rows = ChartViewModel.BuildSymbolRows(
            symbols,
            favorites,
            new HashSet<string>(StringComparer.Ordinal),
            maxRows: 50);

        // The favorites lead in the list's own order; the unmarked follow.
        rows.Select(row => row.Symbol.Normalized).Should().Equal("BTC/USDT", "ETH/BTC", "SOL/USDC");
        rows[0].IsFavorite.Should().BeTrue();
        rows[1].IsFavorite.Should().BeTrue();
        rows[2].IsFavorite.Should().BeFalse();
    }

    /// <summary>
    /// The hidden pair leaves the everyday list no matter what holds the
    /// selection: the save that hid the shown chart rebases the chart to the
    /// first visible pair (the rebuild decides, the selection stream
    /// persists it — see the rebuild facts below).
    /// </summary>
    [Fact]
    public void BuildSymbolRows_HiddenPairsLeaveTheEverydayList()
    {
        var symbols = new[] { Symbol.Parse("BTC/USDT"), Symbol.Parse("ETH/BTC"), Symbol.Parse("SOL/USDC") };
        var hidden = new HashSet<string>(StringComparer.Ordinal) { "BTC/USDT" };

        var filtered = ChartViewModel.BuildSymbolRows(
            symbols,
            new HashSet<string>(StringComparer.Ordinal),
            hidden,
            maxRows: 50);

        filtered.Select(row => row.Symbol.Normalized).Should().Equal("ETH/BTC", "SOL/USDC");
    }

    /// <summary>
    /// The marks are mutually exclusive by rule: a favorite beats a stored
    /// hidden entry (the legacy collision left by earlier releases' shape
    /// freedom). The starred pair stays listed (pinned to the head), and the
    /// rebuild callers feed the preselection the same effective hidden set —
    ///<c>raw hidden minus favorites</c>.
    /// </summary>
    [Fact]
    public void BuildSymbolRows_AFavoriteBeatsAStoredHiddenEntry()
    {
        var symbols = new[] { Symbol.Parse("BTC/USDT"), Symbol.Parse("ETH/BTC") };
        var favorites = new HashSet<string>(StringComparer.Ordinal) { "BTC/USDT" };
        var hidden = new HashSet<string>(StringComparer.Ordinal) { "BTC/USDT", "SOL/USDC" };

        var rows = ChartViewModel.BuildSymbolRows(
            symbols,
            favorites,
            hidden,
            maxRows: 50);

        rows.Select(row => row.Symbol.Normalized).Should().Equal("BTC/USDT", "ETH/BTC");
        rows[0].IsFavorite.Should().BeTrue("the star wins the legacy collision");
    }

    [Fact]
    public void BuildSymbolRows_TheCapNeverCutsAFavorite()
    {
        var symbols = Enumerable.Range(0, 20)
            .Select(index => Symbol.Parse($"SYM{index:00}/USDT"))
            .ToList();
        var favorites = new HashSet<string>(StringComparer.Ordinal)
        {
            "SYM00/USDT",
            "SYM01/USDT",
            "SYM02/USDT",
        };

        var rows = ChartViewModel.BuildSymbolRows(
            symbols,
            favorites,
            new HashSet<string>(StringComparer.Ordinal),
            maxRows: 5);

        rows.Should().HaveCount(5, "the cap still bounds the page");
        rows.Take(3).Should().OnlyContain(
            row => row.IsFavorite,
            "the favorites sort ahead of the cap, so a starred symbol is never trimmed");
    }

    /// <summary>
    /// A hidden persisted preference never takes the chart: the preselection
    /// falls through the preserved current (also hidden-checked) to the
    /// first visible entry.
    /// </summary>
    [Fact]
    public void Preselect_TheHiddenPersistedPair_YieldsToTheFirstVisible()
    {
        var listed = new[] { Symbol.Parse("BTC/USDT"), Symbol.Parse("ETH/BTC"), Symbol.Parse("SOL/USDC") };
        var hidden = new HashSet<string>(StringComparer.Ordinal) { "ETH/BTC" };

        ChartViewModel.Preselect(null, Symbol.Parse("ETH/BTC"), listed, hidden)
            .Should().Be(Symbol.Parse("BTC/USDT"), "the hidden preference never takes the chart");

        // A listed current that is hidden yields as well: the hidden pair
        // cannot carry the workspace across an exchange switch either.
        ChartViewModel.Preselect(Symbol.Parse("ETH/BTC"), null, listed, hidden)
            .Should().Be(Symbol.Parse("BTC/USDT"));

        // Everything visible and hidden alike leaves nothing to select.
        var allHidden = new HashSet<string>(StringComparer.Ordinal) { "BTC/USDT", "ETH/BTC", "SOL/USDC" };
        ChartViewModel.Preselect(Symbol.Parse("BTC/USDT"), null, listed, allHidden)
            .Should().BeNull("a fully hidden list clears the selection with no substitute");
    }

    /// <summary>
    /// The symbols bootstrap is a pure reader of the persisted marks: the
    /// constructor (and the startup walk) never persist anything — reading
    /// the maps is never rewriting them.
    /// </summary>
    [Fact]
    public async Task TheBootstrap_NeverRewritesThePersistedMarks()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("""{"fake":["BTC/USDT"]}""");
        settings.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);

        var chart = CreateChart(
            CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")),
            settings);
        await chart.InitializeAsync();

        await settings.DidNotReceive().SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    #region Candle Export

    /// <summary>
    /// The candles export rides exactly the delivered series: the command
    /// streams the loaded <c>Bars</c> (the delivered load's copy, landed
    /// through the pump drain) through the CSV service — rebase and paged
    /// prepends included by construction, never a second fetch.
    /// </summary>
    [Fact]
    public async Task ExportCandlesCommand_StreamsTheDeliveredSeries()
    {
        var marketData = new FakeMarketData();
        var export = Substitute.For<ICsvExportService>();
        var chart = CreateChart(
            CreateService(marketData, new FakeMarketData(id: "fake2", name: "Fake2")),
            export: export);
        var delivered = new TaskCompletionSource<IReadOnlyList<Bar>>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? requestedName = null;
        IReadOnlyList<Bar> exportedBars = null!;
        string? exportedPath = null;

        using var barsSubscription = chart.BarsUpdates.Subscribe(
            bars => delivered.TrySetResult(bars));

        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        var series = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The marshal hop lands the delivered load in the Bars property (the
        // export set) — the pump executes exactly the queued jobs.
        await AvaloniaDispatcherPump.DrainAsync();

        export.ExportCandlesAsync(
                Arg.Do<string>(path => exportedPath = path),
                Arg.Do<IReadOnlyList<Bar>>(bars => exportedBars = bars),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        using var handler = chart.SaveCandlesCsvRequested.RegisterHandler(context =>
        {
            requestedName = context.Input;
            context.SetOutput(@"C:\exports\candles.csv");
            return Task.CompletedTask;
        });

        await chart.ExportCandlesCommand.Execute(RxVoid.Default);

        requestedName.Should().Be("candles.csv", "the dialog carries the suggested name");
        exportedPath.Should().Be(@"C:\exports\candles.csv");
        exportedBars.Should().BeSameAs(chart.Bars, "the export set is the displayed series collection");
        exportedBars.Should().Equal(series, "the displayed series is exactly the delivered one");
        chart.ErrorMessage.Should().BeNull();
        chart.HasErrorMessage.Should().BeFalse();
    }

    /// <summary>
    /// A cancelled save dialog exports nothing: the null destination is not
    /// an error, the service never runs and no error line appears.
    /// </summary>
    [Fact]
    public async Task ExportCandlesCommand_WithACancelledDialog_EndsQuietly()
    {
        var export = Substitute.For<ICsvExportService>();
        var chart = CreateChart(
            CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")),
            export: export);
        string? requestedName = null;

        using var handler = chart.SaveCandlesCsvRequested.RegisterHandler(context =>
        {
            requestedName = context.Input;
            context.SetOutput(null);
            return Task.CompletedTask;
        });

        await chart.ExportCandlesCommand.Execute(RxVoid.Default);

        requestedName.Should().Be("candles.csv");
        await export.DidNotReceive().ExportCandlesAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<Bar>>(),
            Arg.Any<CancellationToken>());
        chart.ErrorMessage.Should().BeNull();
        chart.HasErrorMessage.Should().BeFalse();
    }

    /// <summary>
    /// A write fault surfaces the localized export message: the fake
    /// compiler prints `[Chart.ExportFailed:{detail}]`, so the fact pins
    /// both the key and the carried exception text.
    /// </summary>
    [Fact]
    public async Task ExportCandlesCommand_WhenTheServiceThrows_ShowsTheLocalizedFault()
    {
        var localization = Substitute.For<ILocalizationManager>();
        localization.GetString(Arg.Any<string>(), Arg.Any<object[]>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}:{string.Join("|", call.ArgAt<object[]>(1))}]");
        var export = Substitute.For<ICsvExportService>();
        export.ExportCandlesAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<Bar>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException(
                new InvalidOperationException("directory is gone")));
        var chart = CreateChart(
            CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")),
            export: export,
            localization: localization);

        using var handler = chart.SaveCandlesCsvRequested.RegisterHandler(context =>
        {
            context.SetOutput(@"C:\exports\candles.csv");
            return Task.CompletedTask;
        });

        await chart.ExportCandlesCommand.Execute(RxVoid.Default);

        chart.ErrorMessage.Should().Be("[Chart.ExportFailed:directory is gone]");
        chart.HasErrorMessage.Should().BeTrue();
    }

    /// <summary>
    /// A stale error line never survives a fresh flow: the cancelled-dialog
    /// outcome after a fault clears the line (the flow's own outcome is the
    /// only message on display).
    /// </summary>
    [Fact]
    public async Task ExportCandlesCommand_AfterAFault_ACancelledDialogClearsTheErrorLine()
    {
        var export = Substitute.For<ICsvExportService>();
        export.ExportCandlesAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<Bar>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException(
                new InvalidOperationException("disk full")));
        var localization = Substitute.For<ILocalizationManager>();
        localization.GetString(Arg.Any<string>(), Arg.Any<object[]>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}:{string.Join("|", call.ArgAt<object[]>(1))}]");
        var chart = CreateChart(
            CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")),
            export: export,
            localization: localization);

        using var failingHandler = chart.SaveCandlesCsvRequested.RegisterHandler(context =>
        {
            context.SetOutput(@"C:\exports\candles.csv");
            return Task.CompletedTask;
        });

        await chart.ExportCandlesCommand.Execute(RxVoid.Default);
        chart.ErrorMessage.Should().Contain("Chart.ExportFailed", "the fault anchored the line first");

        // Rebuild the world for the fresh flow: the dialog cancels now.
        failingHandler.Dispose();
        using var cancellingHandler = chart.SaveCandlesCsvRequested.RegisterHandler(context =>
        {
            context.SetOutput(null);
            return Task.CompletedTask;
        });

        await chart.ExportCandlesCommand.Execute(RxVoid.Default);

        chart.ErrorMessage.Should().BeNull();
        chart.HasErrorMessage.Should().BeFalse();
    }

    #endregion Candle Export

    #endregion

    #endregion
}
