namespace ZettaTrade.Infrastructure.Demo;

using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Deterministic canned-data market data adapter. It exists to give the
/// exchange selector a second registered entry (T-016) without any network,
/// keys or geolocation constraints, and will be replaced by a real exchange
/// adapter in a dedicated task.
/// </summary>
/// <remarks>
/// Bars are derived from a stable per-(symbol, time frame) seed, so the same
/// request always yields the same sequence — tests can pin exact values by
/// passing an explicit <c>to</c> anchor. Unknown symbols raise instead of
/// falling back to defaults (trading safety: never substitute defaults for
/// invalid symbols). Stream subscriptions emit their first snapshot at
/// subscribe time and then follow a fixed cadence; timers die with the
/// subscription.
/// </remarks>
public sealed class DemoMarketDataAdapter : IMarketDataAdapter
{
    #region Constants

    private const string AdapterId = "demo";
    private const string AdapterName = "Demo";

    /// <summary>Number of order book levels each side carries.</summary>
    private const int BookDepth = 10;

    /// <summary>Relative half-width of the canned spread per level.</summary>
    private const decimal LevelSpreadFactor = 0.0001m;

    private static readonly TimeSpan BookRefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TickerRefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TradeRefreshInterval = TimeSpan.FromSeconds(2);

    /// <summary>The fixed tradable universe (a Binance subset).</summary>
    private static readonly Symbol[] SupportedSymbols =
    [
        Symbol.Parse("BTC/USDT"),
        Symbol.Parse("ETH/USDT"),
        Symbol.Parse("SOL/USDT"),
        Symbol.Parse("BTC/USDC"),
        Symbol.Parse("XRP/USDT"),
        Symbol.Parse("ETH/BTC"),
    ];

    /// <summary>Seeded anchor price per normalized symbol.</summary>
    private static readonly IReadOnlyDictionary<string, decimal> AnchorPrices = new Dictionary<string, decimal>
    {
        ["BTC/USDT"] = 67000m,
        ["ETH/USDT"] = 3500m,
        ["SOL/USDT"] = 170m,
        ["BTC/USDC"] = 67110m,
        ["XRP/USDT"] = 0.62m,
        ["ETH/BTC"] = 0.0522m,
    };

    #endregion Constants

    #region Properties

    /// <inheritdoc/>
    public string Id => AdapterId;

    /// <inheritdoc/>
    public string Name => AdapterName;

    /// <summary>There is nothing to connect to; the canned surface is always up.</summary>
    public bool IsConnected { get; private set; }

    /// <inheritdoc/>
    public ExchangeCapabilities Capabilities =>
        ExchangeCapabilities.MarketData | ExchangeCapabilities.OrderBook | ExchangeCapabilities.HistoricalData;

    #endregion Properties

    #region Constructor

    private readonly ILogger<DemoMarketDataAdapter> _logger;

    /// <summary>
    /// Create a connected demo adapter. Canned data needs no asynchronous
    /// initialization: the composition root never has to await this type.
    /// </summary>
    public DemoMarketDataAdapter(ILogger<DemoMarketDataAdapter> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        IsConnected = true;
    }

    #endregion Constructor

    #region Public Methods

    /// <inheritdoc/>
    public Task<IReadOnlyList<Symbol>> GetSymbolsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<Symbol>>(SupportedSymbols);
    }

    /// <inheritdoc/>
    public IObservable<Ticker> SubscribeTicker(Symbol symbol)
    {
        Validate(symbol);
        return Cadence(() => BuildTicker(symbol), TickerRefreshInterval);
    }

    /// <inheritdoc/>
    public IObservable<OrderBook> SubscribeOrderBook(Symbol symbol, int depth = 20)
    {
        Validate(symbol);
        return Cadence(BuildBooks, BookRefreshInterval);

        OrderBook BuildBooks() => BuildOrderBook(symbol, depth);
    }

    /// <inheritdoc/>
    public IObservable<MarketTrade> SubscribeTrades(Symbol symbol)
    {
        Validate(symbol);
        return Cadence(NextTrade, TradeRefreshInterval);

        MarketTrade NextTrade() => BuildTrade(symbol);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Bar>> GetBarsAsync(
        Symbol symbol,
        TimeFrame timeFrame,
        DateTimeOffset from,
        DateTimeOffset? to = null,
        int limit = 500,
        CancellationToken ct = default)
    {
        Validate(symbol);

        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "The bar limit must be positive.");
        }

        ct.ThrowIfCancellationRequested();

        var anchor = CandleOpen(to ?? DateTimeOffset.UtcNow, timeFrame);
        return Task.FromResult<IReadOnlyList<Bar>>(BuildBars(symbol, timeFrame, from, anchor, limit));
    }

    /// <summary>Connect is a no-op: the canned surface never disconnects.</summary>
    public Task ConnectAsync(CancellationToken ct = default)
    {
        IsConnected = true;
        _logger.LogInformation("Demo adapter connected");
        return Task.CompletedTask;
    }

    /// <summary>Disconnect only flips the flag; the next connect is instant.</summary>
    public Task DisconnectAsync(CancellationToken ct = default)
    {
        IsConnected = false;
        _logger.LogInformation("Demo adapter disconnected");
        return Task.CompletedTask;
    }

    /// <summary>The adapter owns no resources: disposal is a no-op.</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    #endregion Public Methods

    #region Private Methods

    /// <summary>
    /// Emit one snapshot at subscribe time (computed inside the subscription,
    /// so timestamps are honest per subscriber) and keep the cadence running
    /// until the subscription dies.
    /// </summary>
    private static IObservable<TSource> Cadence<TSource>(Func<TSource> build, TimeSpan interval) =>
        Observable
            .Defer(() => Observable.Return(build()))
            .Concat(Observable.Interval(interval).Select(_ => build()));

    /// <summary>
    /// Reject an unknown symbol with an exception: the demo universe is a
    /// closed, documented list, and silent defaults are forbidden by the
    /// trading-safety rules.
    /// </summary>
    private static void Validate(Symbol symbol)
    {
        if (!AnchorPrices.ContainsKey(symbol.Normalized))
        {
            throw new ArgumentException(
                $"Symbol '{symbol.Normalized}' is not supported by the Demo adapter.",
                nameof(symbol));
        }
    }

    private uint _tradeSequence;

    private MarketTrade BuildTrade(Symbol symbol)
    {
        var price = AnchorPrices[symbol.Normalized];
        var sequence = ++_tradeSequence;

        return new MarketTrade
        {
            Id = $"demo-{symbol.Normalized}-{sequence}",
            Symbol = symbol,
            Side = sequence % 2 == 0 ? OrderSide.Sell : OrderSide.Buy,
            Price = new Price(price * (sequence % 2 == 0 ? 0.9999m : 1.0001m)),
            Quantity = new Quantity(0.25m + sequence % 3),
            Timestamp = DateTimeOffset.UtcNow,
        };
    }

    private Ticker BuildTicker(Symbol symbol)
    {
        var price = AnchorPrices[symbol.Normalized];
        var halfSpread = price * LevelSpreadFactor;

        return new Ticker(
            symbol,
            price,
            price - halfSpread,
            price + halfSpread,
            Volume24h(symbol),
            PriceChange24h(symbol),
            DateTimeOffset.UtcNow);
    }

    private static OrderBook BuildOrderBook(Symbol symbol, int depth)
    {
        var price = AnchorPrices[symbol.Normalized];
        var levels = Math.Min(depth, BookDepth);
        var seed = StableSeed(symbol.Normalized);

        var bids = new List<PriceLevel>(levels);
        var asks = new List<PriceLevel>(levels);
        for (var i = 0; i < levels; i++)
        {
            var step = price * LevelSpreadFactor * (i + 1);
            bids.Add(new PriceLevel(price - step, LevelQuantity(ref seed)));
            asks.Add(new PriceLevel(price + step, LevelQuantity(ref seed)));
        }

        return new OrderBook
        {
            Symbol = symbol,
            Bids = bids,
            Asks = asks,
            Timestamp = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Bars for one request: opening times step back from the anchor onto the
    /// time frame boundary until <c>from</c>, cap at <c>limit</c>, and the
    /// seeded walk fills them ascending (same request ⇒ same bars).
    /// </summary>
    private IReadOnlyList<Bar> BuildBars(
        Symbol symbol,
        TimeFrame timeFrame,
        DateTimeOffset from,
        DateTimeOffset anchorOpen,
        int limit)
    {
        var step = TimeFrameSeconds(timeFrame);

        var opens = new List<DateTimeOffset> { anchorOpen };
        while (opens[^1].ToUnixTimeSeconds() - step >= from.ToUnixTimeSeconds() && opens.Count < limit)
        {
            opens.Add(opens[^1].AddSeconds(-step));
        }

        opens.Reverse();

        var price = AnchorPrices[symbol.Normalized];
        var seed = StableSeed(symbol.Normalized, (int)timeFrame);
        var previousClose = price;

        var bars = new List<Bar>(opens.Count);
        foreach (var open in opens)
        {
            var openPrice = previousClose;
            var close = openPrice * (1m + BarStep(ref seed));
            var wickUp = (Next(ref seed) % 5 + 1) * 0.0005m;

            bars.Add(new Bar(
                open,
                openPrice,
                Math.Max(openPrice, close) * (1m + wickUp),
                Math.Min(openPrice, close) * (1m - wickUp),
                close,
                BarVolume(ref seed)));

            previousClose = close;
        }

        return bars;
    }

    /// <summary>Seconds per candle for the time frame (MN1 = 30 days).</summary>
    private static int TimeFrameSeconds(TimeFrame timeFrame) => timeFrame switch
    {
        TimeFrame.M1 => 60,
        TimeFrame.M5 => 5 * 60,
        TimeFrame.M15 => 15 * 60,
        TimeFrame.M30 => 30 * 60,
        TimeFrame.H1 => 60 * 60,
        TimeFrame.H4 => 4 * 60 * 60,
        TimeFrame.D1 => 24 * 60 * 60,
        TimeFrame.W1 => 7 * 24 * 60 * 60,
        TimeFrame.MN1 => 30 * 24 * 60 * 60,
        _ => throw new ArgumentOutOfRangeException(nameof(timeFrame), timeFrame, "Unknown time frame."),
    };

    /// <summary>Floor a timestamp onto its time frame's opening boundary.</summary>
    private static DateTimeOffset CandleOpen(DateTimeOffset moment, TimeFrame timeFrame)
    {
        var step = TimeFrameSeconds(timeFrame);
        var unix = moment.ToUnixTimeSeconds();
        return DateTimeOffset.FromUnixTimeSeconds(unix - unix % step);
    }

    /// <summary>Seeded relative bar step in (−1%, +1%).</summary>
    private static decimal BarStep(ref uint seed) => (Next(ref seed) % 2001 - 1000) / 100000m;

    private static decimal Volume24h(Symbol symbol) => StableSeed(symbol.Normalized) % 900 + 100m;

    private static decimal PriceChange24h(Symbol symbol) => (StableSeed(symbol.Normalized) % 21 - 10) / 10m;

    private static decimal LevelQuantity(ref uint seed) => (Next(ref seed) % 500 + 50) / 10m;

    private static decimal BarVolume(ref uint seed) => Next(ref seed) % 1000 + 50m;

    /// <summary>
    /// Stable 32-bit seed over the text (and an optional salt): the same
    /// inputs always start the same sequence.
    /// </summary>
    private static uint StableSeed(string text, int salt = 0)
    {
        unchecked
        {
            uint seed = 2166136261u;
            foreach (var ch in text)
            {
                seed = (seed ^ ch) * 16777619u;
            }

            return seed ^ (uint)salt * 2654435761u;
        }
    }

    /// <summary>Standard 32-bit LCG step (Numerical Recipes constants).</summary>
    private static uint Next(ref uint seed) => seed = seed * 1664525u + 1013904223u;

    #endregion Private Methods
}
