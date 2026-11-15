namespace ZettaTrade.App.Services;

using ZettaTrade.Core.Entities;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// The OI pane's one aligner (T-241): a pure map from the chart's bar
/// series and the venue's open-interest history onto the per-bar pane
/// values the renderer draws. The per-bar value is the LAST snapshot at or
/// before the bar's open time — the interest the venue had settled when
/// the bar opened — and a bar before the history's first snapshot reads
/// null, the honest gap the polyline breaks on (never an interpolated or
/// zero-filled stretch). Both inputs are read as ascending series (the
/// app's bar and OI contracts), so one shared linear walk aligns the whole
/// window.
/// </summary>
public static class OISeriesAlignment
{
    /// <summary>
    /// Align the open-interest history onto the bars: one value per bar
    /// slot, the governing snapshot's size as the wire serves it or null
    /// before the first snapshot.
    /// </summary>
    /// <param name="bars">The chart's bar series, ascending by open time.</param>
    /// <param name="points">The open-interest history, ascending by snapshot time.</param>
    /// <returns>One value per bar slot, index-aligned with the bars.</returns>
    public static IReadOnlyList<decimal?> Align(IReadOnlyList<Bar> bars, IReadOnlyList<OpenInterestPoint> points)
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

            // The last snapshot at or before the open governs the bar; none
            // is the gap (the honest absence the pane breaks its line on).
            values[barIndex] = cursor > 0 ? points[cursor - 1].Size : null;
        }

        return values;
    }
}
