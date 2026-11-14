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
using ZettaTrade.Plugins.Abstractions;
using ZettaTrade.Strategies;
using ZettaTrade.UI.Charting;

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
        private readonly IReadOnlyList<IReadOnlyList<Bar>>? _barsPagePlan;
        private int _pageCursor;

        public FakeMarketData(
            int barDelayMs = 0,
            IEnumerable<string>? failingSymbols = null,
            string id = "fake",
            string name = "Fake",
            IReadOnlyList<string>? symbolNames = null,
            decimal btcClose = 100m,
            IReadOnlyList<IReadOnlyList<Bar>>? barsPagePlan = null)
        {
            _barDelayMs = barDelayMs;
            _failingSymbols = (failingSymbols ?? []).Select(Symbol.Parse).ToList();
            _symbolList = symbolNames is null
                ? [Symbol.Parse("BTC/USDT"), Symbol.Parse("ETH/BTC"), Symbol.Parse("SOL/USDC")]
                : symbolNames.Select(Symbol.Parse).ToList();
            _btcClose = btcClose;
            _barsPagePlan = barsPagePlan;
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

            // The T-086 plan slot: every bars call consumes the NEXT planned
            // page verbatim (an empty page is the feed's empty answer), so a
            // fact can script the boot page, the repair fetch and the paging
            // fetch in exact order. An exhausted plan stays silent and the
            // standing one-bar behavior continues below.
            if (_barsPagePlan is { } plan && _pageCursor < plan.Count)
            {
                return plan[_pageCursor++];
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

        public async Task<IReadOnlyList<Bar>> GetLatestAsync(
            string adapterId,
            Symbol symbol,
            TimeFrame timeFrame,
            int limit,
            CancellationToken ct = default)
        {
            Sequence.Enqueue("cache-read");
            if (ReadFault is { } fault)
            {
                // A throw inside an async method surfaces as a faulted task:
                // the stream reports it, never a live throw past Subscribe.
                throw fault;
            }

            return _served;
        }

        public async Task<IReadOnlyList<Bar>> GetOlderThanAsync(
            string adapterId,
            Symbol symbol,
            TimeFrame timeFrame,
            DateTimeOffset before,
            int limit,
            CancellationToken ct = default)
        {
            // The repair chain's probe marker reads alongside the facts' own
            // delivery log (the T-085 write-through ordering discipline).
            Sequence.Enqueue("cache-probe");
            if (ReadFault is { } fault)
            {
                throw fault;
            }

            return _served
                .Where(bar => bar.Time < before)
                .TakeLast(limit)
                .ToArray();
        }

        public async Task WriteThroughAsync(
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
                throw fault;
            }
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
        ICandleCache? candleCache = null,
        FakeDrawingStore? drawingStore = null)
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

        // The cache the chart facts ride (an empty one by default) is the
        // SAME cache the repair chain probes: the T-086 facts watch the
        // probe and the fetched segments on the one fake.
        var cache = candleCache ?? new FakeCandleCache([]);

        // The drawings store the facts ride: an empty load plan answers
        // every load with an empty layer unless a fact scripts one.
        var drawings = drawingStore ?? new FakeDrawingStore();

        // The T-112 seam's inert hosting pair: no plugin resolves (the
        // substitute catalog is empty), so the markers stream stays silent
        // and the chart facts' pre-existing expectations hold untouched.
        var relay = new SignalSeriesRelay();
        var signals = new StrategySignalService(
            relay.Pages,
            Substitute.For<IStrategyCatalog>(),
            NullLoggerFactory.Instance);
        var rail = new SignalRailViewModel(
            signals.Signals,
            localization ?? Substitute.For<ILocalizationManager>(),
            Substitute.For<ISettingsService>(),
            NullLogger<SignalRailViewModel>.Instance);

        return new(
            service,
            new SymbolListProvider(service, NullLogger<SymbolListProvider>.Instance),
            timeFrameSettings,
            new EmptyIndicatorCatalog(),
            export ?? Substitute.For<ICsvExportService>(),
            localization ?? Substitute.For<ILocalizationManager>(),
            cache,
            // The repair chain's fetcher: every pass rides the same fake
            // cache (write-before-emit visibility) and a plain logger.
            new CandleBackfillService(cache, NullLogger<CandleBackfillService>.Instance),
            drawings,
            signals,
            rail,
            relay,
            // The T-122 seam rides silent in the pre-existing facts: an
            // empty-footprint stream keeps the overlay publishes inert.
            Observable.Empty<ChartFootprintSnapshot>(),
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
    /// thread pool thread, making the same facts order-dependent). The
    /// pre-drain frozen state is the default world; the facts that must walk
    /// INTO a posted apply — the export command flow and the T-086
    /// deep-history repair chain — call
    /// <see cref="AvaloniaDispatcherPump.DrainAsync"/> explicitly.
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

    #region Snapshot & Visible-Range Export (T-094)

    /// <summary>
    /// The PNG flow builds the request off the workspace context and hands
    /// the render to the view: the suggested file name carries the file-safe
    /// symbol, the time frame and the timestamp, the watermark reads
    /// "symbol · timeframe", and the picked destination passes through
    /// untouched (the render itself is the view's job — the window test
    /// pins it; here the outcome is what the ViewModel owns).
    /// </summary>
    [Fact]
    public async Task ExportPngCommand_BuildsTheRequest_AndEndsWithThePick()
    {
        var chart = CreateChart(
            CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")));
        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        await AvaloniaDispatcherPump.DrainAsync();

        PngExportRequest? request = null;

        using var handler = chart.ExportPngRequested.RegisterHandler(context =>
        {
            request = context.Input;
            context.SetOutput(@"C:\exports\BTC_USDT_H1.png");
            return Task.CompletedTask;
        });

        await chart.ExportPngCommand.Execute(RxVoid.Default);

        request.Should().NotBeNull("the view needs the file name and the watermark to run the flow");
        request!.SuggestedFileName.Should()
            .MatchRegex(@"^BTC_USDT_H1_\d{8}_\d{6}\.png$", "the symbol's separator is file-safe and the timestamp stamps the second");
        request.Watermark.Should().Be("BTC/USDT · H1", "the watermark reads the symbol and the time frame");
        chart.ErrorMessage.Should().BeNull();
        chart.HasErrorMessage.Should().BeFalse();
    }

    /// <summary>A cancelled save dialog renders nothing: quiet outcome, no error line.</summary>
    [Fact]
    public async Task ExportPngCommand_WithACancelledDialog_EndsQuietly()
    {
        var chart = CreateChart(
            CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")));
        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        await AvaloniaDispatcherPump.DrainAsync();

        using var handler = chart.ExportPngRequested.RegisterHandler(context =>
        {
            context.SetOutput(null);
            return Task.CompletedTask;
        });

        await chart.ExportPngCommand.Execute(RxVoid.Default);

        chart.ErrorMessage.Should().BeNull();
        chart.HasErrorMessage.Should().BeFalse();
    }

    /// <summary>
    /// A render or write fault propagates through the interaction (the
    /// window's export throws) and surfaces the localized export message —
    /// the same error-line contract the candles CSV keeps.
    /// </summary>
    [Fact]
    public async Task ExportPngCommand_WhenTheRenderThrows_ShowsTheLocalizedFault()
    {
        var localization = Substitute.For<ILocalizationManager>();
        localization.GetString(Arg.Any<string>(), Arg.Any<object[]>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}:{string.Join("|", call.ArgAt<object[]>(1))}]");
        var chart = CreateChart(
            CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")),
            localization: localization);
        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        await AvaloniaDispatcherPump.DrainAsync();

        using var handler = chart.ExportPngRequested.RegisterHandler(async context =>
        {
            await Task.Yield();
            throw new InvalidOperationException("render is gone");
        });

        await chart.ExportPngCommand.Execute(RxVoid.Default);

        chart.ErrorMessage.Should().Be("[Chart.ExportFailed:render is gone]");
        chart.HasErrorMessage.Should().BeTrue();
    }

    /// <summary>
    /// No workspace symbol — no context to stamp: the flow ends before the
    /// interaction fires (a pick without an address exports nothing, and
    /// stays quiet).
    /// </summary>
    [Fact]
    public async Task ExportPngCommand_WithoutASymbol_EndsBeforeTheView()
    {
        var chart = CreateChart(
            CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")));

        var fired = false;

        using var handler = chart.ExportPngRequested.RegisterHandler(context =>
        {
            fired = true;
            context.SetOutput(@"C:\exports\never.png");
            return Task.CompletedTask;
        });

        await chart.ExportPngCommand.Execute(RxVoid.Default);

        fired.Should().BeFalse("a pick without a workspace symbol has no watermark context");
        chart.ErrorMessage.Should().BeNull();
        chart.HasErrorMessage.Should().BeFalse();
    }

    /// <summary>
    /// The visible-range flow streams EXACTLY the handoff's slice through
    /// the shared CSV writer: the view slices the chart's own visible
    /// window, the ViewModel never substitutes the loaded series for it —
    /// the file carries precisely what the frame paints.
    /// </summary>
    [Fact]
    public async Task ExportVisibleCandlesCommand_StreamsExactlyTheHandedSlice()
    {
        var export = Substitute.For<ICsvExportService>();
        var chart = CreateChart(
            CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")),
            export: export);
        IReadOnlyList<Bar> exportedBars = null!;
        string? exportedPath = null;

        export.ExportCandlesAsync(
                Arg.Do<string>(path => exportedPath = path),
                Arg.Do<IReadOnlyList<Bar>>(bars => exportedBars = bars),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var visibleSlice = new[] { TypeBar(0), TypeBar(1) };

        using var handler = chart.ExportVisibleCsvRequested.RegisterHandler(context =>
        {
            context.Input.Should().Be("candles-visible.csv", "the dialog carries the suggested name");
            context.SetOutput(new VisibleCsvHandoff(@"C:\exports\visible.csv", visibleSlice));
            return Task.CompletedTask;
        });

        await chart.ExportVisibleCandlesCommand.Execute(RxVoid.Default);

        exportedPath.Should().Be(@"C:\exports\visible.csv");
        exportedBars.Should().BeSameAs(visibleSlice, "the writer receives exactly the handed slice, never a copy or the loaded series");
        chart.ErrorMessage.Should().BeNull();
        chart.HasErrorMessage.Should().BeFalse();
    }

    /// <summary>A cancelled save dialog exports nothing: quiet outcome, the writer never runs.</summary>
    [Fact]
    public async Task ExportVisibleCandlesCommand_WithACancelledDialog_EndsQuietly()
    {
        var export = Substitute.For<ICsvExportService>();
        var chart = CreateChart(
            CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")),
            export: export);

        using var handler = chart.ExportVisibleCsvRequested.RegisterHandler(context =>
        {
            context.SetOutput(null);
            return Task.CompletedTask;
        });

        await chart.ExportVisibleCandlesCommand.Execute(RxVoid.Default);

        await export.DidNotReceive().ExportCandlesAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<Bar>>(),
            Arg.Any<CancellationToken>());
        chart.ErrorMessage.Should().BeNull();
        chart.HasErrorMessage.Should().BeFalse();
    }

    /// <summary>A write fault surfaces the localized export message — the candles CSV contract.</summary>
    [Fact]
    public async Task ExportVisibleCandlesCommand_WhenTheWriterThrows_ShowsTheLocalizedFault()
    {
        var export = Substitute.For<ICsvExportService>();
        export.ExportCandlesAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<Bar>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException(
                new InvalidOperationException("directory is gone")));
        var localization = Substitute.For<ILocalizationManager>();
        localization.GetString(Arg.Any<string>(), Arg.Any<object[]>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}:{string.Join("|", call.ArgAt<object[]>(1))}]");
        var chart = CreateChart(
            CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")),
            export: export,
            localization: localization);

        using var handler = chart.ExportVisibleCsvRequested.RegisterHandler(context =>
        {
            context.SetOutput(new VisibleCsvHandoff(@"C:\exports\visible.csv", [TypeBar(0)]));
            return Task.CompletedTask;
        });

        await chart.ExportVisibleCandlesCommand.Execute(RxVoid.Default);

        chart.ErrorMessage.Should().Be("[Chart.ExportFailed:directory is gone]");
        chart.HasErrorMessage.Should().BeTrue();
    }

    /// <summary>The file name builder's golden: file-safe symbol, time frame, UTC timestamp to the second.</summary>
    [Fact]
    public void PngFileName_IsFileSafe_AndTimestampedToTheSecond()
    {
        var timestamp = new DateTimeOffset(2026, 11, 9, 21, 4, 7, TimeSpan.FromHours(5));

        ChartViewModel.PngFileName("BTC/USDT", TimeFrame.H1, timestamp)
            .Should().Be("BTC_USDT_H1_20261109_160407.png", "the slash is not file-safe, the stamp reads UTC");
    }

    /// <summary>The watermark builder's golden: the symbol, a middot, the time frame.</summary>
    [Fact]
    public void PngWatermark_ReadsTheSymbolAndTheTimeFrame()
    {
        ChartViewModel.PngWatermark("BTC/USDT", TimeFrame.H1).Should().Be("BTC/USDT · H1");
    }

    #endregion Snapshot & Visible-Range Export (T-094)

    #endregion

    #region Deep-history Repair (T-086)

    /// <summary>The open the repair facts' windows anchor at: a clean H1
    /// edge in January 2026, far away from every other fact's synthetic
    /// base time.</summary>
    private static readonly DateTimeOffset RepairBaseTime = new(2026, 1, 1, 13, 0, 0, TimeSpan.Zero);

    /// <summary>One synthetic H1 bar at <see cref="RepairBaseTime"/> plus
    /// the given hour offset: per-hour ohlcv values make every merge
    /// position assertable by value, not just by open time.</summary>
    private static Bar At(int hour) => new(
        RepairBaseTime.AddHours(hour),
        500m + hour,
        501m + hour,
        499m + hour,
        500m + hour,
        5);

    [Fact]
    public async Task TheRepairChain_HealsTheDeliveryHoleWithFetchedBars()
    {
        var cache = new FakeCandleCache([]);
        var marketData = new FakeMarketData(barsPagePlan: [[At(0), At(2)], [At(1)]]);
        var chart = CreateChart(
            CreateService(marketData, new FakeMarketData(id: "fake2", name: "Fake2")),
            candleCache: cache);
        var healed = new List<IReadOnlyList<Bar>>();

        using var healedWatch = chart.RepairedBarsUpdates.Subscribe(healed.Add);
        chart.Bars.Should().BeEmpty("nothing delivered yet");

        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        await AvaloniaDispatcherPump.DrainAsync();

        // The delivered page [13:00, 15:00] left the 14:00 open missing: the
        // repair chain probed the empty cache, detected exactly that hole and
        // fetched it through the backfill service.
        marketData.BarsWindowRequests.Should().HaveCount(
            2,
            "the boot page fetched first and the repair pass's hole window second");
        marketData.BarsWindowRequests[1].From.Should().Be(At(1).Time, "the hole's first missing open");
        marketData.BarsWindowRequests[1].To.Should().Be(At(2).Time, "the open right above the hole (exclusive)");
        marketData.BarsWindowRequests[1].Limit.Should().Be(
            CandleBackfillService.SegmentSizeBars,
            "one hole fits one segment inside a single adapter request");

        // Write-before-emit: the raw segment persisted before the merge
        // applies it (the T-085 discipline, continued behind the window).
        cache.Writes.Should().HaveCount(2, "the boot page and the repair segment both persist");
        cache.Writes[1].Bars.Should().ContainSingle().Which.Time.Should().Be(At(1).Time);
        cache.Sequence.Should().Contain("cache-probe", "the pass probes the cached coverage first");

        // The merge keeps every delivered bar and inserts the fetched one in
        // its calendar slot.
        chart.Bars.Should().HaveCount(3, "the delivered pair plus the fetched hole-filler");
        chart.Bars.Select(static bar => bar.Time).Should().Equal(
            At(0).Time,
            At(1).Time,
            At(2).Time);
        chart.Bars[1].Close.Should().Be(At(1).Close, "the merged bar carries the fetched values");

        healed.Should().ContainSingle("the pass publishes its additions once");
        healed[0].Should().ContainSingle().Which.Time.Should().Be(At(1).Time);

        chart.HistoryLimitNote.Should().BeNull("the feed still serves below the hole — no floor claim");
    }

    [Fact]
    public async Task TheRepairChain_TheOversizedHoleStaysThePagingMachinery()
    {
        var cache = new FakeCandleCache([]);
        var marketData = new FakeMarketData(barsPagePlan: [[At(0), At(1300)]]);
        var chart = CreateChart(
            CreateService(marketData, new FakeMarketData(id: "fake2", name: "Fake2")),
            candleCache: cache);
        var healed = new List<IReadOnlyList<Bar>>();

        using var healedWatch = chart.RepairedBarsUpdates.Subscribe(healed.Add);

        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        await AvaloniaDispatcherPump.DrainAsync();

        // 1299 hours are missing between the delivered pair — far over one
        // bounded pass's enumeration bound: the detector marks the run
        // truncated, the repair skips it and nothing fetches, writes or
        // merges (deeper history is the paging machinery's subject).
        marketData.BarsWindowRequests
            .Where(window => window.To is null)
            .Should().ContainSingle("only the boot fetch ran — the truncated hole stays the paging machinery's subject");
        cache.Writes.Should().ContainSingle("only the boot page persists");
        chart.Bars.Select(static bar => bar.Time).Should().Equal(At(0).Time, At(1300).Time);
        healed.Should().BeEmpty("a truncated hole never fills");
        chart.HistoryLimitNote.Should().BeNull("a truncation is not a floor claim");
    }

    [Fact]
    public async Task TheRepairChain_TheUnfillableHoleSurfacesTheLocalNote()
    {
        var cache = new FakeCandleCache([]);
        var marketData = new FakeMarketData(barsPagePlan: [[At(0), At(2)], []]);
        var localization = Substitute.For<ILocalizationManager>();
        localization.GetString(Arg.Any<string>(), Arg.Any<object[]>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}:{string.Join("|", call.ArgAt<object[]>(1))}]");
        var chart = CreateChart(
            CreateService(marketData, new FakeMarketData(id: "fake2", name: "Fake2")),
            localization: localization,
            candleCache: cache);
        var healed = new List<IReadOnlyList<Bar>>();

        using var healedWatch = chart.RepairedBarsUpdates.Subscribe(healed.Add);

        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        await AvaloniaDispatcherPump.DrainAsync();

        // The hole window asked the feed and it answered EMPTY: the chain
        // ends with the floor claim anchored at the merged series' oldest
        // open — the honest deep-history end, localized.
        chart.HistoryLimitNote.Should().Be("[Chart.HistoryLimit:2026-01-01 13:00 UTC]");
        chart.HasHistoryLimitNote.Should().BeTrue();
        marketData.BarsWindowRequests.Should().HaveCount(2, "the boot page and the hole window");
        cache.Writes.Should().ContainSingle("an empty answer persists nothing");
        chart.Bars.Select(static bar => bar.Time).Should().Equal(
            [At(0).Time, At(2).Time],
            "the view stands on the delivered series");
        healed.Should().BeEmpty("an empty answer fills nothing");
    }

    [Fact]
    public async Task TheRepairChain_ContiguousCachedBarsNeverDeepenTheView()
    {
        var cache = new FakeCandleCache([At(-1)]);
        var marketData = new FakeMarketData(barsPagePlan: [[At(0)]]);
        var chart = CreateChart(
            CreateService(marketData, new FakeMarketData(id: "fake2", name: "Fake2")),
            candleCache: cache);
        var healed = new List<IReadOnlyList<Bar>>();

        using var healedWatch = chart.RepairedBarsUpdates.Subscribe(healed.Add);

        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        await AvaloniaDispatcherPump.DrainAsync();

        // The cached 12:00 open sits contiguous with the delivered 13:00:
        // the refresh page's chain grew its candidate WITH it and re-probed
        // behind the grown head — but the displayed series stays the
        // delivered one.
        cache.Sequence.Count(marker => marker == "cache-probe").Should().BeGreaterThanOrEqualTo(
            2,
            "the second pass probes strictly below the candidate the cache already grew");
        marketData.BarsWindowRequests
            .Where(window => window.To is null)
            .Should().ContainSingle("no repair fetch fires for a hole the cache already covers");
        cache.Writes.Should().ContainSingle("only the boot page persists");
        chart.Bars.Select(static bar => bar.Time).Should().Equal(
            [At(0).Time],
            "the displayed series is the delivered page: cached coverage is context, never additions");
        healed.Should().BeEmpty("the additions are fetched hole-fill bars only");
        chart.HistoryLimitNote.Should().BeNull();
    }

    [Fact]
    public async Task ThePagedExhaustSurfacesTheNote_AndTheRebaseClearsIt()
    {
        var first = new FakeMarketData(barsPagePlan: [[At(0), At(1)], []]);
        var second = new FakeMarketData(
            barsPagePlan: [[At(2), At(3)]],
            id: "fake2",
            name: "Fake2");
        var localization = Substitute.For<ILocalizationManager>();
        localization.GetString(Arg.Any<string>(), Arg.Any<object[]>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}:{string.Join("|", call.ArgAt<object[]>(1))}]");
        var service = CreateService(first, second);
        var chart = CreateChart(service, localization: localization);
        var delivered = new List<IReadOnlyList<Bar>>();

        using var deliveredWatch = chart.BarsUpdates.Subscribe(delivered.Add);

        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        await AvaloniaDispatcherPump.DrainAsync();

        delivered.Should().ContainSingle("the cache is empty — the fetch page stands alone");
        first.BarsWindowRequests
            .Where(window => window.To is null)
            .Should().ContainSingle("only the boot page fetched before the paging request");
        chart.HistoryLimitNote.Should().BeNull("no paging ran yet — the deep-history edge is unknown");

        // The pan crossed the left edge: the older-window page answers empty
        // and the request path surfaces the loaded series' own depth edge,
        // localized.
        await chart.RequestOlderBarsAsync();

        first.BarsWindowRequests.Should().HaveCount(2, "the paging fetch carried the loaded series' head");
        first.BarsWindowRequests[1].To.Should().Be(At(0).Time, "the anchor is the loaded series' oldest open");
        chart.HistoryLimitNote.Should().Be("[Chart.HistoryLimit:2026-01-01 13:00 UTC]");
        chart.HasHistoryLimitNote.Should().BeTrue();

        // Act: a rebase (the exchange switch) delivers a fresh series — the
        // note drops with the replaced world.
        await service.SwitchToAsync("fake2");
        await AvaloniaDispatcherPump.DrainAsync();

        chart.HistoryLimitNote.Should().BeNull("the fresh series owns the note: the limit cleared with the rebase");
        chart.HasHistoryLimitNote.Should().BeFalse();
        second.BarsWindowRequests
            .Where(window => window.To is null)
            .Should().ContainSingle("the rebase fetched its own boot page and nothing else");
        delivered.Should().HaveCount(2, "the fresh delivery rode behind the first series' page");
        delivered[1].Select(static bar => bar.Time).Should().Equal(At(2).Time, At(3).Time);
    }

    #endregion Deep-history Repair (T-086)

    #region Chart Overlays (T-093)

    /// <summary>
    /// The persisted overlay choices apply at the startup walk and the walk
    /// never writes them back (the reads seed the persisted caches — the
    /// BUG-015 restore shape). Both choices restore independently: a stored
    /// off standing next to a stored on.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_RestoresThePersistedOverlayChoices_WithoutRewritingThem()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
        settings.GetAsync<bool>(Arg.Any<string>(), UiSettingsKeys.ChartShowVwap, Arg.Any<bool>())
            .Returns(false);
        settings.GetAsync<bool>(Arg.Any<string>(), UiSettingsKeys.ChartShowVolumeProfile, Arg.Any<bool>())
            .Returns(true);
        var chart = CreateChart(CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")), settings);

        chart.ShowVwap.Should().BeTrue("the default overlay stands until the walk applies the stored choice");
        chart.ShowVolumeProfile.Should().BeTrue();

        await chart.InitializeAsync();

        chart.ShowVwap.Should().BeFalse("the persisted vwap choice applies at the walk");
        chart.ShowVolumeProfile.Should().BeTrue("the persisted profile choice applies at the walk");
        await settings.DidNotReceive().SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    /// <summary>
    /// BUG-015's class, the overlay share of the restore matrix: the persisted
    /// choices apply in EVERY restore path — the time-frame restore's early
    /// returns (no persisted value, an unparsable one) must not skip them.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    public async Task InitializeAsync_AnUnrestoredTimeFrame_StillAppliesThePersistedOverlayChoices(string storedTimeFrame)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(storedTimeFrame);
        settings.GetAsync<bool>(Arg.Any<string>(), UiSettingsKeys.ChartShowVwap, Arg.Any<bool>())
            .Returns(false);
        settings.GetAsync<bool>(Arg.Any<string>(), UiSettingsKeys.ChartShowVolumeProfile, Arg.Any<bool>())
            .Returns(false);
        var chart = CreateChart(CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")), settings);

        await chart.InitializeAsync();

        chart.SelectedTimeFrame.Should().Be(TimeFrame.H1, "the unparsable time frame keeps the default");
        chart.ShowVwap.Should().BeFalse("the overlay restore is not gated on the time-frame restore");
        chart.ShowVolumeProfile.Should().BeFalse("the profile restore is not gated on the time-frame restore");
        await settings.DidNotReceive().SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    /// <summary>
    /// The toolbar toggle flips the choice and persists it — and a rapid
    /// settle burst never drops its second flip (the cache leads the
    /// pipeline at issue time, BUG-053's discipline): both flips' writes
    /// reach the service in issuance order.
    /// </summary>
    [Fact]
    public async Task ToggleVwapCommand_FlipsTheChoicePersistsIt_AndSurvivesARapidBurst()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
        settings.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(Task.CompletedTask);
        var chart = CreateChart(CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")), settings);

        await chart.ToggleVwapCommand.Execute(RxVoid.Default);

        chart.ShowVwap.Should().BeFalse("the toggle flips the standing enabled choice");
        await settings.Received().SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartShowVwap, false);

        await chart.ToggleVwapCommand.Execute(RxVoid.Default);

        chart.ShowVwap.Should().BeTrue("the second flip restores the choice");

        // The burst's second flip persisted too — the cache lead never dropped it.
        await settings.Received(2).SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartShowVwap,
            Arg.Any<bool>());
    }

    /// <summary>The volume-profile toggle: the same flip-and-persist lifecycle, its own key.</summary>
    [Fact]
    public async Task ToggleVolumeProfileCommand_FlipsTheChoiceAndPersistsIt()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
        settings.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(Task.CompletedTask);
        var chart = CreateChart(CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")), settings);

        await chart.ToggleVolumeProfileCommand.Execute(RxVoid.Default);

        chart.ShowVolumeProfile.Should().BeFalse("the toggle flips the standing enabled choice");
        await settings.Received().SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartShowVolumeProfile, false);
    }

    /// <summary>
    /// A settings-service change re-applies the overlay choices WITHOUT a
    /// restart: matching keys flip the properties (and the allow-cache runs
    /// ahead), an unrelated setting and a non-bool value are dropped — the
    /// applied choice stays in charge and no write echoes back.
    /// </summary>
    [Fact]
    public void TheSettingsServiceChange_AppliesTheOverlayChoicesLive_WithoutRewritingThem()
    {
        var changes = new Subject<SettingsChange>();
        var settings = Substitute.For<ISettingsService>();
        settings.Changes.Returns(changes);
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
        var chart = CreateChart(CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")), settings);

        changes.OnNext(new SettingsChange(UiSettingsKeys.Section, UiSettingsKeys.ChartShowVwap, true, false));

        chart.ShowVwap.Should().BeFalse("a settings change must reach the chart without a restart");

        changes.OnNext(new SettingsChange(UiSettingsKeys.Section, UiSettingsKeys.ChartShowVolumeProfile, true, false));
        changes.OnNext(new SettingsChange(UiSettingsKeys.Section, UiSettingsKeys.ChartTimeFrame, "H1", "H4"));
        changes.OnNext(new SettingsChange(UiSettingsKeys.Section, UiSettingsKeys.ChartShowVwap, false, "true"));

        chart.ShowVolumeProfile.Should().BeFalse("the second matching key applies too");
        chart.ShowVwap.Should().BeFalse("an unrelated key and a non-bool value both leave the choice alone");
    }

    /// <summary>
    /// The delivered series drives the published overlay (the raw stream the
    /// chart binds through the marshalled assignment): the computed values
    /// align with the delivered bars by index. The series carries volume, so
    /// the overlay is the computed state, stamped with the delivered page.
    /// </summary>
    [Fact]
    public async Task TheDeliveredSeries_PublishesTheComputedOverlayAlignedWithIt()
    {
        var marketData = new FakeMarketData();
        var chart = CreateChart(CreateService(marketData, new FakeMarketData(id: "fake2", name: "Fake2")));
        var overlays = new List<VwapOverlay?>();

        using var overlayWatch = chart.VwapOverlayUpdates.Subscribe(overlays.Add);

        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        await AvaloniaDispatcherPump.DrainAsync();

        var delivered = chart.Bars;
        delivered.Should().NotBeEmpty("the symbol selection delivered the boot page");
        overlays.Should().Contain(
            overlay => overlay != null &&
                       overlay.State == VwapOverlayState.Computed &&
                       overlay.Center.Count == delivered.Count,
            "the published values align with the delivered series by index");
    }

    /// <summary>
    /// Every toggle re-keys the published overlay against the CURRENT
    /// series: a flip to OFF publishes null (the chart draws nothing), a
    /// flip back ON recomputes over the standing series — never a stale
    /// value carried over from the hidden state.
    /// </summary>
    [Fact]
    public async Task TheVwapToggle_RecomputesThePublishedOverlayAgainstTheCurrentSeries()
    {
        var marketData = new FakeMarketData();
        var chart = CreateChart(CreateService(marketData, new FakeMarketData(id: "fake2", name: "Fake2")));
        var overlays = new List<VwapOverlay?>();

        using var overlayWatch = chart.VwapOverlayUpdates.Subscribe(overlays.Add);
        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        await AvaloniaDispatcherPump.DrainAsync();

        await chart.ToggleVwapCommand.Execute(RxVoid.Default);

        overlays.Last().Should().BeNull("the overlay off: the chart draws nothing");

        await chart.ToggleVwapCommand.Execute(RxVoid.Default);

        var refreshed = overlays.Last();
        refreshed.Should().NotBeNull("the flip back on recomputes against the standing series");
        refreshed!.Center.Should().HaveCount(chart.Bars.Count, "the recompute aligns with the standing series");
    }

    /// <summary>
    /// The persisted OFF choice keeps the overlays off at delivery: the
    /// restore seeded the cache, so the delivered series publishes null and
    /// the persisted ON choice publishes the computed overlay — each on its
    /// own read (the seeding is in charge end to end).
    /// </summary>
    [Fact]
    public async Task ThePersistedOffChoice_KeepsTheOverlaysOffAtDelivery()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
        settings.GetAsync<bool>(Arg.Any<string>(), UiSettingsKeys.ChartShowVwap, Arg.Any<bool>())
            .Returns(false);
        var chart = CreateChart(CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")), settings);
        var overlays = new List<VwapOverlay?>();

        using var overlayWatch = chart.VwapOverlayUpdates.Subscribe(overlays.Add);

        await chart.InitializeAsync();
        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        await AvaloniaDispatcherPump.DrainAsync();

        overlays.Should().OnlyContain(
            overlay => overlay == null,
            "the persisted off choice stands through the delivery");
    }

    #endregion Chart Overlays (T-093)

    #region Chart Type Selector (T-089)

    /// <summary>The open the chart-type facts' windows anchor at: a clean H1
    /// edge in March 2026, far away from every other fact's synthetic base
    /// time.</summary>
    private static readonly DateTimeOffset ChartTypeBaseTime = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// One synthetic H1 bar at <see cref="ChartTypeBaseTime"/> plus the given
    /// hour offset: OHLC values spread apart (100/110/90/104 family), so the
    /// Heikin-Ashi pooled values never coincide with the raw bar's — every
    /// transform value is assertable against the delivered series by value.
    /// </summary>
    private static Bar TypeBar(int index) => new(
        ChartTypeBaseTime.AddHours(index),
        100m + 2 * index,
        110m + 2 * index,
        90m + 2 * index,
        104m + 2 * index,
        5m);

    /// <summary>
    /// The type selector flips persist the WHOLE per-symbol map under the
    /// workspace symbol (the cache leads the pipeline, the map re-serializes
    /// on every flip): the composite shim forwards into the canonical type,
    /// a second addressed symbol lands its own entry next to the first one's,
    /// and every document is the exact map shape.
    /// </summary>
    [Fact]
    public async Task TheTypeSelectorFlip_PersistsTheWholeMapUnderTheWorkspaceSymbol()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
        settings.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);
        var chart = CreateChart(CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")), settings);

        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        chart.ChartType.Should().Be(ChartSeriesType.Candlesticks, "the candles default stands until the selector moves");
        chart.SelectedChartTypeIndex.Should().Be(
            (int)ChartSeriesType.Candlesticks,
            "the selector shim follows the canonical type");

        // The shim write: the combo's selected index forwards into ChartType.
        chart.SelectedChartTypeIndex = (int)ChartSeriesType.HeikinAshi;

        chart.ChartType.Should().Be(ChartSeriesType.HeikinAshi, "the shim forwards into the canonical type");
        chart.SelectedChartTypeIndex.Should().Be(
            (int)ChartSeriesType.HeikinAshi,
            "the shim's read follows the applied type");
        await settings.Received().SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartSymbolTypes,
            """{"BTC/USDT":"HeikinAshi"}""");

        // The canonical property write: the same pipeline, the entry replaced.
        chart.ChartType = ChartSeriesType.Area;

        await settings.Received().SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartSymbolTypes,
            """{"BTC/USDT":"Area"}""");

        chart.SelectedSymbol = Symbol.Parse("ETH/BTC");
        chart.ChartType = ChartSeriesType.Line;

        await settings.Received().SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartSymbolTypes,
            """{"BTC/USDT":"Area","ETH/BTC":"Line"}""");
    }

    /// <summary>
    /// The persisted choice applies at the symbol switch and the restore
    /// never rewrites the stored document (a restore is a read, not an edit
    /// — the BUG-015 shape the overlay choices keep): a symbol without an
    /// entry keeps the standing type — a preference map substitutes nothing
    /// — and the return to the stored symbol re-asserts the same choice.
    /// </summary>
    [Fact]
    public async Task ThePersistedType_RestoresOnTheSymbolSwitch_RewritesNothing()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
        settings.GetAsync<string>(Arg.Any<string>(), UiSettingsKeys.ChartSymbolTypes, Arg.Any<string>())
            .Returns("""{"BTC/USDT":"HeikinAshi"}""");
        settings.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);
        var chart = CreateChart(CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")), settings);

        chart.ChartType.Should().Be(ChartSeriesType.Candlesticks, "nothing is addressed at construction");

        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");

        chart.ChartType.Should().Be(ChartSeriesType.HeikinAshi, "the stored choice applies with the address");
        chart.SelectedChartTypeIndex.Should().Be(
            (int)ChartSeriesType.HeikinAshi,
            "the shim follows the restored type");

        chart.SelectedSymbol = Symbol.Parse("ETH/BTC");

        chart.ChartType.Should().Be(
            ChartSeriesType.HeikinAshi,
            "a symbol without an entry keeps the standing type — the map substitutes nothing");

        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");

        chart.ChartType.Should().Be(ChartSeriesType.HeikinAshi, "the return re-asserts the stored choice");
        await settings.DidNotReceive().SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartSymbolTypes,
            Arg.Any<string>());
    }

    /// <summary>
    /// A flip with no workspace symbol addressed stays transient: no map
    /// document can be attached to a context, so nothing persists — and the
    /// standing pick carries over to the first addressed symbol until that
    /// symbol's own entry exists (the persist begins where the context does).
    /// </summary>
    [Fact]
    public async Task TheFlipWithoutAWorkspaceSymbol_PersistsNothing_UntilAContextIsAddressed()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
        settings.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);
        var chart = CreateChart(CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")), settings);

        chart.SelectedSymbol.Should().BeNull("the posted bootstrap apply never pumps in a fact");

        chart.ChartType = ChartSeriesType.Bars;

        chart.ChartType.Should().Be(ChartSeriesType.Bars, "the pick applies to the standing type");
        await settings.DidNotReceive().SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartSymbolTypes,
            Arg.Any<string>());

        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");

        chart.ChartType.Should().Be(
            ChartSeriesType.Bars,
            "the standing pick carries over — the empty stored map substitutes nothing");

        chart.ChartType = ChartSeriesType.Line;

        await settings.Received().SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartSymbolTypes,
            """{"BTC/USDT":"Line"}""");
    }

    /// <summary>
    /// The Heikin-Ashi type publishes the computed series against the CURRENT
    /// series (raw, marshal-invariant — the overlay publishes' discipline):
    /// aligned with the delivered bars by index and carrying the transform's
    /// pooled values, while every other type owns the null publish (the chart
    /// draws its own variant from the raw bars).
    /// </summary>
    [Fact]
    public async Task TheHeikinAshiType_PublishesTheSeriesAlignedWithTheDelivery_AndOthersPublishNull()
    {
        var marketData = new FakeMarketData(
            barsPagePlan: [[TypeBar(0), TypeBar(1), TypeBar(2)]]);
        var chart = CreateChart(CreateService(marketData, new FakeMarketData(id: "fake2", name: "Fake2")));
        var publishes = new List<IReadOnlyList<Bar>?>();

        using var publishWatch = chart.HeikinAshiBarsUpdates.Subscribe(publishes.Add);

        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        await AvaloniaDispatcherPump.DrainAsync();

        var delivered = chart.Bars;
        delivered.Select(static bar => bar.Time).Should().Equal(
            [TypeBar(0).Time, TypeBar(1).Time, TypeBar(2).Time],
            "the delivered page is the planned series");
        publishes.Should().OnlyContain(
            publish => publish == null,
            "the chart draws its own candlesticks until the series type moves");

        chart.ChartType = ChartSeriesType.HeikinAshi;

        var published = publishes.Last();
        published.Should().NotBeNull("the flip publishes the transform immediately against the standing series");
        var aligned = published!;
        aligned.Select(static bar => bar.Time).Should().Equal(
            delivered.Select(static bar => bar.Time),
            "the HA series aligns with the loaded series by index — the chart slices it with the same window");
        aligned[0].Open.Should().Be(102m, "the seed pools the first open with the close — not the raw open");
        aligned[0].Close.Should().Be(101m, "the four-price mean of the first bar");
        aligned[1].Open.Should().Be(101.5m, "the chain pools with the previous HA open and close");

        chart.ChartType = ChartSeriesType.Bars;

        publishes.Last().Should().BeNull("a non-Heikin-Ashi type owns the null publish again");
    }

    /// <summary>
    /// A settings-service change to the types map re-addresses the shown
    /// symbol live (no restart): the matching entry applies under the restore
    /// gate, and a document that addresses only other symbols leaves the
    /// standing choice alone — the applied choice stays in charge.
    /// </summary>
    [Fact]
    public void TheSettingsServiceChange_ReAddressesTheShownSymbolLive()
    {
        var changes = new Subject<SettingsChange>();
        var settings = Substitute.For<ISettingsService>();
        settings.Changes.Returns(changes);
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
        var chart = CreateChart(CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")), settings);

        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        chart.ChartType.Should().Be(ChartSeriesType.Candlesticks);

        changes.OnNext(new SettingsChange(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartSymbolTypes,
            UiSettingsKeys.DefaultChartSymbolTypes,
            """{"BTC/USDT":"HeikinAshi"}"""));

        chart.ChartType.Should().Be(ChartSeriesType.HeikinAshi, "a settings change must reach the chart without a restart");

        changes.OnNext(new SettingsChange(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartSymbolTypes,
            """{"BTC/USDT":"HeikinAshi"}""",
            """{"ETH/BTC":"Line"}"""));

        chart.ChartType.Should().Be(
            ChartSeriesType.HeikinAshi,
            "a document that addresses only other symbols leaves the standing choice alone");
    }

    #endregion Chart Type Selector (T-089)

    #region Scale Mode Selector (T-090)

    /// <summary>
    /// The scale-mode flips persist the WHOLE per-symbol map under the
    /// workspace symbol (the same cache-leads pipeline the chart types
    /// keep): the composite shim forwards into the canonical mode, a second
    /// addressed symbol lands its own entry next to the first one's, and
    /// every document is the exact map shape.
    /// </summary>
    [Fact]
    public async Task TheScaleModeFlip_PersistsTheWholeMapUnderTheWorkspaceSymbol()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
        settings.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);
        var chart = CreateChart(CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")), settings);

        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        chart.ScaleMode.Should().Be(PriceScaleMode.Linear, "the linear default stands until the selector moves");
        chart.SelectedScaleModeIndex.Should().Be(
            (int)PriceScaleMode.Linear,
            "the selector shim follows the canonical mode");

        // The shim write: the combo's selected index forwards into ScaleMode.
        chart.SelectedScaleModeIndex = (int)PriceScaleMode.Logarithmic;

        chart.ScaleMode.Should().Be(PriceScaleMode.Logarithmic, "the shim forwards into the canonical mode");
        chart.SelectedScaleModeIndex.Should().Be(
            (int)PriceScaleMode.Logarithmic,
            "the shim's read follows the applied mode");
        await settings.Received().SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartSymbolScales,
            """{"BTC/USDT":"Logarithmic"}""");

        // The canonical property write: the same pipeline, the entry replaced.
        chart.ScaleMode = PriceScaleMode.Percent;

        await settings.Received().SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartSymbolScales,
            """{"BTC/USDT":"Percent"}""");

        chart.SelectedSymbol = Symbol.Parse("ETH/BTC");
        chart.ScaleMode = PriceScaleMode.Linear;

        await settings.Received().SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartSymbolScales,
            """{"BTC/USDT":"Percent","ETH/BTC":"Linear"}""");
    }

    /// <summary>
    /// The persisted mode applies at the symbol switch and the restore never
    /// rewrites the stored document (a restore is a read, not an edit — the
    /// discipline the chart types keep): a symbol without an entry keeps the
    /// standing mode — a preference map substitutes nothing — and the return
    /// to the stored symbol re-asserts the same mode. The mode switch itself
    /// is mode-foreign to the load machine: the visible window's bars stay
    /// addressed, only the axis re-maps.
    /// </summary>
    [Fact]
    public async Task ThePersistedMode_RestoresOnTheSymbolSwitch_RewritesNothing()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
        settings.GetAsync<string>(Arg.Any<string>(), UiSettingsKeys.ChartSymbolScales, Arg.Any<string>())
            .Returns("""{"BTC/USDT":"Logarithmic"}""");
        settings.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);
        var chart = CreateChart(CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")), settings);

        chart.ScaleMode.Should().Be(PriceScaleMode.Linear, "nothing is addressed at construction");

        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");

        chart.ScaleMode.Should().Be(PriceScaleMode.Logarithmic, "the stored mode applies with the address");
        chart.SelectedScaleModeIndex.Should().Be(
            (int)PriceScaleMode.Logarithmic,
            "the shim follows the restored mode");

        chart.SelectedSymbol = Symbol.Parse("ETH/BTC");

        chart.ScaleMode.Should().Be(
            PriceScaleMode.Logarithmic,
            "a symbol without an entry keeps the standing mode — the map substitutes nothing");

        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");

        chart.ScaleMode.Should().Be(PriceScaleMode.Logarithmic, "the return re-asserts the stored mode");
        await settings.DidNotReceive().SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartSymbolScales,
            Arg.Any<string>());
    }

    /// <summary>
    /// A settings-service change to the scale-modes map re-addresses the
    /// shown symbol live (no restart): the matching entry applies under the
    /// restore gate, and a document that addresses only other symbols leaves
    /// the standing mode alone — the applied mode stays in charge.
    /// </summary>
    [Fact]
    public void TheSettingsServiceChange_ReAddressesTheShownModeLive()
    {
        var changes = new Subject<SettingsChange>();
        var settings = Substitute.For<ISettingsService>();
        settings.Changes.Returns(changes);
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
        var chart = CreateChart(CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")), settings);

        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        chart.ScaleMode.Should().Be(PriceScaleMode.Linear);

        changes.OnNext(new SettingsChange(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartSymbolScales,
            UiSettingsKeys.DefaultChartSymbolScales,
            """{"BTC/USDT":"Percent"}"""));

        chart.ScaleMode.Should().Be(PriceScaleMode.Percent, "a settings change must reach the chart without a restart");

        changes.OnNext(new SettingsChange(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartSymbolScales,
            """{"BTC/USDT":"Percent"}""",
            """{"ETH/BTC":"Logarithmic"}"""));

        chart.ScaleMode.Should().Be(
            PriceScaleMode.Percent,
            "a document that addresses only other symbols leaves the standing mode alone");
    }

    #endregion Scale Mode Selector (T-090)

    #region Symbol Compare (T-124)

    /// <summary>The fake compiler: no-arg refusals print <c>[key]</c>, arg-carrying faults print <c>[key:v1|v2]</c>.</summary>
    private static ILocalizationManager LocalizationThatPrints()
    {
        var localization = Substitute.For<ILocalizationManager>();
        localization.GetString(Arg.Any<string>()).Returns(call => $"[{call.ArgAt<string>(0)}]");
        localization.GetString(Arg.Any<string>(), Arg.Any<object[]>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}:{string.Join("|", call.ArgAt<object[]>(1))}]");
        return localization;
    }

    /// <summary>
    /// A handler that answers the chart's compare pick with one symbol —
    /// the palette ride's stand-in (the window's real ride is pinned by
    /// the palette facts).
    /// </summary>
    private static IDisposable PickHandler(ChartViewModel chart, string symbol) =>
        chart.PickCompareRequested.RegisterHandler(context =>
        {
            context.SetOutput(Symbol.Parse(symbol));
            return Task.CompletedTask;
        });

    /// <summary>
    /// The compare flow with nothing loaded refuses BEFORE any fetch: no
    /// bars window request happens and the localized refusal surfaces on
    /// the error line — never a silent substitution.
    /// </summary>
    [Fact]
    public async Task CompareCommand_WithoutALoadedSeries_RefusesLocalized()
    {
        var localization = LocalizationThatPrints();
        var marketData = new FakeMarketData();
        var chart = CreateChart(
            CreateService(marketData, new FakeMarketData(id: "fake2", name: "Fake2")),
            localization: localization);

        using var pick = PickHandler(chart, "ETH/BTC");
        await chart.CompareCommand.Execute(RxVoid.Default);

        chart.ErrorMessage.Should().Be("[Chart.CompareNoSeries]");
        chart.HasErrorMessage.Should().BeTrue();
        chart.CompareLayers.Should().BeEmpty("the refusal adds no layer");
        marketData.BarsRequested.Should().NotContain(Symbol.Parse("ETH/BTC"), "the refusal fetches nothing");
    }

    /// <summary>A closed palette is not an error: the pick ends null, nothing happens, the error line stays clean.</summary>
    [Fact]
    public async Task CompareCommand_WithAClosedPalette_EndsQuietly()
    {
        var chart = CreateChart(
            CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")));

        using var pick = chart.PickCompareRequested.RegisterHandler(context =>
        {
            context.SetOutput(null);
            return Task.CompletedTask;
        });
        await chart.CompareCommand.Execute(RxVoid.Default);

        chart.ErrorMessage.Should().BeNull("a closed palette is the user's choice, not a fault");
        chart.CompareLayers.Should().BeEmpty();
    }

    /// <summary>
    /// The picked symbol lands the second series: the fetch reads the SAME
    /// standing interval over the standing series' span (plus the pad),
    /// and the layer rides the chart with the picked symbol's normalized
    /// name — the chip label and the removal identity at once.
    /// </summary>
    [Fact]
    public async Task ComparePick_LandsASecondSeries_OnTheStandingSpan()
    {
        var bootPage = new[]
        {
            new Bar(new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero), 99m, 105m, 97m, 104m, 5m),
            new Bar(new DateTimeOffset(2026, 11, 9, 1, 0, 0, TimeSpan.Zero), 104m, 112m, 102m, 111m, 7m),
            new Bar(new DateTimeOffset(2026, 11, 9, 2, 0, 0, TimeSpan.Zero), 111m, 120m, 109m, 118m, 9m),
        };
        var comparePage = new[]
        {
            new Bar(new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero), 199m, 206m, 197m, 200m, 3m),
            new Bar(new DateTimeOffset(2026, 11, 9, 1, 0, 0, TimeSpan.Zero), 200m, 210m, 198m, 202m, 3m),
            new Bar(new DateTimeOffset(2026, 11, 9, 2, 0, 0, TimeSpan.Zero), 202m, 214m, 200m, 204m, 3m),
        };
        var marketData = new FakeMarketData(barsPagePlan: [bootPage, comparePage]);
        var chart = CreateChart(
            CreateService(marketData, new FakeMarketData(id: "fake2", name: "Fake2")));
        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        await AvaloniaDispatcherPump.DrainAsync();

        using var pick = PickHandler(chart, "ETH/BTC");
        await chart.CompareCommand.Execute(RxVoid.Default);

        marketData.BarsRequested[^1].Should().Be(Symbol.Parse("ETH/BTC"));
        marketData.BarsTimeFrames[^1].Should().Be(TimeFrame.H1, "the compare fetch reads the standing interval");
        var window = marketData.BarsWindowRequests[^1];
        window.Limit.Should().Be(chart.Bars.Count + 2, "the standing span with the one pad-slot headroom");
        window.From.Should().Be(chart.Bars[0].Time, "the fetch anchors on the visible window's head");
        chart.CompareLayers.Should().ContainSingle().Which.Label.Should().Be("ETH/BTC");
        chart.CompareLayers.Should().ContainSingle().Which.Bars.Should()
            .BeEquivalentTo(comparePage, "the layer keeps its own fetched series");
    }

    /// <summary>The chart's own symbol cannot compare against itself — the refusal diagnoses at the pick.</summary>
    [Fact]
    public async Task AddCompareAsync_TheChartOwnSymbol_RefusesLocalized()
    {
        var localization = LocalizationThatPrints();
        var chart = CreateChart(
            CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")),
            localization: localization);
        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        await AvaloniaDispatcherPump.DrainAsync();

        await chart.AddCompareAsync(Symbol.Parse("BTC/USDT"));

        chart.ErrorMessage.Should().Be("[Chart.CompareSelf]");
        chart.CompareLayers.Should().BeEmpty();
    }

    /// <summary>A duplicate pick refuses and keeps the standing single layer — the chip strip stays one-chip-per-symbol.</summary>
    [Fact]
    public async Task AddCompareAsync_ADuplicateSymbol_RefusesLocalized()
    {
        var localization = LocalizationThatPrints();
        var chart = CreateChart(
            CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")),
            localization: localization);
        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        await AvaloniaDispatcherPump.DrainAsync();
        await chart.AddCompareAsync(Symbol.Parse("ETH/BTC"));
        chart.CompareLayers.Should().ContainSingle();

        await chart.AddCompareAsync(Symbol.Parse("ETH/BTC"));

        chart.ErrorMessage.Should().Be("[Chart.CompareDuplicate]");
        chart.CompareLayers.Should().ContainSingle("the duplicate never appended twice");
    }

    /// <summary>The layer cap refuses the fifth pick with the carried limit — the strip's whole row budget.</summary>
    [Fact]
    public async Task AddCompareAsync_AtTheCap_RefusesLocalized()
    {
        var localization = LocalizationThatPrints();
        var marketData = new FakeMarketData(
            symbolNames: ["BTC/USDT", "ETH/BTC", "SOL/USDC", "XRP/USDT", "ADA/USDT", "DOGE/USDT"]);
        var chart = CreateChart(
            CreateService(marketData, new FakeMarketData(id: "fake2", name: "Fake2")),
            localization: localization);
        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        await AvaloniaDispatcherPump.DrainAsync();

        string[] picks = ["ETH/BTC", "SOL/USDC", "XRP/USDT", "ADA/USDT"];
        foreach (var pick in picks)
        {
            await chart.AddCompareAsync(Symbol.Parse(pick));
        }

        chart.CompareLayers.Should().HaveCount(
            ChartCompareSeries.MaxCompareLayers,
            "the standing layers fill the strip");

        var overflowPick = Symbol.Parse("DOGE/USDT");
        await chart.AddCompareAsync(overflowPick);

        chart.ErrorMessage.Should().Be("[Chart.CompareCap:4]");
        chart.CompareLayers.Should().HaveCount(4, "the refusal leaves the standing set");
    }

    /// <summary>A fetch fault surfaces the localized message with the carried detail — never a silent drop of the user's intent.</summary>
    [Fact]
    public async Task AddCompareAsync_WhenTheFetchFaults_RefusesLocalized()
    {
        var localization = LocalizationThatPrints();
        var marketData = new FakeMarketData(failingSymbols: ["ETH/BTC"]);
        var chart = CreateChart(
            CreateService(marketData, new FakeMarketData(id: "fake2", name: "Fake2")),
            localization: localization);
        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        await AvaloniaDispatcherPump.DrainAsync();

        await chart.AddCompareAsync(Symbol.Parse("ETH/BTC"));

        chart.ErrorMessage.Should().Be("[Chart.CompareFetchFailed:no bars for this symbol]");
        chart.CompareLayers.Should().BeEmpty();
    }

    /// <summary>An empty fetch (the feed served nothing for the picked symbol) refuses on the error line too.</summary>
    [Fact]
    public async Task AddCompareAsync_WithAnEmptyFetch_RefusesLocalized()
    {
        var bootPage = new[]
        {
            new Bar(new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero), 99m, 105m, 97m, 104m, 5m),
            new Bar(new DateTimeOffset(2026, 11, 9, 1, 0, 0, TimeSpan.Zero), 104m, 112m, 102m, 111m, 7m),
        };
        var marketData = new FakeMarketData(barsPagePlan: [bootPage, []]);
        var chart = CreateChart(
            CreateService(marketData, new FakeMarketData(id: "fake2", name: "Fake2")),
            localization: LocalizationThatPrints());
        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        await AvaloniaDispatcherPump.DrainAsync();

        await chart.AddCompareAsync(Symbol.Parse("ETH/BTC"));

        chart.ErrorMessage.Should().Be("[Chart.CompareFetchEmpty]");
        chart.CompareLayers.Should().BeEmpty();
    }

    /// <summary>
    /// The removal drops only the named layer and restores the standing
    /// series records untouched — the removal-restore identity's
    /// ViewModel leg: the kept list is the ORIGINAL layer records minus
    /// the named one, re-published; an unknown label removes nothing.
    /// </summary>
    [Fact]
    public async Task RemoveCompare_DropsOnlyTheNamedLayer_AndUnknownLabelIsANoOp()
    {
        var chart = CreateChart(
            CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")));
        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        await AvaloniaDispatcherPump.DrainAsync();
        await chart.AddCompareAsync(Symbol.Parse("ETH/BTC"));
        await chart.AddCompareAsync(Symbol.Parse("SOL/USDC"));
        var ethLayer = chart.CompareLayers[0];
        var solLayer = chart.CompareLayers[1];

        chart.RemoveCompare("ETH/BTC");
        chart.CompareLayers.Should().HaveCount(1);
        chart.CompareLayers[0].Should().BeSameAs(solLayer, "the removal drops the named layer only");

        chart.RemoveCompare("DOGE/USDT");
        chart.CompareLayers.Should().HaveCount(1, "an unknown label removes nothing");
        chart.CompareLayers[0].Should().BeSameAs(solLayer);
    }

    /// <summary>
    /// The compare layers are chart-level state keyed to the triple: an
    /// INTERVAL change (a different open-time grid) drops the lot, but a
    /// same-interval symbol switch keeps them — the layers re-align
    /// against the new series' window on the next pass.
    /// </summary>
    [Fact]
    public async Task AnIntervalChange_DropsTheLayers_AndASameIntervalSymbolSwitch_KeepsThem()
    {
        var chart = CreateChart(
            CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")));
        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        await AvaloniaDispatcherPump.DrainAsync();
        await chart.AddCompareAsync(Symbol.Parse("ETH/BTC"));
        chart.CompareLayers.Should().ContainSingle();

        chart.SelectedTimeFrame = TimeFrame.M5;
        await AvaloniaDispatcherPump.DrainAsync();
        chart.CompareLayers.Should().BeEmpty("a new open-time grid takes no riders");

        await chart.AddCompareAsync(Symbol.Parse("SOL/USDC"));
        chart.CompareLayers.Should().ContainSingle().Which.Label.Should().Be("SOL/USDC");

        chart.SelectedSymbol = Symbol.Parse("SOL/USDC");
        await AvaloniaDispatcherPump.DrainAsync();
        chart.CompareLayers.Should().ContainSingle().Which.Label.Should().Be(
            "SOL/USDC",
            "a same-interval symbol switch keeps the layers");
    }

    /// <summary>The chip strip's main label mirrors the workspace selection live (empty while none stands).</summary>
    [Fact]
    public async Task MainSeriesLabel_MirrorsTheWorkspaceSelection()
    {
        var chart = CreateChart(
            CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")));
        chart.MainSeriesLabel.Should().BeEmpty("no selection stands yet");
        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        await AvaloniaDispatcherPump.DrainAsync();

        chart.MainSeriesLabel.Should().Be("BTC/USDT");
    }

    #endregion Symbol Compare (T-124)

    #region Drawing Actions (T-091)

    /// <summary>
    /// The IChartDrawingStore fake for the drawings facts: a scripted layer
    /// queue answers the loads (exhaustion answers an empty layer), every
    /// save is recorded and its answer echoes the caller's layer (the
    /// store's naming reality in the happy path), and fault hooks stand
    /// both arms ready.
    /// </summary>
    private sealed class FakeDrawingStore(IReadOnlyList<ChartDrawing>[]? layerPlan = null)
        : IChartDrawingStore
    {
        private readonly Queue<IReadOnlyList<ChartDrawing>> _layers = new(layerPlan ?? []);

        /// <summary>The recorded saves: the key plus the pushed layer, in call order.</summary>
        public List<(string AdapterId, string Symbol, TimeFrame TimeFrame, IReadOnlyList<ChartDrawing> Layer)> Saves { get; } = [];

        /// <summary>The recorded load keys, in call order.</summary>
        public List<(string AdapterId, string Symbol, TimeFrame TimeFrame)> Loads { get; } = [];

        /// <summary>When set, every save faults.</summary>
        public Exception? SaveFault { get; set; }

        /// <summary>When set, every load faults.</summary>
        public Exception? LoadFault { get; set; }

        public async Task<IReadOnlyList<ChartDrawing>> LoadAsync(
            string adapterId,
            string symbol,
            TimeFrame timeFrame,
            CancellationToken cancellationToken = default)
        {
            Loads.Add((adapterId, symbol, timeFrame));
            if (LoadFault is { } fault)
            {
                throw fault;
            }

            if (_layers.TryDequeue(out var layer))
            {
                return layer;
            }

            return [];
        }

        public async Task<IReadOnlyList<ChartDrawing>> SaveAsync(
            string adapterId,
            string symbol,
            TimeFrame timeFrame,
            IReadOnlyList<ChartDrawing> drawings,
            CancellationToken cancellationToken = default)
        {
            Saves.Add((adapterId, symbol, timeFrame, drawings));
            if (SaveFault is { } fault)
            {
                throw fault;
            }

            return drawings;
        }
    }

    /// <summary>The palette arms a tool, switches between tools and toggles the armed one off in place.</summary>
    [Fact]
    public async Task ToolCommand_ArmsSwitchesAndTogglesOff()
    {
        var chart = CreateChart(
            CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")));

        await chart.ToolCommand.Execute(ChartDrawingTool.TrendLine);
        chart.ActiveDrawingTool.Should().Be(ChartDrawingTool.TrendLine);
        chart.IsTrendLineToolActive.Should().BeTrue();
        chart.IsLevelToolActive.Should().BeFalse();
        chart.IsFibToolActive.Should().BeFalse();

        await chart.ToolCommand.Execute(ChartDrawingTool.FibRetracement);
        chart.ActiveDrawingTool.Should().Be(ChartDrawingTool.FibRetracement);
        chart.IsTrendLineToolActive.Should().BeFalse();
        chart.IsFibToolActive.Should().BeTrue();

        await chart.ToolCommand.Execute(ChartDrawingTool.FibRetracement);
        chart.ActiveDrawingTool.Should().Be(ChartDrawingTool.None, "the same-tool press toggles off");
        chart.IsFibToolActive.Should().BeFalse();

        await chart.ToolCommand.Execute(ChartDrawingTool.HorizontalLevel);
        chart.IsLevelToolActive.Should().BeTrue();
    }

    /// <summary>The wave-2 tools ride the same arming machine: each arms, switches and toggles off.</summary>
    [Fact]
    public async Task ToolCommand_Wave2Tools_ArmSwitchAndToggleOff()
    {
        var chart = CreateChart(
            CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")));

        await chart.ToolCommand.Execute(ChartDrawingTool.Zone);
        chart.ActiveDrawingTool.Should().Be(ChartDrawingTool.Zone);
        chart.IsZoneToolActive.Should().BeTrue();
        chart.IsTrendLineToolActive.Should().BeFalse();
        chart.IsTextToolActive.Should().BeFalse();
        chart.IsMeasureToolActive.Should().BeFalse();

        await chart.ToolCommand.Execute(ChartDrawingTool.Zone);
        chart.ActiveDrawingTool.Should().Be(ChartDrawingTool.None, "the same-tool press toggles off");

        await chart.ToolCommand.Execute(ChartDrawingTool.Text);
        chart.IsTextToolActive.Should().BeTrue();
        chart.IsZoneToolActive.Should().BeFalse("the arm switches between the wave's tools");

        await chart.ToolCommand.Execute(ChartDrawingTool.Measure);
        chart.IsMeasureToolActive.Should().BeTrue();
        chart.IsTextToolActive.Should().BeFalse();
    }

    /// <summary>A placement's commit routes the whole-layer save under the standing key and republishes the store's answer.</summary>
    [Fact]
    public async Task CommitDrawing_SavesTheLayer_UnderTheStandingKey()
    {
        var store = new FakeDrawingStore();
        var chart = CreateChartWithStore(
            CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")),
            store);
        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        await AvaloniaDispatcherPump.DrainAsync();

        var anchors = new[]
        {
            new ChartDrawingAnchor(new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero), 100m),
            new ChartDrawingAnchor(new DateTimeOffset(2026, 11, 9, 2, 0, 0, TimeSpan.Zero), 120m),
        };
        await chart.CommitDrawingAsync(new ChartDrawingEventArgs(null, ChartDrawingKind.TrendLine, anchors));

        chart.Drawings.Should().ContainSingle()
            .Which.Anchors.Should().BeEquivalentTo(anchors, "the store's answer republishes as the layer");
        var (_, savedSymbol, savedTimeFrame, savedLayer) = store.Saves.Should().ContainSingle().Which;
        savedSymbol.Should().Be("BTC/USDT");
        savedTimeFrame.Should().Be(TimeFrame.H1, "the standing interval owns the key");
        savedLayer.Should().ContainSingle()
            .Which.Kind.Should().Be(ChartDrawingKind.TrendLine);
    }

    /// <summary>
    /// The text annotation's commit prompts the view first (T-200): the
    /// answer becomes the annotation's string — trimmed — and the layer
    /// saves with the label riding it.
    /// </summary>
    [Fact]
    public async Task CommitTextAnnotation_PromptsThenSavesTheText()
    {
        var store = new FakeDrawingStore();
        var chart = CreateChartWithStore(
            CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")),
            store);
        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        await AvaloniaDispatcherPump.DrainAsync();

        using var prompt = chart.TextAnnotationRequested.RegisterHandler(context =>
        {
            context.SetOutput("  supply above  ");
            return Task.CompletedTask;
        });
        var anchors = new[]
        {
            new ChartDrawingAnchor(new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero), 100m),
        };
        await chart.CommitDrawingAsync(new ChartDrawingEventArgs(null, ChartDrawingKind.Text, anchors));

        chart.Drawings.Should().ContainSingle().Which.Text
            .Should().Be("supply above", "the prompt's answer labels the annotation, trimmed");
        store.Saves.Should().ContainSingle().Which.Layer.Should().ContainSingle().Which.Text
            .Should().Be("supply above", "the label rides the whole-layer save");
    }

    /// <summary>
    /// A cancelled or an empty answer places nothing: the annotation never
    /// persists unlabelled, the layer keeps its standing shape and no save
    /// fires.
    /// </summary>
    [Fact]
    public async Task CommitTextAnnotation_AnEmptyOrCancelledAnswer_PlacesNothing()
    {
        var store = new FakeDrawingStore();
        var chart = CreateChartWithStore(
            CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")),
            store);
        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        await AvaloniaDispatcherPump.DrainAsync();

        var anchors = new[]
        {
            new ChartDrawingAnchor(new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero), 100m),
        };

        using (chart.TextAnnotationRequested.RegisterHandler(context =>
        {
            context.SetOutput(null);
            return Task.CompletedTask;
        }))
        {
            await chart.CommitDrawingAsync(new ChartDrawingEventArgs(null, ChartDrawingKind.Text, anchors));
        }

        using (chart.TextAnnotationRequested.RegisterHandler(context =>
        {
            context.SetOutput("   ");
            return Task.CompletedTask;
        }))
        {
            await chart.CommitDrawingAsync(new ChartDrawingEventArgs(null, ChartDrawingKind.Text, anchors));
        }

        chart.Drawings.Should().BeEmpty("an annotation without a label is no annotation");
        store.Saves.Should().BeEmpty("nothing was placed, nothing was saved");
    }

    /// <summary>A commit before any load has no key to anchor to — it drops quietly, saving nothing.</summary>
    [Fact]
    public async Task CommitDrawing_WithoutALoadedPage_DropsQuietly()
    {
        var chart = CreateChart(
            CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")));

        await chart.CommitDrawingAsync(new ChartDrawingEventArgs(
            null,
            ChartDrawingKind.TrendLine,
            [new ChartDrawingAnchor(new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero), 100m),
             new ChartDrawingAnchor(new DateTimeOffset(2026, 11, 9, 2, 0, 0, TimeSpan.Zero), 120m)]));

        chart.Drawings.Should().BeEmpty();
    }

    /// <summary>A commit carrying degenerate geometry is refused before any store round-trip.</summary>
    [Fact]
    public async Task CommitDrawing_DegenerateGeometry_RefusesBeforeTheStore()
    {
        var chart = CreateChart(
            CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")));
        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        await AvaloniaDispatcherPump.DrainAsync();

        await chart.CommitDrawingAsync(new ChartDrawingEventArgs(
            null,
            ChartDrawingKind.TrendLine,
            [new ChartDrawingAnchor(new DateTimeOffset(2026, 11, 9, 2, 0, 0, TimeSpan.Zero), 100m),
             new ChartDrawingAnchor(new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero), 120m)]));

        chart.Drawings.Should().BeEmpty("refused geometry never reaches the layer");
    }

    /// <summary>The delivered page mirrors its key's persisted layer, and the rebase drops rides with the fresh key.</summary>
    [Fact]
    public async Task TheDeliveredPage_MirrorsTheKeyLayer_AndTheRebaseReplacesIt()
    {
        var scripted = new ChartDrawing
        {
            Kind = ChartDrawingKind.HorizontalLevel,
            Anchors = [new ChartDrawingAnchor(new DateTimeOffset(2026, 11, 9, 1, 0, 0, TimeSpan.Zero), 95m)],
        };
        var store = new FakeDrawingStore([[scripted]]);
        var chart = CreateChartWithStore(
            CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")),
            store);
        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        await AvaloniaDispatcherPump.DrainAsync();

        chart.Drawings.Should().ContainSingle().Which.Id.Should().Be(scripted.Id);

        chart.SelectDrawing(scripted.Id);
        chart.SelectedDrawingId.Should().Be(scripted.Id);

        chart.SelectedTimeFrame = TimeFrame.M5;
        await AvaloniaDispatcherPump.DrainAsync();

        chart.Drawings.Should().BeEmpty("the fresh key's own layer is what stands");
        chart.SelectedDrawingId.Should().BeNull("selections belong to their layer, never cross keys");
    }

    /// <summary>A faulted drawings load stands the layer empty and never breaks the page delivery.</summary>
    [Fact]
    public async Task AFaultedDrawingsLoad_StandsEmpty_AndThePageStillDelivers()
    {
        var chart = CreateChartWithStore(
            CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")),
            new FakeDrawingStore { LoadFault = new InvalidOperationException("db locked") });
        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        await AvaloniaDispatcherPump.DrainAsync();

        chart.Bars.Should().NotBeEmpty("the bars load is never the drawings' hostage");
        chart.Drawings.Should().BeEmpty("the fault stands the layer empty honestly");
    }

    /// <summary>An edit replaces the named drawing's anchors; a stale name drops without saving.</summary>
    [Fact]
    public async Task EditDrawing_ReplacesTheNamedAnchors_AndStaleNamesDropQuietly()
    {
        var scripted = new ChartDrawing
        {
            Kind = ChartDrawingKind.HorizontalLevel,
            Anchors = [new ChartDrawingAnchor(new DateTimeOffset(2026, 11, 9, 1, 0, 0, TimeSpan.Zero), 95m)],
        };
        var store = new FakeDrawingStore([[scripted]]);
        var chart = CreateChartWithStore(
            CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")),
            store);
        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        await AvaloniaDispatcherPump.DrainAsync();

        await chart.EditDrawingAsync(new ChartDrawingEventArgs(
            scripted.Id,
            ChartDrawingKind.HorizontalLevel,
            [new ChartDrawingAnchor(new DateTimeOffset(2026, 11, 9, 1, 0, 0, TimeSpan.Zero), 97m)]));

        store.Saves.Should().ContainSingle();
        chart.Drawings.Should().ContainSingle()
            .Which.Anchors.Should().ContainSingle().Which.Price.Should().Be(97m);

        await chart.EditDrawingAsync(new ChartDrawingEventArgs(
            Guid.NewGuid(),
            ChartDrawingKind.HorizontalLevel,
            [new ChartDrawingAnchor(new DateTimeOffset(2026, 11, 9, 1, 0, 0, TimeSpan.Zero), 99m)]));

        store.Saves.Should().ContainSingle("a stale edit routing never rewrites the store");
    }

    /// <summary>A SaveAsync fault keeps the pushed layer visible in memory — the store stays as it was.</summary>
    [Fact]
    public async Task AFailedSave_KeepsThePushedLayerInMemory()
    {
        var store = new FakeDrawingStore { SaveFault = new InvalidOperationException("disk full") };
        var chart = CreateChartWithStore(
            CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")),
            store);
        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        await AvaloniaDispatcherPump.DrainAsync();

        await chart.CommitDrawingAsync(new ChartDrawingEventArgs(
            null,
            ChartDrawingKind.TrendLine,
            [new ChartDrawingAnchor(new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero), 100m),
             new ChartDrawingAnchor(new DateTimeOffset(2026, 11, 9, 2, 0, 0, TimeSpan.Zero), 120m)]));

        chart.Drawings.Should().ContainSingle("the session layer stands even when the store refused");
        store.Saves.Should().ContainSingle("the write was routed before the fault");
    }

    /// <summary>A removal drops only the named drawing; an unknown name saves nothing.</summary>
    [Fact]
    public async Task DeleteDrawing_RemovesTheNamedDrawing_AndUnknownNamesSaveNothing()
    {
        var level = new ChartDrawing
        {
            Kind = ChartDrawingKind.HorizontalLevel,
            Anchors = [new ChartDrawingAnchor(new DateTimeOffset(2026, 11, 9, 1, 0, 0, TimeSpan.Zero), 95m)],
        };
        var trend = new ChartDrawing
        {
            Kind = ChartDrawingKind.TrendLine,
            Anchors =
            [
                new ChartDrawingAnchor(new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero), 100m),
                new ChartDrawingAnchor(new DateTimeOffset(2026, 11, 9, 2, 0, 0, TimeSpan.Zero), 120m),
            ],
        };
        var store = new FakeDrawingStore([[level, trend]]);
        var chart = CreateChartWithStore(
            CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")),
            store);
        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        await AvaloniaDispatcherPump.DrainAsync();

        chart.SelectDrawing(level.Id);
        await chart.DeleteDrawingAsync(level.Id);

        chart.Drawings.Should().ContainSingle().Which.Id.Should().Be(trend.Id);
        chart.SelectedDrawingId.Should().BeNull("the named removal clears its own selection");
        store.Saves.Should().ContainSingle()
            .Which.Layer.Should().ContainSingle().Which.Id.Should().Be(trend.Id);

        await chart.DeleteDrawingAsync(Guid.NewGuid());

        store.Saves.Should().ContainSingle("an unknown name never rewrites the store");
    }

    /// <summary>The CreateChart backend for the drawings facts: the scripted store stands in for the singleton.</summary>
    private static ChartViewModel CreateChartWithStore(ActiveExchangeService service, FakeDrawingStore store) =>
        CreateChart(service, drawingStore: store);

    #endregion Drawing Actions (T-091)

    #region Indicator Pane Heights (T-199)

    /// <summary>
    /// The pane-heights seam's scripted settings pair: the stored map rides
    /// the sibling key (the seed read answers it, every other key answers
    /// empty) and the changes stream is a live subject the facts emit on.
    /// </summary>
    private (ChartViewModel Chart, Subject<SettingsChange> Changes, ISettingsService Settings)
        CreateChartWithPaneSettings(string storedPaneHeights)
    {
        var settings = Substitute.For<ISettingsService>();
        var changes = new Subject<SettingsChange>();
        settings.Changes.Returns(changes);
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(call => (string)call[1] == UiSettingsKeys.ChartIndicatorPaneHeights
                ? storedPaneHeights
                : string.Empty);
        settings.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(call => Task.CompletedTask);

        var chart = CreateChart(
            CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")),
            settings);

        return (chart, changes, settings);
    }

    /// <summary>A settled splitter drag writes the compact map — clamped into the layout's usable band (the stored truth is never an out-of-band height).</summary>
    [Fact]
    public void SettleIndicatorPaneHeight_WritesTheClampedHeight()
    {
        var (chart, _, settings) = CreateChartWithPaneSettings("{}");

        chart.SettleIndicatorPaneHeight("RSI", 9999);

        settings.Received().SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartIndicatorPaneHeights,
            "{\"RSI\":480}");
    }

    /// <summary>The settle below the floor clamps up too.</summary>
    [Fact]
    public void SettleIndicatorPaneHeight_ABelowFloorHeight_ClampsUp()
    {
        var (chart, _, settings) = CreateChartWithPaneSettings("{}");

        chart.SettleIndicatorPaneHeight("RSI", 5);

        settings.Received().SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartIndicatorPaneHeights,
            "{\"RSI\":72}");
    }

    /// <summary>The pipeline's own write echoes back through the changes stream: an equal JSON re-applies nothing (the no-echo guard).</summary>
    [Fact]
    public void SettleIndicatorPaneHeight_TheWriteEcho_ReappliesNothing()
    {
        var (chart, changes, _) = CreateChartWithPaneSettings("{}");
        var publishes = 0;
        using var sub = chart.IndicatorLinesUpdates.Subscribe(_ => publishes++);

        chart.SettleIndicatorPaneHeight("RSI", 200);
        publishes.Should().Be(1, "the settle republishes the lines once");

        changes.OnNext(new SettingsChange(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartIndicatorPaneHeights,
            null,
            "{\"RSI\":200}"));

        publishes.Should().Be(1, "the pipeline's own echo re-applies nothing");
    }

    /// <summary>An external write re-applies live (the settings window or another instance) — and the applied map clamps on restore, so the cache never carries an out-of-band height.</summary>
    [Fact]
    public void ApplyPaneHeights_AnExternalWrite_ClampsOnRestore()
    {
        var (chart, changes, settings) = CreateChartWithPaneSettings("{}");
        using var sub = chart.IndicatorLinesUpdates.Subscribe(_ => { });

        changes.OnNext(new SettingsChange(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartIndicatorPaneHeights,
            null,
            "{\"RSI\":9999,\"MACD\":5}"));

        // The next settle writes the CLAMPED cache (480/72), not the raw
        // stored extremes — the clamp-on-restore discipline rides the cache.
        chart.SettleIndicatorPaneHeight("RSI", 220);

        settings.Received().SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartIndicatorPaneHeights,
            "{\"MACD\":72,\"RSI\":220}");
    }

    /// <summary>A seeded stored map clamps at the seed read too: the first settle after a restart writes the clamped truth beside its new entry.</summary>
    [Fact]
    public void SettleIndicatorPaneHeight_ASeededOutOfRangeMap_ClampsAtTheSeed()
    {
        var (chart, _, settings) = CreateChartWithPaneSettings("{\"RSI\":9999,\"MACD\":5}");

        chart.SettleIndicatorPaneHeight("RSI", 220);

        settings.Received().SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartIndicatorPaneHeights,
            "{\"MACD\":72,\"RSI\":220}");
    }

    #endregion

    #region Startup Prewarm (T-226)

    /// <summary>
    /// The prewarm (T-226): the persisted workspace preference reads BEFORE
    /// the symbols fetch and applies through the UI marshal while the fetch
    /// is still in flight — the chart renders from the candle cache without
    /// waiting for the instrument list. A restore is a read, not an edit —
    /// the prewarm never rewrites the preference (the persistence chain
    /// skips the bootstrap gate's assignment), and the later delivery's
    /// re-apply of the same preference costs nothing: the record Symbol's
    /// value equality makes the equal raise reload nothing.
    /// </summary>
    [Fact]
    public async Task BootstrapSymbols_ThePersistedSymbol_PrewarmsTheChartWhileTheFetchIsInFlight()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("SOL/USDC");
        settings.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);

        var marketData = new FakeMarketData();

        // The gate is in place BEFORE the construction: the constructor's
        // own bootstrap parks inside its fetch, so the whole fetch window
        // stays open for the fact.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        marketData.SymbolsGate = gate;

        var second = new FakeMarketData(id: "fake2", name: "Fake2");
        var chart = CreateChart(CreateService(marketData, second), settings);
        var delivered = new List<IReadOnlyList<Bar>>();
        using var subscription = chart.BarsUpdates.Subscribe(delivered.Add);

        // The fetch kicked synchronously at construction (the prewarm never
        // delayed it) and the apply waits for the marshal.
        marketData.SymbolsRequested.Should().ContainSingle("the fetch kick keeps its synchronous construction shape");
        chart.SelectedSymbol.Should().BeNull("the prewarm's apply rides the UI marshal — inert until pumped");

        await AvaloniaDispatcherPump.DrainAsync();

        chart.SelectedSymbol.Should().Be(
            Symbol.Parse("SOL/USDC"),
            "the persisted preference rendered the chart while the fetch is still parked");
        marketData.BarsRequested.Should().ContainSingle().Which.Should().Be(
            Symbol.Parse("SOL/USDC"),
            "the prewarmed selection loaded from the cache-first chain");
        marketData.BarsTimeFrames.Should().ContainSingle().Which.Should().Be(TimeFrame.H1);
        delivered.Should().ContainSingle("the prewarmed load served the standing one-bar page");
        delivered[0].Should().ContainSingle().Which.Close.Should().Be(300m);

        // A restore is a read, not an edit: the prewarm's apply rides the
        // bootstrap gate, so the persistence chain never writes (the
        // assertion call's auto-value Task is discarded).
        _ = settings.DidNotReceive().SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());

        // The delivery's re-apply, landed synchronously through the facts
        // seam (the parked fetch has completed and cached its list, so the
        // seam's provider call answers from the fast path): the persisted
        // symbol stays the preselection, and re-applying it to the
        // prewarmed chart reloads nothing.
        gate.SetResult();
        await chart.StartBootstrapForFactsAsync(marketData);

        marketData.BarsRequested.Should().ContainSingle().Which.Should().Be(
            Symbol.Parse("SOL/USDC"),
            "the delivered re-apply of the prewarmed selection reloads nothing");
        chart.AvailableSymbols.Select(row => row.Symbol.Normalized).Should()
            .Equal(new[] { "BTC/USDT", "ETH/BTC", "SOL/USDC" }, "the delivered list landed");
    }

    /// <summary>
    /// The prewarm's apply carries its bootstrap's epoch (BUG-016's guard):
    /// newer bootstraps — the exchange switch's own start, then the facts
    /// seam's newest start — claim newer epochs, and every still-unpumped
    /// prewarm apply DROPS at its marshal hop. The superseded exchange
    /// renders nothing (no wasted cache fetch pair) while the newest
    /// exchange's list and preselection stand.
    /// </summary>
    [Fact]
    public async Task BootstrapSymbols_ThePrewarm_DropsWhenANewerBootstrapClaimsTheEpoch()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("ETH/BTC");
        settings.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);

        var standard = new FakeMarketData();
        var exotic = new FakeMarketData(
            id: "fake2",
            name: "Fake2",
            symbolNames: ["DOGE/USDT", "LTC/USDT"]);
        var service = CreateService(standard, exotic);
        var chart = CreateChart(service, settings);

        // The construction's bootstrap claims epoch 1, kicks the standard
        // fetch and posts its delivery plus the prewarm apply. The switch
        // then claims epoch 2 (its own delivery posts behind them), and the
        // facts seam claims epoch 3 — everything queued before the newest
        // start stays unpumped until the drain.
        await service.SwitchToAsync("fake2");
        await chart.StartBootstrapForFactsAsync(exotic);
        await AvaloniaDispatcherPump.DrainAsync();

        chart.SelectedSymbol.Should().Be(
            Symbol.Parse("DOGE/USDT"),
            "the newest bootstrap's preselection stands");
        chart.AvailableSymbols.Select(row => row.Symbol.Normalized).Should()
            .Equal(new[] { "DOGE/USDT", "LTC/USDT" });
        standard.BarsRequested.Should().BeEmpty(
            "the stale prewarm dropped at its epoch guard — the superseded exchange renders nothing");
        exotic.BarsRequested.Should().ContainSingle().Which.Should().Be(
            Symbol.Parse("DOGE/USDT"),
            "the delivered preselection loaded on the new exchange");
    }

    /// <summary>
    /// The honest nothing-to-prewarm: an absent (or unparsable) preference
    /// reads to nothing, the prewarm posts no apply, and the chart stays on
    /// its constructor selection until the delivery decides — the whole
    /// start keeps the pre-existing shape exactly (one fetch, one delivery,
    /// one bars load), so a prewarm that applied anything of its own would
    /// show up as an extra load here.
    /// </summary>
    [Fact]
    public async Task BootstrapSymbols_WithNoPersistedSymbol_ThePrewarmRendersNothing()
    {
        var marketData = new FakeMarketData();

        var chart = CreateChart(
            CreateService(marketData, new FakeMarketData(id: "fake2", name: "Fake2")));

        // The ungated fetch completed inline at construction: the delivery
        // is already posted, one drain lands it.
        await AvaloniaDispatcherPump.DrainAsync();

        chart.SelectedSymbol.Should().Be(
            Symbol.Parse("BTC/USDT"),
            "the delivery's first-entry preselection stands — the prewarm contributed nothing");
        marketData.BarsRequested.Should().ContainSingle().Which.Should().Be(
            Symbol.Parse("BTC/USDT"),
            "exactly one bars load — the delivery's own");
        marketData.SymbolsRequested.Should().ContainSingle("the fetch kick keeps its synchronous construction shape");
        chart.AvailableSymbols.Select(row => row.Symbol.Normalized).Should()
            .Equal(new[] { "BTC/USDT", "ETH/BTC", "SOL/USDC" });
    }

    #endregion

    #endregion
}

