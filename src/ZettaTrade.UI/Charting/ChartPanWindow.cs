namespace ZettaTrade.UI.Charting;

/// <summary>
/// Pure pan-window math for the candlestick chart: which slice of the
/// loaded bars paints with the pan offset applied, whether the window's
/// left edge stands on the loaded series' first bar (the older-history
/// fetch threshold), and the offset clamp. Unit-testable without pixels
/// (the chart control itself is UIA-invisible), the same contract as
/// <see cref="ChartBarLayout"/>.
/// </summary>
public static class ChartPanWindow
{
    /// <summary>
    /// The visible bar index span <c>[Start, End)</c> for the loaded series:
    /// <paramref name="panOffset"/> bars are hidden at the series' right end
    /// (zero leaves the newest bar riding the plot's right edge — the
    /// standing behavior), the window hangs <paramref name="visibleBars"/>
    /// wide to the left of that end, and the left edge clamps at the
    /// series' first bar (panning into history that is not loaded yet
    /// paints the honest empty space, it never wraps or invents bars).
    /// A degenerate input (no bars) yields an empty span.
    /// </summary>
    public static (int Start, int End) VisibleSpan(int barCount, int panOffset, int visibleBars)
    {
        if (barCount < 1)
        {
            return (0, 0);
        }

        var offset = ClampOffset(barCount, panOffset);
        var end = barCount - offset;
        var start = Math.Max(0, end - Math.Max(1, visibleBars));

        return (start, end);
    }

    /// <summary>
    /// The pan offset clamped to the loaded series: no negative offset
    /// (panning right of the newest bar wraps nothing — the window pins to
    /// the latest bar), nothing below one visible bar (offset = count would
    /// show an empty plot).
    /// </summary>
    public static int ClampOffset(int barCount, int requestedOffset) =>
        barCount < 1
            ? 0
            : Math.Clamp(requestedOffset, 0, barCount - 1);

    /// <summary>
    /// Whether the visible window's left edge stands on the loaded series'
    /// first bar — the point where further panning into older history
    /// depends on bars that are not loaded yet: the caller asks the
    /// ViewModel for the previous window.
    /// </summary>
    public static bool OlderFetchThresholdReached(int barCount, int panOffset, int visibleBars) =>
        VisibleSpan(barCount, panOffset, visibleBars).Start == 0;

    /// <summary>
    /// The whole bar slots a pan drag translates the visible window by:
    /// the dragged distance (taken from the drag's own anchor, not from
    /// per-event deltas) divided by the PAINTED bar pitch, rounded. The
    /// pitch is the caller's cached painted pitch — the draw pass computes
    /// it from the plot width (bounds minus the measured price gutter,
    /// BUG-048) — so one painted bar width of dragging moves the window
    /// exactly one slot. Ties round to even (the .NET default), the same
    /// as the pre-extraction math.
    /// </summary>
    public static int SlotsForDrag(double draggedDistance, double paintedBarPitch) =>
        (int)Math.Round(draggedDistance / paintedBarPitch);
}
