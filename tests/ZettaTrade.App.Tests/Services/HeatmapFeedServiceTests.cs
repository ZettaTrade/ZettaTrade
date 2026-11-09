namespace ZettaTrade.App.Tests.Services;

using System;
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
using NSubstitute;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Exchange;
using ZettaTrade.UI.Charting;

/// <summary>
/// The heatmap feed's behavioral pins (T-121): the candidate window caps
/// at the budget in the adapter's own order, a ticker tick surfaces on the
/// next pulse under the metric's weight, ties resolve by the ordinal
/// symbol text, unpriced pairs own no row, a faulting universe fetch ships
/// the honest empty page, a refresh swap drops the previous exchange's
/// state in full, a superseded (stale-epoch) composition publishes
/// nothing, the metric flip re-plans on the panel's own cadence. Every
/// wait is delivery-ordered via an already-subscribed
/// <see cref="TaskCompletionSource{TResult}"/> promise - no wall-clock
/// budgets, no cross-observer races (the T-135 lesson).
/// </summary>
public sealed class HeatmapFeedServiceTests
{
    /// <summary>
    /// The outcome-conditioned poll's loud abort bound (yielded iterations):
    /// a settle that misses the bound fails loudly — a composition that
    /// never lands never hangs the fact.
    /// </summary>
    private const int SpinAbortBudget = 200_000;

    #region The fake adapter

    /// <summary>The capture logger: every entry the logged service emits is kept for the fail diagnosis.</summary>
    private sealed class ProbeLogger<T> : ILogger<T> where T : notnull
    {
        public readonly List<string> Entries = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add($"[{logLevel}] {formatter(state, exception)}");
    }

    /// <summary>
    /// Market data fake with a scripted symbol plan (compositions pop per
    /// fetch, the last stands), an optional gate parking the FIRST fetch,
    /// an optional one-shot fault, per-symbol ticker subjects the test
    /// drives directly, and a count of the ticker subscriptions the feed
    /// made (the budget window is observable through it).
    /// </summary>
    private sealed class FakeMarketData : IMarketDataAdapter
    {
        private readonly Queue<IReadOnlyList<Symbol>> _symbolPlans;
        private readonly Dictionary<string, ProbeObservable> _tickers = new(StringComparer.Ordinal);
        private int _fetches;

        /// <summary>
        /// When set, completed when the composition's FIRST inner ticker
        /// subscription is fully formed - the settle pin the test waits on
        /// before publishing (the method call happens first; a publish
        /// between the two is lost).
        /// </summary>
        private readonly TaskCompletionSource? _composedGate;

        public FakeMarketData(
            string id,
            IReadOnlyList<IReadOnlyList<string>>? symbolPlan,
            TaskCompletionSource? parkFirstFetch = null,
            bool throwOnFirstFetch = false,
            TaskCompletionSource? composedOnFirstSubscribe = null)
        {
            Id = id;
            Name = id;
            _symbolPlans = new Queue<IReadOnlyList<Symbol>>(
                (symbolPlan ?? []).Select(composition => composition.Select(Symbol.Parse).ToArray()));
            ParkFirstFetch = parkFirstFetch;
            ThrowOnFirstFetch = throwOnFirstFetch;
            _composedGate = composedOnFirstSubscribe;
        }

        public string Id { get; }

        public string Name { get; }

        public bool IsConnected => true;

        public ExchangeCapabilities Capabilities => ExchangeCapabilities.MarketData;

        public int SymbolFetches => _fetches;

        public int TickerSubscriptions { get; private set; }

        public TaskCompletionSource? ParkFirstFetch { get; }

        private bool ThrowOnFirstFetch { get; }

        public async Task<IReadOnlyList<Symbol>> GetSymbolsAsync(CancellationToken ct = default)
        {
            _fetches++;
            if (_fetches == 1 && ThrowOnFirstFetch)
            {
                throw new HttpRequestException("offline");
            }

            if (_fetches == 1 && ParkFirstFetch is { } gate)
            {
                await gate.Task;
            }

            // The plan pops one composition per fetch; the last one stands.
            return _symbolPlans.Count > 1
                ? _symbolPlans.Dequeue()
                : _symbolPlans.Peek();
        }

        public Task<IReadOnlyList<Bar>> GetBarsAsync(
            Symbol symbol,
            TimeFrame timeFrame,
            DateTimeOffset from,
            DateTimeOffset? to = null,
            int limit = 500,
            CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Bar>>([]);

        public IObservable<Ticker> SubscribeTicker(Symbol symbol)
        {
            TickerSubscriptions++;
            var key = symbol.Normalized;
            if (!_tickers.TryGetValue(key, out var wrapped))
            {
                wrapped = new ProbeObservable(() => _composedGate?.TrySetResult());
                _tickers[key] = wrapped;
            }

            return wrapped;
        }

        public IObservable<OrderBook> SubscribeOrderBook(Symbol symbol, int depth = 20) => Observable.Never<OrderBook>();

        public IObservable<MarketTrade> SubscribeTrades(Symbol symbol) => Observable.Never<MarketTrade>();

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        /// <summary>Publish one full ticker update onto the symbol's stream.</summary>
        public void Publish(
            string normalizedSymbol,
            decimal lastPrice,
            decimal volume24h,
            decimal change24hPercent)
        {
            if (_tickers.TryGetValue(normalizedSymbol, out var stream))
            {
                stream.PublishTick(new Ticker(
                    Symbol.Parse(normalizedSymbol),
                    lastPrice,
                    0m,
                    0m,
                    volume24h,
                    change24hPercent,
                    DateTimeOffset.UtcNow));
            }
        }

        /// <summary>
        /// The probe subject wrapper: once the inner subscription is fully
        /// formed, fires its first-connection hook - the settle signal that
        /// makes a publish-after-settle land in a live handler.
        /// </summary>
        private sealed class ProbeObservable(Action? onFirstConnection) : IObservable<Ticker>
        {
            private readonly Subject<Ticker> _inner = new();
            private int _connectedOnce;

            public IDisposable Subscribe(IObserver<Ticker> observer)
            {
                var innerSubscription = _inner.Subscribe(observer);
                if (Interlocked.Exchange(ref _connectedOnce, 1) == 0)
                {
                    onFirstConnection?.Invoke();
                }

                return Disposable.Create(innerSubscription.Dispose);
            }

            public void PublishTick(Ticker tick) => _inner.OnNext(tick);
        }
    }

    #endregion The fake adapter

    #region The kit

    private static ActiveExchangeService BuildService(params IMarketDataAdapter[] adapters)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Changes.Returns(Observable.Empty<SettingsChange>());
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
        return new ActiveExchangeService(
            adapters,
            settings,
            NullLogger<ActiveExchangeService>.Instance);
    }

    private static ActiveExchangeService BuildService(
        ProbeLogger<ActiveExchangeService> logger,
        params IMarketDataAdapter[] adapters)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Changes.Returns(Observable.Empty<SettingsChange>());
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
        return new ActiveExchangeService(
            adapters,
            settings,
            logger);
    }

    private static HeatmapFeedService BuildFeed(
        ActiveExchangeService service,
        Subject<long>? pulse = null) => new(
        service,
        NullLogger<HeatmapFeedService>.Instance,
        pulse ?? new Subject<long>());

    /// <summary>
    /// Subscribe a next-publication promise BEFORE the action that drives
    /// it (the feed's pulse sampling delivers to already-subscribed
    /// observers only; a promise subscribed after a publish would starve).
    /// </summary>
    private static (Task<IReadOnlyList<HeatmapTile>> Promise, IDisposable Subscription) NextSnapshotPromise(
        HeatmapFeedService feed)
    {
        var promise = new TaskCompletionSource<IReadOnlyList<HeatmapTile>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = feed.Snapshots.Subscribe(
            snapshot => promise.TrySetResult(snapshot),
            ex => promise.TrySetException(ex));
        return (promise.Task, subscription);
    }

    #endregion The kit

    #region Tests

    /// <summary>The warm-up caps the candidate window at the budget.</summary>
    [Fact]
    public async Task TheWarmUp_CapsTheWindowAtTheBudget()
    {
        var many = Enumerable.Range(0, 60)
            .Select(index => $"P{index:00}/USDT")
            .ToArray();
        var adapter = new FakeMarketData("binance", [many]);
        var service = BuildService(adapter);
        var feed = BuildFeed(service);

        await feed.InitializeAsync();

        adapter.TickerSubscriptions.Should().Be(HeatmapFeedService.CandidateBudget,
            "a big venue's catalog is capped to the bounded candidate window");
    }

    /// <summary>A ticker tick surfaces on the next pulse under the metric's weight.</summary>
    [Fact]
    public async Task ATickerTick_SurfacesOnTheNextPulse_AtTheMetricWeight()
    {
        var adapter = new FakeMarketData("binance", [["BTC/USDT"]]);
        var service = BuildService(adapter);
        var pulse = new Subject<long>();
        var feed = BuildFeed(service, pulse);
        await feed.InitializeAsync();

        adapter.Publish("BTC/USDT", 100m, 10m, 2m);
        var (promise, subscription) = NextSnapshotPromise(feed);
        pulse.OnNext(1);

        var snapshot = await promise;
        subscription.Dispose();

        snapshot.Should().ContainSingle("the ticked candidate is the whole page");
        snapshot[0].SymbolText.Should().Be("BTC/USDT");
        snapshot[0].Change24hPercent.Should().Be(2m);
        snapshot[0].Weight.Should().Be(1000m,
            "the quote-volume metric sizes by the last price times the 24h volume");
    }

    /// <summary>Tied weights resolve by the ordinal symbol text, so a re-plan is deterministic.</summary>
    [Fact]
    public async Task TiedWeights_ResolveByTheOrdinalSymbolText()
    {
        var adapter = new FakeMarketData("binance", [["BTC/USDT", "ETH/USDT"]]);
        var service = BuildService(adapter);
        var pulse = new Subject<long>();
        var feed = BuildFeed(service, pulse);
        await feed.InitializeAsync();

        adapter.Publish("BTC/USDT", 100m, 2m, 0m);
        adapter.Publish("ETH/USDT", 200m, 1m, 0m);

        var (promise, subscription) = NextSnapshotPromise(feed);
        pulse.OnNext(1);

        var snapshot = await promise;
        subscription.Dispose();

        snapshot.Select(static tile => tile.SymbolText)
            .Should().Equal(
                (IEnumerable<string>)["BTC/USDT", "ETH/USDT"],
                "equal weights ride the ordinal text order, never the map's internal order");
    }

    /// <summary>The absolute 24h change metric sizes by the magnitude and re-plans on the flip.</summary>
    [Fact]
    public async Task TheChangeMetric_SizesByMagnitude_AndTheFlipRePlans()
    {
        var adapter = new FakeMarketData("binance", [["BTC/USDT", "ETH/USDT"]]);
        var service = BuildService(adapter);
        var pulse = new Subject<long>();
        var feed = BuildFeed(service, pulse);
        await feed.InitializeAsync();

        adapter.Publish("BTC/USDT", 100m, 10m, 0.5m);
        adapter.Publish("ETH/USDT", 50m, 3m, 3m);

        var (volumePromise, volumeSub) = NextSnapshotPromise(feed);
        pulse.OnNext(1);
        var byVolume = await volumePromise;
        volumeSub.Dispose();

        byVolume.Select(static tile => tile.SymbolText)
            .Should().Equal(
                (IEnumerable<string>)["BTC/USDT", "ETH/USDT"],
                "BTC's 1000 quote units outrank ETH's 150 under the volume metric");

        feed.Metric = HeatmapMetric.Change24hPercent;

        var (changePromise, changeSub) = NextSnapshotPromise(feed);
        pulse.OnNext(2);
        var byChange = await changePromise;
        changeSub.Dispose();

        byChange.Select(static tile => tile.SymbolText)
            .Should().Equal(
                (IEnumerable<string>)["ETH/USDT", "BTC/USDT"],
                "ETH's |3| change magnitude outranks BTC's |0.5| under the change metric");
    }

    /// <summary>Unpriced (non-ticked or zero-weight) pairs own no row; the empty page still ships.</summary>
    [Fact]
    public async Task UnpricedPairs_OwnNoRow_WhileThePageStillShips()
    {
        var adapter = new FakeMarketData("binance", [["BTC/USDT", "ETH/USDT"]]);
        var service = BuildService(adapter);
        var pulse = new Subject<long>();
        var feed = BuildFeed(service, pulse);
        await feed.InitializeAsync();

        adapter.Publish("BTC/USDT", 100m, 0m, 2m); // zero volume: no weight under the metric
        // ETH never ticks at all.

        var (promise, subscription) = NextSnapshotPromise(feed);
        pulse.OnNext(1);

        (await promise).Should().BeEmpty(
            "a pair with no measurable weight owns no area, and the honest empty page replaces a stale surface");
        subscription.Dispose();
    }

    /// <summary>A faulting universe fetch ships the empty page, never a fabricated row.</summary>
    [Fact]
    public async Task AFaultingUniverseFetch_ShipsTheEmptyPage()
    {
        var adapter = new FakeMarketData("binance", [["BTC/USDT"]], throwOnFirstFetch: true);
        var service = BuildService(adapter);
        var pulse = new Subject<long>();
        var feed = BuildFeed(service, pulse);

        await feed.InitializeAsync();

        var (promise, subscription) = NextSnapshotPromise(feed);
        pulse.OnNext(1);

        (await promise).Should().BeEmpty("a broken fetch stays honest: no pairs means no tiles");
        subscription.Dispose();
    }

    /// <summary>A refresh swap drops the previous exchange's ticker state in full.</summary>
    [Fact]
    public async Task ARefreshSwap_DropsThePreviousStateInFull()
    {
        var adapter = new FakeMarketData("binance", [["BTC/USDT"], ["SOL/USDT"]]);
        var service = BuildService(adapter);
        var pulse = new Subject<long>();
        var feed = BuildFeed(service, pulse);
        await feed.InitializeAsync();

        adapter.Publish("BTC/USDT", 10m, 1m, 0m);

        var (beforePromise, beforeSub) = NextSnapshotPromise(feed);
        pulse.OnNext(1);
        var before = await beforePromise;
        beforeSub.Dispose();

        before.Select(static tile => tile.SymbolText)
            .Should().Equal(
                (IEnumerable<string>)["BTC/USDT"],
                "the first composition's candidate serves the page");

        await feed.RefreshOnceAsync(CancellationToken.None);

        adapter.Publish("BTC/USDT", 20m, 2m, 0m); // a stale stream's late tick must not resurrect

        var (afterPromise, afterSub) = NextSnapshotPromise(feed);
        pulse.OnNext(2);
        var after = await afterPromise;
        afterSub.Dispose();

        after.Should().BeEmpty(
            "the swap dropped the previous candidate set in full: the old exchange's late tick resurrects nothing and the fresh candidates are still unpriced");
    }

    /// <summary>A composition superseded by a newer switch publishes nothing - the stale-epoch drop.</summary>
    [Fact]
    public async Task AStaleEpochComposition_PublishesNothing()
    {
        var parkedGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var binance = new FakeMarketData("binance", [["BTC/USDT"]], parkFirstFetch: parkedGate);
        var pumpConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var demo = new FakeMarketData("demo", [["ETH/USDT"]], composedOnFirstSubscribe: pumpConnect);
        var facadeProbe = new ProbeLogger<ActiveExchangeService>();
        var feedProbe = new ProbeLogger<HeatmapFeedService>();
        var service = BuildService(facadeProbe, binance, demo);
        var pulse = new Subject<long>();
        var feed = new HeatmapFeedService(service, feedProbe, pulse);
        await feed.InitializeAsync(); // the warm-up parks inside binance's first fetch.

        await service.SwitchToAsync("demo"); // the epoch bump + the queued re-fetch.

        parkedGate.SetResult(); // the parked composition resumes and must drop.

        // The composition's settle pin: outcome-conditioned poll with a
        // LOUD abort - the wait completes exactly when the swap's
        // composition armed the demo pump; a composition that never
        // lands aborts the poll and fails the fact loudly, never a hang.
        var spins = 0;
        // The settle pin is the REAL pump connection: the fake's connect TCS
        // completes only after the wrapper's inner subscription is fully
        // formed - a publish after the settle lands in a LIVE handler (the
        // method-call counter rose before the connection and let a publish
        // slip inside that gap).
        while (!pumpConnect.Task.IsCompleted)
        {
            await Task.Yield();
            if (++spins > SpinAbortBudget)
            {
                Assert.Fail(
                    $"The demo composition never armed its ticker pump after the switch (spins={spins})." +
                    $" binance: fetches={binance.SymbolFetches} tickers={binance.TickerSubscriptions};" +
                    $" demo: fetches={demo.SymbolFetches} tickers={demo.TickerSubscriptions};" +
                    $" Current='{service.Current.Id}'." +
                    $" Facade log: {string.Join(" | ", facadeProbe.Entries)}" +
                    $" Feed log: {string.Join(" | ", feedProbe.Entries)}");
            }
        }

        demo.Publish("ETH/USDT", 50m, 1m, 0m);

        var (promise, subscription) = NextSnapshotPromise(feed);
        pulse.OnNext(1);

        var snapshot = await promise;
        subscription.Dispose();

        snapshot.Select(static tile => tile.SymbolText)
            .Should().Equal(
                (IEnumerable<string>)["ETH/USDT"],
                "the parked first composition was superseded: only the new exchange's page ships" +
                $" (tiles={snapshot.Count} tickers={demo.TickerSubscriptions}" +
                $" facadeLog={string.Join(" | ", facadeProbe.Entries)}" +
                $" feedLog={string.Join(" | ", feedProbe.Entries)})");
    }

    #endregion Tests
}
