namespace ZettaTrade.Infrastructure.Exchange;

using ZettaTrade.Core.Enums;

/// <summary>
/// The Bybit v5 <c>orderStatus</c> to Core <see cref="OrderStatus"/> map for
/// the venue trading surface (T-127): the statuses the limit-order lifecycle
/// carries over the private stream and the open-orders query, mapped onto
/// the Core lifecycle the panels and the journal speak. An unknown venue
/// string is an error — never a substituted default (trading-safety: an
/// unparsable enum must not flow toward execution or history as a guess).
/// </summary>
/// <remarks>
/// The map covers the documented v5 set the spot limit flow reaches; the
/// conditional-order statuses (<c>Untriggered</c>, <c>Triggered</c>) have no
/// Core equivalent in this surface's limit-only scope and are therefore
/// also errors. The venue's older <c>Created</c> ack status is kept as a
/// documented alias of <see cref="OrderStatus.Submitted"/> so a late spot
/// ack frame still maps instead of erroring.
/// </remarks>
public static class BybitOrderStatusMapper
{
    /// <summary>Map one venue <c>orderStatus</c> string to the Core status.</summary>
    /// <param name="orderStatus">The venue status value, verbatim.</param>
    /// <returns>The mapped Core status.</returns>
    /// <exception cref="InvalidOperationException">
    /// The venue status is unknown to this surface — no Core mapping would
    /// be a fabrication.
    /// </exception>
    public static OrderStatus Map(string orderStatus) => orderStatus switch
    {
        FrameStatuses.New => OrderStatus.Submitted,
        FrameStatuses.Created => OrderStatus.Submitted,
        FrameStatuses.PartiallyFilled => OrderStatus.PartiallyFilled,
        FrameStatuses.Filled => OrderStatus.Filled,
        FrameStatuses.Cancelled => OrderStatus.Cancelled,
        FrameStatuses.PartiallyFilledCanceled => OrderStatus.Cancelled,
        FrameStatuses.Deactivated => OrderStatus.Cancelled,
        FrameStatuses.Rejected => OrderStatus.Rejected,
        _ => throw new InvalidOperationException(
            $"Bybit orderStatus '{orderStatus}' has no Core mapping on the testnet trading surface; " +
            "the update is refused rather than defaulted."),
    };

    /// <summary>The venue status strings this map serves (the v5 enum set).</summary>
    private static class FrameStatuses
    {
        public const string New = "New";
        public const string Created = "Created";
        public const string PartiallyFilled = "PartiallyFilled";
        public const string Filled = "Filled";
        public const string Cancelled = "Cancelled";
        public const string PartiallyFilledCanceled = "PartiallyFilledCanceled";
        public const string Deactivated = "Deactivated";
        public const string Rejected = "Rejected";
    }
}
