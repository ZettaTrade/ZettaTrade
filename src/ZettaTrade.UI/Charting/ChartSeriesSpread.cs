namespace ZettaTrade.UI.Charting;

using System.Collections.Generic;
using ZettaTrade.Core.Entities;

/// <summary>
/// The price spread the drawn series feeds the chart's padded price scale
/// with (T-089): the scale always covers exactly the geometry the current
/// renderer actually paints, so nothing a frame draws can clip the plot.
/// Candlesticks, OHLC bars and Heikin-Ashi spread over their bars' full
/// low–high range (the wicks are drawn geometry); the line and area
/// variants only touch closes, so they spread over them (the terminal
/// close-line scale — the wicks are not part of the drawn geometry). Pure
/// unit-testable math, like <see cref="ChartBarLayout"/> and
/// <see cref="ChartPriceScale"/>.
/// </summary>
public static class ChartSeriesSpread
{
    /// <summary>
    /// The drawn window's price spread: the low–high range of every bar, or
    /// the close-only range for the close-line variants (<paramref name="closesOnly"/>).
    /// Assumes at least one bar (the draw pass guards the empty window
    /// before consuming the spread).
    /// </summary>
    /// <param name="bars">The drawn window's bars (the Heikin-Ashi slice when
    /// that type draws).</param>
    /// <param name="closesOnly">Whether the renderer touches only closes
    /// (the line and area variants).</param>
    /// <returns>The drawn minimum and maximum.</returns>
    public static (decimal Low, decimal High) Of(IReadOnlyList<Bar> bars, bool closesOnly)
    {
        var lowest = closesOnly ? bars[0].Close : bars[0].Low;
        var highest = closesOnly ? bars[0].Close : bars[0].High;

        for (var i = 1; i < bars.Count; i++)
        {
            var floor = closesOnly ? bars[i].Close : bars[i].Low;
            var ceiling = closesOnly ? bars[i].Close : bars[i].High;

            if (floor < lowest)
            {
                lowest = floor;
            }

            if (ceiling > highest)
            {
                highest = ceiling;
            }
        }

        return (lowest, highest);
    }
}
