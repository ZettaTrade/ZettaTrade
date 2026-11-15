namespace ZettaTrade.App.Services;

using ZettaTrade.Core.Entities;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// The funding pane's one aligner (T-240): a pure map from the chart's bar
/// series and the venue's funding history onto the per-bar pane values the
/// renderer draws. The per-bar value is the LAST funding event at or before
/// the bar's open time — the rate that governed that bar's interval — and a
/// bar before the history's first event reads null, the honest gap the
/// polyline breaks on (never an interpolated or zero-filled stretch). Both
/// inputs are read as ascending series (the app's bar and funding
/// contracts), so one shared linear walk aligns the whole window.
/// </summary>
public static class FundingSeriesAlignment
{
    /// <summary>
    /// Align the funding history onto the bars: one value per bar slot, the
    /// governing event's rate as the wire serves it (a fraction, 0.0001 =
    /// 0.01%) or null before the first event.
    /// </summary>
    /// <param name="bars">The chart's bar series, ascending by open time.</param>
    /// <param name="points">The funding history, ascending by event time.</param>
    /// <returns>One value per bar slot, index-aligned with the bars.</returns>
    public static IReadOnlyList<decimal?> Align(IReadOnlyList<Bar> bars, IReadOnlyList<FundingRatePoint> points)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(points);

        var values = new decimal?[bars.Count];
        if (bars.Count == 0 || points.Count == 0)
        {
            return values;
        }

        var cursor = 0;
        for (var barIndex = 0; barIndex < bars.Count; barIndex++)
        {
            var open = bars[barIndex].Time;
            while (cursor < points.Count && points[cursor].Time <= open)
            {
                cursor++;
            }

            // The last event at or before the open governs the bar; none is
            // the gap (the honest absence the pane breaks its line on).
            values[barIndex] = cursor > 0 ? points[cursor - 1].Rate : null;
        }

        return values;
    }
}
