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
/// The correlation computer's pins (T-252): a pass over an empty cache
/// answers the honest all-dash matrix (the symbols keep their slots), a
/// cached pair computes, the pass's cache reads share the two-slot read
/// budget, an unparsable text refuses (never a fabricated row), a roster
/// over the cap truncates with the honesty flag, and a 20-symbol matrix
/// walks in under the budget without storming the cache — the worker
/// discipline the panel's off-UI acceptance rides.
/// </summary>
public sealed class CorrelationServiceTests
{
    #region Scene kit

    /// <summary>The computer's whole scene: the facade over one adapter, the cache, the computer.</summary>
    private sealed class Scene
    {
        public Scene(ICandleCache? cache = null, IMarketDataAdapter? adapter = null)
        {
            var settings = Substitute.For<ZettaTrade.Core.Interfaces.ISettingsService>();
            settings.Changes.Returns(System.Reactive.Linq.Observable.Empty<SettingsChange>());

            Exchange = new ActiveExchangeService(
                [adapter ?? new RecordingMarketData()],
                settings,
                NullLogger<ActiveExchangeService>.Instance);
            Cache = cache ?? new StubCandleCache();
            Computer = new CorrelationService(
                Exchange,
                Cache,
                NullLogger<CorrelationService>.Instance);
        }

        public ActiveExchangeService Exchange { get; }

        public ICandleCache Cache { get; }

        public CorrelationService Computer { get; }
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

        /// <summary>Stage one symbol's series (the adapter snapshot the facade serves is the demo one).</summary>
        public void Stage(string symbolText, TimeFrame frame, IReadOnlyList<Bar> bars)
        {
            Series[("demo", Symbol.Parse(symbolText).Normalized, frame)] = bars;
        }
    }

    /// <summary>The market-data void: the facade needs one registered adapter; the computer never calls it.</summary>
    internal sealed class RecordingMarketData : IMarketDataAdapter
    {
        public string Id => "demo";

        public string Name => "demo";

        public bool IsConnected => true;

        public ExchangeCapabilities Capabilities => ExchangeCapabilities.MarketData;

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

        public IObservable<Ticker> SubscribeTicker(Symbol symbol) => Observable.Never<Ticker>();

        public IObservable<OrderBook> SubscribeOrderBook(Symbol symbol, int depth = 20) => Observable.Never<OrderBook>();

        public IObservable<MarketTrade> SubscribeTrades(Symbol symbol) => Observable.Never<MarketTrade>();

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

    #endregion Scene kit

    [Fact]
    public async Task APass_OverAnEmptyCache_AnswersTheHonestAllDashMatrix()
    {
        var scene = new Scene();

        var snapshot = await scene.Computer.ComputeAsync(["BTC/USDT", "ETH/USDT"], TimeFrame.H1, 50);

        snapshot.Symbols.Should().Equal("BTC/USDT", "ETH/USDT");
        snapshot.Matrix.Rank.Should().Be(2);
        snapshot.Matrix[0, 0].Should().BeNull();
        snapshot.Matrix[0, 1].Should().BeNull("no cached coverage — the honest dash");
        snapshot.Matrix[1, 1].Should().BeNull();
        snapshot.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task APass_OverCachedSeries_ComputesThePairsAndTheDiagonals()
    {
        var cache = new StubCandleCache();
        var closes = new decimal[] { 100m, 110m, 121m, 100m, 110m, 121m };
        cache.Stage("BTC/USDT", TimeFrame.H1, Series(closes));
        cache.Stage("ETH/USDT", TimeFrame.H1, Series(closes.Select(static close => close * 2m).ToArray()));
        var scene = new Scene(cache);

        // The window of ten asks an overlap of five — exactly what the
        // staged six-bar series share (the varied walk keeps the variance
        // alive); ETH's closes double BTC's, so the returns mirror.
        var snapshot = await scene.Computer.ComputeAsync(["BTC/USDT", "ETH/USDT"], TimeFrame.H1, 10);

        snapshot.Matrix[0, 1]!.Value.Should().BeApproximately(1.0, 1e-9, "ETH's returns mirror BTC's exactly");
        snapshot.Matrix[0, 0]!.Value.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public async Task TheReadBudget_CapsTheConcurrentReads()
    {
        var cache = new StubCandleCache { SeriesDelay = TimeSpan.FromMilliseconds(100) };
        var symbols = Enumerable.Range(0, 8).Select(i => $"SYM{i}/USDT").ToArray();
        foreach (var symbol in symbols)
        {
            cache.Stage(symbol, TimeFrame.H1, Series(100m, 110m, 121m));
        }

        var scene = new Scene(cache);
        await scene.Computer.ComputeAsync(symbols, TimeFrame.H1, 50);

        cache.MaxInFlight.Should().BeLessThanOrEqualTo(
            CorrelationService.ReadBudget,
            "the pass's cache reads share the bounded budget");
    }

    [Fact]
    public async Task AnUnparsableText_IsRefusedNeverFabricated()
    {
        var cache = new StubCandleCache();
        cache.Stage("BTC/USDT", TimeFrame.H1, Series(100m, 110m, 121m));
        var scene = new Scene(cache);

        // The two-separator text is the parser's refusal shape (a plain
        // word would parse as a stock ticker — the parser's own
        // convention).
        var snapshot = await scene.Computer.ComputeAsync(["BTC/USDT", "A/B/C"], TimeFrame.H1, 50);

        snapshot.Symbols.Should().Equal(["BTC/USDT"], "the refused text owns no matrix slot");
        snapshot.Matrix.GetLength(0).Should().Be(1, "the refused text owns no matrix slot");
    }

    [Fact]
    public async Task ARoster_OverTheCap_TruncatesWithTheHonestyFlag()
    {
        var cache = new StubCandleCache();
        var symbols = Enumerable.Range(0, CorrelationService.MaxSymbols + 5)
            .Select(i => $"SYM{i}/USDT").ToArray();
        foreach (var symbol in symbols)
        {
            cache.Stage(symbol, TimeFrame.H1, Series(100m, 110m, 121m));
        }

        var scene = new Scene(cache);
        var snapshot = await scene.Computer.ComputeAsync(symbols, TimeFrame.H1, 50);

        snapshot.Symbols.Count.Should().Be(CorrelationService.MaxSymbols);
        snapshot.Truncated.Should().BeTrue("the roster exceeded the cap — the snapshot says so");
        snapshot.Symbols.Should().BeInAscendingOrder(StringComparer.Ordinal, "the pass sorts the set");
    }

    [Fact]
    public async Task AShortRoster_NeverCarriesTheTruncationFlag()
    {
        var cache = new StubCandleCache();
        var symbols = Enumerable.Range(0, 5).Select(i => $"SYM{i}/USDT").ToArray();
        foreach (var symbol in symbols)
        {
            cache.Stage(symbol, TimeFrame.H1, Series(100m, 110m, 121m));
        }

        var scene = new Scene(cache);
        var snapshot = await scene.Computer.ComputeAsync(symbols, TimeFrame.H1, 50);

        snapshot.Truncated.Should().BeFalse();
        snapshot.Symbols.Count.Should().Be(5);
    }

    [Fact]
    public async Task TheWorkerBudget_A20SymbolMatrix_WalksInWithoutStorming()
    {
        // The acceptance's shape: twenty symbols, each read carrying a
        // real latency, the whole pass completing under the budget's
        // pacing (two flights of 20 reads x 50 ms ~ 500 ms; a full storm
        // would answer in one 50 ms wave, a serialized walk in 1000 ms —
        // both observable against the honest budget's floor).
        var cache = new StubCandleCache { SeriesDelay = TimeSpan.FromMilliseconds(50) };
        var closes = new decimal[] { 100m, 110m, 121m, 100m, 110m, 121m };
        var symbols = Enumerable.Range(0, 20).Select(i => $"SYM{i}/USDT").ToArray();
        foreach (var symbol in symbols)
        {
            cache.Stage(symbol, TimeFrame.H1, Series(closes));
        }

        var scene = new Scene(cache);
        var started = DateTimeOffset.UtcNow;
        var snapshot = await scene.Computer.ComputeAsync(symbols, TimeFrame.H1, 10);
        var elapsed = DateTimeOffset.UtcNow - started;

        snapshot.Symbols.Count.Should().Be(20);
        snapshot.Matrix.Rank.Should().Be(2);
        snapshot.Matrix[0, 1].Should().NotBeNull("every staged pair computes");
        cache.MaxInFlight.Should().BeLessThanOrEqualTo(CorrelationService.ReadBudget);
        elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(5),
            "the budgeted walk (20 reads x 50 ms over two flights) answers in under a second — five seconds would mean a broken lane");
    }

    [Fact]
    public async Task AWindow_OverlappedByTheCacheLimit_ClampsTheOverlapHonestly()
    {
        // The cache carries fewer bars than the window asks: the overlap
        // floor (half the requested window) then dashes every pair — the
        // honest gap, never a thin coefficient dressed up.
        var cache = new StubCandleCache();
        cache.Stage("BTC/USDT", TimeFrame.H1, Series(100m, 110m, 121m, 100m));
        cache.Stage("ETH/USDT", TimeFrame.H1, Series(100m, 110m, 121m, 100m));
        var scene = new Scene(cache);

        // The window of 50 asks an overlap of 25; the two series own four
        // returns each — the pair dashes.
        var snapshot = await scene.Computer.ComputeAsync(["BTC/USDT", "ETH/USDT"], TimeFrame.H1, 50);

        snapshot.Matrix[0, 1].Should().BeNull("four shared returns sit below the floor of 25");
    }
}
