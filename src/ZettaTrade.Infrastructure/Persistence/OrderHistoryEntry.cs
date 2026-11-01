namespace ZettaTrade.Infrastructure.Persistence;

using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;

/// <summary>
/// One row of the persistent order journal (SQLite). Mirrors
/// <see cref="Order"/> field by field — the store maps rows back to
/// <see cref="Order"/> when reading; nothing else (no credentials, no
/// secrets) ever gets a column in this table.
/// </summary>
public sealed class OrderHistoryEntry
{
    /// <summary>Exchange identifier this order belongs to; part of the primary key.</summary>
    public required string ExchangeId { get; set; }

    /// <summary>Adapter order id; part of the primary key (unique per exchange).</summary>
    public required string OrderId { get; set; }

    /// <summary>Normalized symbol (BASE/QUOTE).</summary>
    public required string Symbol { get; set; }

    /// <summary>Order side.</summary>
    public required OrderSide Side { get; set; }

    /// <summary>Order type.</summary>
    public required OrderType Type { get; set; }

    /// <summary>Order quantity.</summary>
    public required decimal Quantity { get; set; }

    /// <summary>Limit/stop price the order was placed with.</summary>
    public required decimal Price { get; set; }

    /// <summary>Lifecycle state at the time of the last write.</summary>
    public required OrderStatus Status { get; set; }

    /// <summary>Time-in-force policy.</summary>
    public required TimeInForce TimeInForce { get; set; }

    /// <summary>Order creation timestamp.</summary>
    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>Last update timestamp (e.g. the cancellation moment).</summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>Filled quantity so far.</summary>
    public required decimal FilledQuantity { get; set; }

    /// <summary>Client-side order identifier, if the flow assigned one.</summary>
    public string? ClientOrderId { get; set; }

    /// <summary>Stop price (stop order types).</summary>
    public decimal? StopPrice { get; set; }

    /// <summary>Exchange-assigned order identifier, if the adapter reports one.</summary>
    public string? ExchangeOrderId { get; set; }

    /// <summary>Rejection reason (Rejected status).</summary>
    public string? RejectReason { get; set; }

    /// <summary>Expiry time (GTD orders).</summary>
    public DateTimeOffset? Expiry { get; set; }
}
