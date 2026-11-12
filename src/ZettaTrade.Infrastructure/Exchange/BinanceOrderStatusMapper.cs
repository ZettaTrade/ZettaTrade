namespace ZettaTrade.Infrastructure.Exchange;

using ZettaTrade.Core.Enums;

/// <summary>
/// The Binance spot order status to Core <see cref="OrderStatus"/> map for
/// the venue trading surface (T-193): the statuses the limit-order lifecycle
/// carries over the user-data stream and the open-orders query, mapped onto
/// the Core lifecycle the panels and the journal speak. An unknown venue
/// string is an error — never a substituted default (trading-safety: an
/// unparsable enum must not flow toward execution or history as a guess).
/// </summary>
/// <remarks>
/// The map covers the documented spot set the limit flow reaches. The
/// transitional statuses (<c>PENDING_NEW</c>, <c>PENDING_CANCEL</c>,
/// <c>REPLACED</c>) have no Core equivalent in this surface's limit-only
/// scope and are therefore also errors, exactly like the Bybit surface
/// refuses its own conditional statuses (T-127). <c>EXPIRED</c> maps onto
/// <see cref="OrderStatus.Cancelled"/>: the order is no longer working —
/// the same honest family the Bybit map put <c>Deactivated</c> in.
/// </remarks>
public static class BinanceOrderStatusMapper
{
    /// <summary>Map one venue order status string to the Core status.</summary>
    /// <param name="orderStatus">The venue status value, verbatim.</param>
    /// <returns>The mapped Core status.</returns>
    /// <exception cref="InvalidOperationException">
    /// The venue status is unknown to this surface — no Core mapping would
    /// be a fabrication.
    /// </exception>
    public static OrderStatus Map(string orderStatus) => orderStatus switch
    {
        FrameStatuses.New => OrderStatus.Submitted,
        FrameStatuses.PartiallyFilled => OrderStatus.PartiallyFilled,
        FrameStatuses.Filled => OrderStatus.Filled,
        FrameStatuses.Cancelled => OrderStatus.Cancelled,
        FrameStatuses.Expired => OrderStatus.Cancelled,
        FrameStatuses.Rejected => OrderStatus.Rejected,
        _ => throw new InvalidOperationException(
            $"Binance order status '{orderStatus}' has no Core mapping on the testnet trading surface; " +
            "the update is refused rather than defaulted."),
    };

    /// <summary>The venue status strings this map serves (the spot enum set).</summary>
    private static class FrameStatuses
    {
        public const string New = "NEW";
        public const string PartiallyFilled = "PARTIALLY_FILLED";
        public const string Filled = "FILLED";
        public const string Cancelled = "CANCELED";
        public const string Expired = "EXPIRED";
        public const string Rejected = "REJECTED";
    }
}
