namespace ZettaTrade.UI.Charting;

using System;
using System.Collections.Generic;

/// <summary>
/// Pure vertical scale math for the oscillator sub-panes (T-199): the
/// pane's own value range resolved per redraw from the indicator's pane
/// declaration and its visible slice. A declared fixed bound pair (RSI's
/// 0-100) pins the scale exactly - no padding, the canonical levels sit
/// where the scale says; a declared bound pair that is unusable (a null
/// member, min >= max) degrades to the auto scale loudly, never clipped.
/// The auto scale is the main plot's own padded-spread discipline applied
/// to the pane's visible values. The mapping itself rides the shared
/// <see cref="ChartPriceScale.PriceToY"/> - a pane is a plot with its own
/// scale, not a second mapping.
/// </summary>
public static class ChartPaneScale
{
    /// <summary>
    /// The pane's scale for one redraw: the declared fixed bounds win when
    /// both are declared and ordered; otherwise the padded spread of the
    /// slice's non-null values (an empty or all-warmup slice maps a
    /// neutral 0-1 unit so the pass never divides by zero).
    /// </summary>
    /// <returns>The pane minimum, the pane maximum and their range.</returns>
    public static (decimal Min, decimal Max, decimal Range) Resolve(
        decimal? scaleMin,
        decimal? scaleMax,
        IReadOnlyList<decimal?> sliceValues)
    {
        if (scaleMin is { } min && scaleMax is { } max && min < max)
        {
            return (min, max, max - min);
        }

        decimal lowest = 0;
        decimal highest = 0;
        var seen = false;
        foreach (var value in sliceValues)
        {
            if (value is not { } current)
            {
                continue;
            }

            if (!seen)
            {
                lowest = highest = current;
                seen = true;
                continue;
            }

            if (current < lowest)
            {
                lowest = current;
            }

            if (current > highest)
            {
                highest = current;
            }
        }

        if (!seen)
        {
            return PaddedUnit();
        }

        return ChartPriceScale.PaddedSpread(lowest, highest);
    }

    /// <summary>The neutral unit window an all-warmup slice maps (the pane draws its frame, no line yet).</summary>
    private static (decimal Min, decimal Max, decimal Range) PaddedUnit() => (0m, 1m, 1m);
}
