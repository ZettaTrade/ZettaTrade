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
    /// <summary>Trading symbol.</summary>
    public required Symbol Symbol { get; init; }

    /// <summary>Bid side (buy orders).</summary>
    public required IReadOnlyList<PriceLevel> Bids { get; init; }

    /// <summary>Ask side (sell orders).</summary>
    public required IReadOnlyList<PriceLevel> Asks { get; init; }

    /// <summary>
    /// The snapshot's local receive moment: the <c>DateTimeOffset.UtcNow</c>
    /// the adapter's book ingestion point captured when the update arrived —
    /// never the exchange's own server stamp. One clock discipline across
    /// every adapter (T-142): a comparison against the book's age then
    /// measures the feed's pipeline latency only; an exchange clock would
    /// conflate transport speed with the local-vs-exchange clock offset.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Best bid price level.</summary>
    public PriceLevel? BestBid => Bids.Count > 0 ? Bids[0] : null;

    /// <summary>Best ask price level.</summary>
    public PriceLevel? BestAsk => Asks.Count > 0 ? Asks[0] : null;

    /// <summary>Mid price between best bid and ask.</summary>
    public decimal? MidPrice =>
        BestBid is { } bid && BestAsk is { } ask
            ? (bid.Price + ask.Price) / 2m
            : null;

    /// <summary>Spread between best bid and ask.</summary>
    public decimal? Spread =>
        BestBid is { } bid && BestAsk is { } ask
            ? ask.Price - bid.Price
            : null;
}

/// <summary>
/// Real-time ticker update. The three funding members (T-197) ride the
/// derivatives wires: a perpetual ticker carries the venue's funding rate,
/// the open interest and the next funding moment exactly as served; a spot
/// or stock ticker keeps the honest nulls — the absent fact is never a
/// fabricated zero.
/// </summary>
public readonly record struct Ticker(
    Symbol Symbol,
    decimal LastPrice,
    decimal Bid,
    decimal Ask,
    decimal Volume24h,
    decimal PriceChange24hPercent,
    DateTimeOffset Timestamp,
    decimal? FundingRate = null,
    decimal? OpenInterest = null,
    DateTimeOffset? NextFundingTime = null);
