namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
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
/// The venue sampler's pins (T-253): a silent venue takes its table slot
/// as the honest dash row (the acceptance — a missing venue is a dash,
/// never an error), an answering venue carries its wire prices, the
/// deltas ride the ANSWERING set's median, an unparsable text throws
/// (the one-symbol pass has nothing to answer with), the basis reads
/// each venue's OWN cache with the derived perp sibling (a stock owns
/// none), and the pass's cache reads share the two-slot budget.
/// </summary>
public sealed class VenueSpreadServiceTests
{
    #region Scene kit

    /// <summary>The sampler's whole scene: the facade over the stub fleet, the cache, the sampler.</summary>
    private sealed class Scene
    {
        public Scene(ICandleCache? cache = null, params StubAdapter[] adapters)
        {
            var settings = Substitute.For<ZettaTrade.Core.Interfaces.ISettingsService>();
            settings.Changes.Returns(System.Reactive.Linq.Observable.Empty<SettingsChange>());

            Exchange = new ActiveExchangeService(
                adapters,
                settings,
                NullLogger<ActiveExchangeService>.Instance);
            Cache = cache ?? new StubCandleCache();
            Sampler = new VenueSpreadService(
                Exchange,
                Cache,
                NullLogger<VenueSpreadService>.Instance);
        }

        public ActiveExchangeService Exchange { get; }

        public ICandleCache Cache { get; }

        public VenueSpreadService Sampler { get; }
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

        public async Task WriteThroughAsync(
            string adapterId,
            Symbol symbol,
            TimeFrame timeFrame,
            IReadOnlyList<Bar> bars,
            CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
        }

        /// <summary>Stage one symbol's series.</summary>
        public void Stage(string adapterId, string symbolText, TimeFrame frame, IReadOnlyList<Bar> bars)
        {
            Series[(adapterId, Symbol.Parse(symbolText).Normalized, frame)] = bars;
        }
    }

    /// <summary>
    /// The controllable venue: a staged ticker answers SYNCHRONOUSLY on
    /// subscribe (no real wait), a null one stays silent (the wait
    /// budget runs) — the pass's two honest shapes, no timing races.
    /// </summary>
    internal sealed class StubAdapter : IMarketDataAdapter
    {
        public StubAdapter(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public string Id { get; }

        public string Name { get; }

        public bool IsConnected => true;

        public ExchangeCapabilities Capabilities => ExchangeCapabilities.MarketData;

        /// <summary>The staged ticker (null = the venue never answers).</summary>
        public Ticker? StagedTicker { get; set; }

        public IObservable<Ticker> SubscribeTicker(Symbol symbol) =>
            StagedTicker is { } staged ? Observable.Return(staged) : Observable.Never<Ticker>();

        public IObservable<OrderBook> SubscribeOrderBook(Symbol symbol, int depth = 20) => Observable.Never<OrderBook>();

        public IObservable<MarketTrade> SubscribeTrades(Symbol symbol) => Observable.Never<MarketTrade>();

        public async Task<IReadOnlyList<Bar>> GetBarsAsync(
            Symbol symbol,
            TimeFrame timeFrame,
            DateTimeOffset from,
            DateTimeOffset? to = null,
            int limit = 500,
            CancellationToken ct = default)
        {
            await Task.CompletedTask;
            return [];
        }

        public async Task<IReadOnlyList<Symbol>> GetSymbolsAsync(CancellationToken ct = default)
        {
            await Task.CompletedTask;
            return [];
        }

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static Bar Bar(int minutesFromEpoch, decimal close) => new()
    {
        Time = DateTimeOffset.UnixEpoch.AddMinutes(minutesFromEpoch),
        Open = close,
        High = close,
        Low = close,
        Close = close,
        Volume = 1m,
    };

    private static IReadOnlyList<Bar> Series(params decimal[] closes) =>
        [.. closes.Select((close, i) => Bar(i * 15, close))];

    private const string SilentVenue = "silent";

    private const string LiveVenue = "live";

    #endregion Scene kit

    [Fact]
    public async Task APass_OverAColdFleet_AnswersTheHonestDashRows()
    {
        var old = VenueSpreadService.TickerWait;
        VenueSpreadService.TickerWait = TimeSpan.FromMilliseconds(50);
        try
        {
            var scene = new Scene(
                null,
                new StubAdapter("a", "Alpha"),
                new StubAdapter("b", "Beta"));

            var snapshot = await scene.Sampler.SampleAsync("BTC/USDT", TimeFrame.H1, 100);

            snapshot.SymbolText.Should().Be("BTC/USDT");
            snapshot.Quotes.Should().HaveCount(2);
            snapshot.Quotes.Should().OnlyContain(quote =>
                !quote.Last.HasValue && !quote.Bid.HasValue && !quote.Ask.HasValue && !quote.LastDeltaBps.HasValue);
            snapshot.Quotes.Select(static quote => quote.Venue).Should().Equal("Alpha", "Beta");
        }
        finally
        {
            VenueSpreadService.TickerWait = old;
        }
    }

    [Fact]
    public async Task AVenueThatAnswers_TakesItsRow_TheSilentOneDashes()
    {
        var old = VenueSpreadService.TickerWait;
        VenueSpreadService.TickerWait = TimeSpan.FromMilliseconds(50);
        try
        {
            var silent = new StubAdapter(SilentVenue, "Silent");
            var live = new StubAdapter(LiveVenue, "Live")
            {
                StagedTicker = new Ticker(
                    Symbol.Parse("BTC/USDT"),
                    LastPrice: 65000m,
                    Bid: 64990m,
                    Ask: 65010m,
                    Volume24h: 1m,
                    PriceChange24hPercent: 0m,
                    Timestamp: DateTimeOffset.UtcNow),
            };
            var scene = new Scene(null, silent, live);

            var snapshot = await scene.Sampler.SampleAsync("BTC/USDT", TimeFrame.H1, 100);

            var silentRow = snapshot.Quotes.Single(quote => quote.Venue == "Silent");
            silentRow.Last.Should().BeNull();
            silentRow.LastDeltaBps.Should()
                .BeNull("a spread against one venue is the venue against itself — the dash");

            var liveRow = snapshot.Quotes.Single(quote => quote.Venue == "Live");
            liveRow.Last.Should().Be(65000m);
            liveRow.Bid.Should().Be(64990m);
            liveRow.Ask.Should().Be(65010m);
        }
        finally
        {
            VenueSpreadService.TickerWait = old;
        }
    }

    [Fact]
    public async Task TwoAnsweringVenues_CarryTheMirroredDeltasAroundTheMedian()
    {
        var low = new StubAdapter("low", "Low") { StagedTicker = TickerOf("BTC/USDT", 99m) };
        var high = new StubAdapter("high", "High") { StagedTicker = TickerOf("BTC/USDT", 101m) };
        var scene = new Scene(null, low, high);

        var snapshot = await scene.Sampler.SampleAsync("BTC/USDT", TimeFrame.H1, 100);

        snapshot.Quotes[0].LastDeltaBps.Should().BeApproximately(-100.0, 1e-9);
        snapshot.Quotes[1].LastDeltaBps.Should().BeApproximately(100.0, 1e-9);
    }

    [Fact]
    public async Task AnUnparsableText_ThrowsAndNeverFabricates()
    {
        var scene = new Scene(
            null,
            new StubAdapter("a", "Alpha"),
            new StubAdapter("b", "Beta"));

        // The two-separator text is the parser's refusal shape (a plain
        // word would parse as a stock ticker — the parser's own
        // convention); the one-symbol pass has nothing to answer with.
        var sample = () => scene.Sampler.SampleAsync("A/B/C", TimeFrame.H1, 100);

        await sample.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task TheBasis_ReadsEachVenueOwnCache_TheSiblingDerives()
    {
        var cache = new StubCandleCache();
        var closes = new decimal[] { 100m, 110m, 121m, 100m, 110m, 121m };
        cache.Stage("demo", "BTC/USDT", TimeFrame.H1, Series(closes));
        cache.Stage("demo", "BTC/USDT:USDT", TimeFrame.H1, Series(closes.Select(static close => close * 1.01m).ToArray()));
        var demo = new StubAdapter("demo", "Demo");
        var empty = new StubAdapter("empty", "Empty");
        var scene = new Scene(cache, demo, empty);

        var snapshot = await scene.Sampler.SampleAsync("BTC/USDT", TimeFrame.H1, 10);

        var demoBasis = snapshot.Bases.Single(basis => basis.Venue == "Demo");
        demoBasis.Samples.Should().HaveCount(6, "six bars pair over six shared bar times");
        demoBasis.Samples[^1].Bps.Should()
            .BeApproximately(100.0, 1e-9, "the perp's 1% premium over the spot is exactly +100 bps on every bar");
        var emptyBasis = snapshot.Bases.Single(basis => basis.Venue == "Empty");
        emptyBasis.Samples.Should().BeEmpty("a cache that cannot judge the pair answers the honest no-series");
    }

    [Fact]
    public async Task AStockSymbol_CarriesNoBasis_NeverAThrow()
    {
        var cache = new StubCandleCache();
        cache.Stage("demo", "AAPL", TimeFrame.H1, Series(100m, 110m, 121m));
        var demo = new StubAdapter("demo", "Demo");
        var scene = new Scene(cache, demo);

        var snapshot = await scene.Sampler.SampleAsync("AAPL", TimeFrame.H1, 10);

        snapshot.SymbolText.Should().Be("AAPL");
        snapshot.Bases.Single().Samples.Should().BeEmpty("a stock owns no perp sibling");
    }

    [Fact]
    public async Task TheReadBudget_CapsTheConcurrentCacheReads()
    {
        var cache = new StubCandleCache { SeriesDelay = TimeSpan.FromMilliseconds(80) };
        foreach (var adapterId in (string[])["a", "b", "c"])
        {
            cache.Stage(adapterId, "BTC/USDT", TimeFrame.H1, Series(100m, 110m, 121m));
            cache.Stage(adapterId, "BTC/USDT:USDT", TimeFrame.H1, Series(100m, 110m, 121m));
        }

        var scene = new Scene(
            cache,
            new StubAdapter("a", "Alpha"),
            new StubAdapter("b", "Beta"),
            new StubAdapter("c", "Gamma"));

        await scene.Sampler.SampleAsync("BTC/USDT", TimeFrame.H1, 10);

        cache.MaxInFlight.Should().BeLessThanOrEqualTo(
            VenueSpreadService.ReadBudget,
            "the pass's cache reads share the bounded budget");
    }

    [Fact]
    public async Task TheTypedText_NormalizesOntoTheSnapshot()
    {
        var demo = new StubAdapter("demo", "Demo");
        var scene = new Scene(null, demo);

        var snapshot = await scene.Sampler.SampleAsync("btc/usdt", TimeFrame.H1, 10);

        snapshot.SymbolText.Should().Be("BTC/USDT", "the parser's normalized form, not the typed text");
    }

    private static Ticker TickerOf(string symbolText, decimal last) => new(
        Symbol.Parse(symbolText),
        LastPrice: last,
        Bid: last * 0.9999m,
        Ask: last * 1.0001m,
        Volume24h: 1m,
        PriceChange24hPercent: 0m,
        Timestamp: DateTimeOffset.UtcNow);
}
