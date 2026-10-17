namespace ZettaTrade.Core.Entities;

using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Represents an executed trade (user's trade).
/// </summary>
public sealed record Trade
{
    public required string Id { get; init; }
    public required string OrderId { get; init; }
    public required Symbol Symbol { get; init; }
    public required OrderSide Side { get; init; }
    public required Price Price { get; init; }
    public required Quantity Quantity { get; init; }
    public required Money Fee { get; init; }
    public required DateTimeOffset ExecutedAt { get; init; }
}
