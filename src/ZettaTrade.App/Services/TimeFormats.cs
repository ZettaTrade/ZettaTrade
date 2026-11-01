namespace ZettaTrade.App.Services;

/// <summary>
/// Shared display formats for timestamp columns across the trade flows
/// (positions panel, order history): one source so the tables never drift.
/// </summary>
public static class TimeFormats
{
    /// <summary>Column-friendly, culture-pinned order/position timestamp.</summary>
    public const string OrderTimestamp = "yyyy-MM-dd HH:mm";
}
