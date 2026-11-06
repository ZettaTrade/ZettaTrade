namespace ZettaTrade.Core.Entities;

using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Represents a trading order placed on an exchange.
/// </summary>
public sealed record Order
{
    /// <summary>Unique identifier for this order.</summary>
    public required string Id { get; init; }

    /// <summary>Exchange identifier where the order was placed.</summary>
    public required string ExchangeId { get; init; }

    /// <summary>Trading symbol (e.g., BTC/USDT).</summary>
    public required Symbol Symbol { get; init; }

    /// <summary>Order side (Buy or Sell).</summary>
    public required OrderSide Side { get; init; }

    /// <summary>Order type (Market, Limit, etc.).</summary>
    public required OrderType Type { get; init; }

    /// <summary>Order quantity.</summary>
    public required Quantity Quantity { get; init; }

    /// <summary>Order price (for limit orders).</summary>
    public required Price Price { get; init; }

    /// <summary>Current order status.</summary>
    public required OrderStatus Status { get; init; }

    /// <summary>Time-in-force policy.</summary>
    public required TimeInForce TimeInForce { get; init; }

    /// <summary>Order creation timestamp.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Last update timestamp.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>Filled quantity so far.</summary>
    public Quantity FilledQuantity { get; init; } = Quantity.Zero;

    /// <summary>Client-side order identifier.</summary>
    public string? ClientOrderId { get; init; }

    /// <summary>Stop price (for stop orders).</summary>
    public Price? StopPrice { get; init; }

    /// <summary>
    /// Trailing distance (for TrailingStop orders): how far the market may
    /// retrace from the favorable extreme before the stop triggers.
    /// Null for every other order type.
    /// </summary>
    public Price? TrailDistance { get; init; }

    /// <summary>
    /// OCO (one-cancels-the-other) group identifier: orders sharing a
    /// non-empty id form an either-or group — the first leg that fills or
    /// is cancelled takes the remaining legs down with it. Null for
    /// standalone orders.
    /// </summary>
    public string? OcoGroupId { get; init; }

    /// <summary>Exchange-assigned order identifier.</summary>
    public string? ExchangeOrderId { get; init; }

    /// <summary>Rejection reason (if status is Rejected).</summary>
    public string? RejectReason { get; init; }

    /// <summary>Expiry time (for GTD orders).</summary>
    public DateTimeOffset? Expiry { get; init; }
}
