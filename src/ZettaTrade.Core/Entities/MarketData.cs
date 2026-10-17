namespace ZettaTrade.Core.Entities;

using ZettaTrade.Core.ValueObjects;

/// <summary>
/// OHLCV candlestick bar.
/// </summary>
public readonly record struct Bar(
    DateTimeOffset Time,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);

/// <summary>
/// Top-of-book price level.
/// </summary>
public readonly record struct PriceLevel(decimal Price, decimal Quantity);

/// <summary>
/// Order book snapshot.
/// </summary>
public sealed record OrderBook
{
    public required Symbol Symbol { get; init; }
    public required IReadOnlyList<PriceLevel> Bids { get; init; }
    public required IReadOnlyList<PriceLevel> Asks { get; init; }
    public required DateTimeOffset Timestamp { get; init; }

    public PriceLevel? BestBid => Bids.Count > 0 ? Bids[0] : null;
    public PriceLevel? BestAsk => Asks.Count > 0 ? Asks[0] : null;

    public decimal? MidPrice =>
        BestBid is { } bid && BestAsk is { } ask
            ? (bid.Price + ask.Price) / 2m
            : null;

    public decimal? Spread =>
        BestBid is { } bid && BestAsk is { } ask
            ? ask.Price - bid.Price
            : null;
}

/// <summary>
/// Real-time ticker update.
/// </summary>
public readonly record struct Ticker(
    Symbol Symbol,
    decimal LastPrice,
    decimal Bid,
    decimal Ask,
    decimal Volume24h,
    decimal PriceChange24hPercent,
    DateTimeOffset Timestamp);
