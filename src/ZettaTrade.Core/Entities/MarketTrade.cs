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
    public required string Id { get; init; }
    public required Symbol Symbol { get; init; }
    public required OrderSide Side { get; init; }
    public required Price Price { get; init; }
    public required Quantity Quantity { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}
