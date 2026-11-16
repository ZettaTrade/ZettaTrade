namespace ZettaTrade.UI.Charting;

using System;
using System.Collections.Generic;

/// <summary>
/// The right-margin label stack (T-248): the profile annotations' and the
/// naked POCs' labels all want the same right-margin column, and two labels
/// at nearly the same price would overprint. The stack walks the labels in
/// price order (the topmost first) and pushes each label down just enough
/// to keep a minimum pixel gap from the one above, then clamps the whole
/// column into the plot — the top label never leaves the plot's top edge.
/// Pure: the goldens pin the stacking without a canvas.
/// <para>
/// The discipline: the PRIORITY order survives (the caller lists the
/// labels most-worth-keeping first — a pushed-aside label never swaps
/// above the one that pushed it), the gaps are the honest minimum (labels
/// sit exactly one gap apart at the tightest, never stretched apart) and a
/// label that cannot fit inside the plot at all still keeps its resolved
/// position (the caller clips the draw, the stack never reorders).
/// </para>
/// </summary>
public static class ChartLabelStack
{
    /// <summary>
    /// Resolve the labels' vertical centers.
    /// </summary>
    /// <param name="desiredYs">The labels' desired y positions, in priority
    /// order (the first entry is the most-worth-keeping).</param>
    /// <param name="minGapPx">The minimum pixel gap between neighbors.</param>
    /// <param name="topPx">The plot's top edge (the column's ceiling).</param>
    /// <param name="bottomPx">The plot's bottom edge (the column's floor).</param>
    /// <returns>The resolved centers, in the SAME order as the input.</returns>
    public static IReadOnlyList<double> Resolve(
        IReadOnlyList<double> desiredYs,
        double minGapPx,
        double topPx,
        double bottomPx)
    {
        ArgumentNullException.ThrowIfNull(desiredYs);
        if (desiredYs.Count == 0)
        {
            return [];
        }

        if (bottomPx < topPx)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bottomPx),
                bottomPx,
                "The stack's floor must not sit above its ceiling.");
        }

        // Sort by the desired y (the smallest y = the highest on the plot)
        // while remembering each label's input position, so the resolved
        // list returns in the caller's order.
        var order = new int[desiredYs.Count];
        for (var i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (a, b) => desiredYs[a].CompareTo(desiredYs[b]));

        var resolved = new double[desiredYs.Count];
        var previous = double.NegativeInfinity;
        foreach (var index in order)
        {
            var y = Math.Max(desiredYs[index], previous + minGapPx);
            resolved[index] = y;
            previous = y;
        }

        // The pushed shape clamps into the plot as ONE rigid column: a
        // uniform translation keeps every gap and every push exactly as the
        // first pass resolved them. A column taller than the plot anchors
        // at the ceiling and lets the tail clip (the caller skips the
        // off-plot labels — the stack never reorders).
        var overflowBottom = resolved[order[^1]] - bottomPx;
        if (overflowBottom > 0)
        {
            for (var k = 0; k < order.Length; k++)
            {
                resolved[order[k]] -= overflowBottom;
            }

            var overflowTop = topPx - resolved[order[0]];
            if (overflowTop > 0)
            {
                for (var k = 0; k < order.Length; k++)
                {
                    resolved[order[k]] += overflowTop;
                }
            }
        }

        return resolved;
    }
}
