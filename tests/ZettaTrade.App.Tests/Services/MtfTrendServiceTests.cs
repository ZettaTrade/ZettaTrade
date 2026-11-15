namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// The screener trend computer's pins (T-239): a pass over an empty cache
/// answers the honest all-warming verdict, a cached series classifies
/// through the pure classifier, the pass's cache reads share the two-slot
/// read budget, a warming pair's depth ask rides the T-086 deep-history
/// fetcher with the pass's adapter snapshot and a window wide enough for
/// the day-scale frames' weekend gaps, a pair fetched dry waits out its
/// ten-minute backoff, and the page pipeline throttles a same-shape page
/// while a symbol-set change bypasses the cooldown. The facts drive the
/// public <see cref="MtfTrendService.ComputeAsync"/> seam and the
/// internal pass pipeline directly — the feed's own cadence stays the
/// launch smoke's territory.
/// </summary>
public sealed class MtfTrendServiceTests
{
    #region Scene kit

    /// <summary>The trend computer's whole scene: the facade, the feed, the cache, the fetcher, the computer.</summary>
    private sealed class TrendScene
    {
        public TrendScene(ICandleCache? cache = null, IMarketDataAdapter? adapter = null, bool initTuning = false)
        {
            var settings = Substitute.For<ISettingsService>();
            settings.Changes.Returns(Observable.Empty<SettingsChange>());
            settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>())
                .Returns(string.Empty);
            if (initTuning)
            {
                settings.GetAsync<int>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>()).Returns(100);
                settings.GetAsync<decimal>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>()).Returns(90m);
            }

            Exchange = new ActiveExchangeService(
                [adapter ?? new RecordingMarketData()],
                settings,
                NullLogger<ActiveExchangeService>.Instance);
            Cache = cache ?? new StubCandleCache();
            Feed = new ScreenerFeedService(
                Exchange,
                new SymbolListProvider(Exchange, NullLogger<SymbolListProvider>.Instance),
                NullLogger<ScreenerFeedService>.Instance,
                Observable.Never<long>());
            Trends = new(
                Feed,
                Cache,
                Exchange,
                new CandleBackfillService(Cache, NullLogger<CandleBackfillService>.Instance),
                settings,
                NullLogger<MtfTrendService>.Instance);
        }

        public ActiveExchangeService Exchange { get; }

        public ScreenerFeedService Feed { get; }

        public ICandleCache Cache { get; }

        public MtfTrendService Trends { get; }
    }

    /// <summary>The honest cold-cache stub: an empty answer unless a fact staged a series, plus the read-flight gauge.</summary>
    internal sealed class StubCandleCache : ICandleCache
    {
        private int _inFlight;

        /// <summary>The highest simultaneous <see cref="GetLatestAsync"/> flight the stub observed.</summary>
        public int MaxInFlight { get; private set; }

        /// <summary>The per-read delay the budget fact runs (zero elsewhere).</summary>
        public TimeSpan SeriesDelay { get; set; }

        /// <summary>The staged series keyed (adapter id, normalized symbol, frame); a miss answers empty.</summary>
        public Dictionary<(string AdapterId, string SymbolText, TimeFrame Frame), IReadOnlyList<Bar>> Series { get; } = [];

        public async Task<IReadOnlyList<Bar>> GetLatestAsync(
            string adapterId,
            Symbol symbol,
            TimeFrame timeFrame,
            int limit,
            CancellationToken cancellationToken = default)
        {
            var observed = Interlocked.Increment(ref _inFlight);
            MaxInFlight = Math.Max(MaxInFlight, observed);
            try
            {
                if (SeriesDelay > TimeSpan.Zero)
                {
                    await Task.Delay(SeriesDelay, cancellationToken);
                }

                return Series.TryGetValue((adapterId, symbol.Normalized, timeFrame), out var bars)
                    ? bars.Count <= limit ? bars : bars.Take(limit).ToList()
                    : [];
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public async Task<IReadOnlyList<Bar>> GetOlderThanAsync(
            string adapterId,
            Symbol symbol,
            TimeFrame timeFrame,
            DateTimeOffset before,
            int limit,
            CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            return [];
        }

        public Task WriteThroughAsync(
            string adapterId,
            Symbol symbol,
            TimeFrame timeFrame,
            IReadOnlyList<Bar> bars,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// The recording market-data void: every stream is bottomless and every
    /// candle fetch is recorded — the deep-history fetcher's requests are
    /// what the drain facts assert on.
    /// </summary>
    internal sealed class RecordingMarketData : IMarketDataAdapter
    {
        public string Id => "demo";

        public string Name => "demo";

        public bool IsConnected => true;

        public ExchangeCapabilities Capabilities => ExchangeCapabilities.MarketData;

        /// <summary>The recorded candle fetches (frame, symbol, band) in arrival order.</summary>
        public List<(TimeFrame Frame, Symbol Symbol, DateTimeOffset From, DateTimeOffset To)> Fetches { get; } = [];

        public async Task<IReadOnlyList<Bar>> GetBarsAsync(
            Symbol symbol,
            TimeFrame timeFrame,
            DateTimeOffset from,
            DateTimeOffset? to = null,
            int limit = 500,
            CancellationToken ct = default)
        {
            await Task.CompletedTask;
            Fetches.Add((timeFrame, symbol, from, to ?? DateTimeOffset.MaxValue));
            return [];
        }

        public async Task<IReadOnlyList<Symbol>> GetSymbolsAsync(CancellationToken ct = default)
        {
            await Task.CompletedTask;
            return [];
        }

        public IObservable<Ticker> SubscribeTicker(Symbol symbol) => Observable.Never<Ticker>();

        public IObservable<OrderBook> SubscribeOrderBook(Symbol symbol, int depth = 20) => Observable.Never<OrderBook>();

        public IObservable<MarketTrade> SubscribeTrades(Symbol symbol) => Observable.Never<MarketTrade>();

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A rising series in the classifier's golden shape (the shipped tuning's minimum bars).</summary>
    private static IReadOnlyList<Bar> RisingSeries(int count)
    {
        var bars = new List<Bar>(count);
        var close = 100m;
        for (var index = 0; index < count; index++)
        {
            bars.Add(new Bar(
                DateTimeOffset.UnixEpoch.AddDays(index),
                close,
                close + 1m,
                close - 1m,
                close,
                1000m));
            close += 1m;
        }

        return bars;
    }

    /// <summary>The all-warming verdict table one symbol wide.</summary>
    private static MtfTrendSnapshot WarmingSnapshot(string symbolText) => new(
        DateTimeOffset.UtcNow,
        new Dictionary<string, MtfSymbolTrend>(StringComparer.Ordinal)
        {
            [symbolText] = MtfSymbolTrend.Warming(symbolText),
        });

    private static ScreenerRow Row(string symbolText) => new(symbolText, 100m, 10m, 1m);

    #endregion Scene kit

    /// <summary>A pass over an empty cache answers the honest all-warming verdict — never a fabricated state.</summary>
    [Fact]
    public async Task AComputedPass_AnswersWarming_OverAnEmptyCache()
    {
        var scene = new TrendScene();

        var table = await scene.Trends.ComputeAsync(["BTC/USDT"]);

        table.BySymbol.Should().ContainKey("BTC/USDT");
        var verdict = table.BySymbol["BTC/USDT"];
        verdict.M15.Should().Be(MtfTrendState.Warming);
        verdict.H1.Should().Be(MtfTrendState.Warming);
        verdict.H4.Should().Be(MtfTrendState.Warming);
        verdict.D1.Should().Be(MtfTrendState.Warming);
        table.ComputedAt.Should().BeAfter(default, "the table stamps its own compute instant");
    }

    /// <summary>A pass over a staged cache classifies through the pure classifier, frame by frame.</summary>
    [Fact]
    public async Task AComputedPass_ClassifiesTheCachedSeries()
    {
        var cache = new StubCandleCache();
        cache.Series[("demo", "BTC/USDT", TimeFrame.H1)] = RisingSeries(73);
        cache.Series[("demo", "BTC/USDT", TimeFrame.H4)] = RisingSeries(73).Select(static bar => bar with
        {
            Close = 200m - (bar.Close - 100m),
            Open = 200m - (bar.Open - 100m),
            High = 201m - (bar.High - 100m),
            Low = 199m - (bar.Low - 100m),
        }).ToList();
        var scene = new TrendScene(cache);

        var verdict = (await scene.Trends.ComputeAsync(["BTC/USDT"])).BySymbol["BTC/USDT"];

        verdict.H1.Should().Be(MtfTrendState.Bull, "the cached one-hour rise classifies bull");
        verdict.H4.Should().Be(MtfTrendState.Bear, "the mirrored cached fall classifies bear");
        verdict.M15.Should().Be(MtfTrendState.Warming, "an unstaged frame stays the honest warming state");
        verdict.D1.Should().Be(MtfTrendState.Warming);
    }

    /// <summary>The pass's cache reads share the two-slot read budget — never more in flight.</summary>
    [Fact]
    public async Task TheReadBudget_CapsTheConcurrentReads()
    {
        var cache = new StubCandleCache { SeriesDelay = TimeSpan.FromMilliseconds(50) };
        var scene = new TrendScene(cache);

        await scene.Trends.ComputeAsync(["A/USDT", "B/USDT", "C/USDT", "D/USDT", "E/USDT", "F/USDT"]);

        cache.MaxInFlight.Should().BeGreaterThan(0, "the pass reads the cache at all");
        cache.MaxInFlight.Should().BeLessThanOrEqualTo(MtfTrendService.ReadBudget,
            "the bounded read budget caps the pass's concurrency (the standing discipline)");
    }

    /// <summary>
    /// A warming pair's depth ask rides the deep-history fetcher once per
    /// frame, in the frames' column order, with the pass's adapter snapshot
    /// and a window wide enough for the day-scale frames' weekend gaps.
    /// </summary>
    [Fact]
    public async Task ADrainedWarmingPair_RidesTheDeepHistoryFetcher()
    {
        var adapter = new RecordingMarketData();
        var scene = new TrendScene(adapter: adapter);

        await scene.Trends.DrainWarmingPairsAsync(WarmingSnapshot("BTC/USDT"), CancellationToken.None);

        adapter.Fetches.Should().HaveCount(MtfTrendService.Frames.Count,
            "one warming symbol asks for depth on every trend frame");
        adapter.Fetches.Select(static fetch => fetch.Frame)
            .Should().Equal(MtfTrendService.Frames, "the drain walks the frames in their column order");
        foreach (var fetch in adapter.Fetches)
        {
            fetch.Symbol.Normalized.Should().Be("BTC/USDT");
            fetch.To.Should().BeOnOrBefore(DateTimeOffset.UtcNow.AddSeconds(1));
            fetch.To.Should().BeOnOrAfter(DateTimeOffset.UtcNow.AddSeconds(-30));
            var expected = TimeSpan.FromTicks(
                TrendClassifier.MinimumBars(20, 14)
                * MtfTrendService.BackfillWindowMultiple
                * TimeFrameCalendar.NominalBarLength(fetch.Frame).Ticks);
            (fetch.To - fetch.From).Should().BeCloseTo(
                expected,
                TimeSpan.FromSeconds(1),
                "the fetch window is the classifier's minimum doubled over the frame's nominal bar length");
        }
    }

    /// <summary>A pair fetched dry waits out its ten-minute backoff — the second pass re-asks nothing.</summary>
    [Fact]
    public async Task ADryPair_WaitsOutItsBackoff()
    {
        var adapter = new RecordingMarketData();
        var scene = new TrendScene(adapter: adapter);
        await scene.Trends.DrainWarmingPairsAsync(WarmingSnapshot("BTC/USDT"), CancellationToken.None);

        await scene.Trends.DrainWarmingPairsAsync(WarmingSnapshot("BTC/USDT"), CancellationToken.None);

        adapter.Fetches.Should().HaveCount(MtfTrendService.Frames.Count,
            "the fetch passes covered no depth, so every pair is dry and the second pass re-asks nothing");
    }

    /// <summary>
    /// The page pipeline throttles a same-shape page inside the cooldown
    /// and bypasses it when the symbol set changes — one publish per pass.
    /// </summary>
    [Fact]
    public async Task ThePassPipeline_ThrottlesTheSameShape_AndBypassesOnASetChange()
    {
        var scene = new TrendScene();
        var published = 0;
        using var subscription = scene.Trends.Snapshots.Subscribe(_ => published++);
        var pageA = new ScreenerSnapshot([Row("AAA/USDT"), Row("BBB/USDT")], ScreenerFeedState.Live);

        await scene.Trends.ConsiderAsync(pageA, CancellationToken.None);
        published.Should().Be(1, "the first page computes and publishes one pass");

        await scene.Trends.ConsiderAsync(pageA, CancellationToken.None);
        published.Should().Be(1, "the same-shape page inside the cooldown throttles");

        await scene.Trends.ConsiderAsync(
            new ScreenerSnapshot([Row("AAA/USDT"), Row("CCC/USDT")], ScreenerFeedState.Live),
            CancellationToken.None);
        published.Should().Be(2, "the symbol-set change bypasses the cooldown and publishes the fresh pass");
    }

    /// <summary>An unparsable row text stays the honest warming verdict without a fabricated state or a throw.</summary>
    [Fact]
    public async Task AnUnparsableText_KeepsTheHonestWarming()
    {
        var scene = new TrendScene();

        var verdict = (await scene.Trends.ComputeAsync(["!!!"])).BySymbol["!!!"];

        verdict.M15.Should().Be(MtfTrendState.Warming);
        verdict.H1.Should().Be(MtfTrendState.Warming);
        verdict.H4.Should().Be(MtfTrendState.Warming);
        verdict.D1.Should().Be(MtfTrendState.Warming);
    }

    /// <summary>Duplicate texts collapse into one verdict — the pass answers a table, not a list echo.</summary>
    [Fact]
    public async Task ADuplicateText_CollapsesIntoOneVerdict()
    {
        var scene = new TrendScene();

        var table = await scene.Trends.ComputeAsync(["BTC/USDT", "BTC/USDT"]);

        table.BySymbol.Should().HaveCount(1);
    }

    /// <summary>The initialization loads the tuning from the settings store — the pass runs on the loaded periods.</summary>
    [Fact]
    public async Task InitializeAsync_LoadsTheTuningFromTheStore()
    {
        var cache = new StubCandleCache();
        cache.Series[("demo", "BTC/USDT", TimeFrame.H1)] = RisingSeries(73);
        var scene = new TrendScene(cache, initTuning: true);

        await scene.Trends.InitializeAsync();

        var verdict = (await scene.Trends.ComputeAsync(["BTC/USDT"])).BySymbol["BTC/USDT"];
        verdict.H1.Should().Be(MtfTrendState.Warming,
            "the loaded tuning (100/100) asks for 325 bars; the staged 73-bar series cannot judge — " +
            "the shipped default's 73-bar minimum would have classified it bull, so the fact pins the load");
    }
}
