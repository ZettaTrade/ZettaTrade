namespace ZettaTrade.Core.Entities;

using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Represents a trading order.
/// </summary>
public sealed record Order
{
    public required string Id { get; init; }
    public required string ExchangeId { get; init; }
    public required Symbol Symbol { get; init; }
    public required OrderSide Side { get; init; }
    public required OrderType Type { get; init; }
    public required Quantity Quantity { get; init; }
    public required Price Price { get; init; }
    public required OrderStatus Status { get; init; }
    public required TimeInForce TimeInForce { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public Quantity FilledQuantity { get; init; } = Quantity.Zero;
    public string? ClientOrderId { get; init; }
    public Price? StopPrice { get; init; }
    public string? ExchangeOrderId { get; init; }
    public string? RejectReason { get; init; }
    public DateTimeOffset? Expiry { get; init; }
}
