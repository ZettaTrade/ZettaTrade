namespace ZettaTrade.Core.Entities;

using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Represents a public market trade (from exchange trade stream).
/// Does not include user-specific data like OrderId or Fee.
/// Used for public trade feeds (tape).
/// </summary>
public sealed record MarketTrade
{
    /// <summary>Unique trade identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Trading symbol.</summary>
    public required Symbol Symbol { get; init; }

    /// <summary>
    /// Trade side (aggressor side). The neutral <see cref="OrderSide.None"/>
    /// (T-198) states the wire's own absence: a stock feed's tape serves no
    /// aggressor (a trade is a matched buy and a matched sell), so the tape
    /// fact is the price, the size and the time — never a fabricated side.
    /// </summary>
    public required OrderSide Side { get; init; }

    /// <summary>Trade price.</summary>
    public required Price Price { get; init; }

    /// <summary>Trade quantity.</summary>
    public required Quantity Quantity { get; init; }

    /// <summary>Trade timestamp.</summary>
    public required DateTimeOffset Timestamp { get; init; }
}
