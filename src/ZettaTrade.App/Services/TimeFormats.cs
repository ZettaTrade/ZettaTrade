namespace ZettaTrade.App.Services;

/// <summary>
/// Shared display formats for timestamp columns across the trade flows
/// (positions panel, order history): one source so the tables never drift.
/// </summary>
public static class TimeFormats
{
    /// <summary>Column-friendly, culture-pinned order/position timestamp.</summary>
    public const string OrderTimestamp = "yyyy-MM-dd HH:mm";

    /// <summary>Plain capture clock for the order ticket's sizing snapshots (a snapshot's staleness shown as taken-at).</summary>
    public const string SizeSnapshotTime = "HH:mm:ss";

    /// <summary>The notification and history rows' moment prefix (a toast lives for seconds; the rows carry no date).</summary>
    public const string NotificationMoment = "HH:mm:ss";
}
