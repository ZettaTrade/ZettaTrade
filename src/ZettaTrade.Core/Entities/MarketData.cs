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

/// <summary>
/// One forced liquidation event of a venue's derivatives wire (T-242):
/// the forced close of a position the exchange executed at its margin
/// call, as the venue published it. Every member is a wire fact — the
/// price is the venue's served forced price (Bybit's bankruptcy price,
/// Binance's average forced fill), the quantity the venue's served size
/// (the executed size, the accumulated fill), the timestamp the venue's
/// event moment. The side is the venue-normalized
/// <see cref="Enums.LiquidationSide"/> — the POSITION that was force-closed
/// — never a venue's raw dialect field; the adapters own that mapping.
/// </summary>
public sealed record LiquidationEvent
{
    /// <summary>The subscription's app symbol the event resolved to.</summary>
    public required Symbol Symbol { get; init; }

    /// <summary>The side of the position the liquidation force-closed.</summary>
    public required Enums.LiquidationSide LiquidatedSide { get; init; }

    /// <summary>The venue's served forced price.</summary>
    public required Price Price { get; init; }

    /// <summary>The venue's served executed size.</summary>
    public required Quantity Quantity { get; init; }

    /// <summary>The venue's event moment (its own server clock).</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>The event's notional value at the served price and size (the computed display fact; the tape's magnitude ordering rides it).</summary>
    public decimal Notional => Price.Value * Quantity.Value;
}
