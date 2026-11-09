namespace ZettaTrade.UI.Charting;

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;

/// <summary>
/// Pure squarified-treemap layout for the heatmap panel (T-121): every
/// ranked tile gets a rect inside the plot whose area is proportional to
/// its weight, laid strip-wise along the remaining rect's shorter side
/// with the classic greedy rule - the next tile joins the strip only while
/// adding it does not worsen the strip's worst aspect ratio. The input
/// order IS the rank order (largest first): the layout never re-sorts
/// behind the feed's back, so the panel's own ranking decides where a
/// pair lands. A tile with a null or non-positive weight owns no area
/// (nothing is invented for unpriced pairs) and is skipped; the placed
/// tiles keep the input order.
/// <para>
/// Layout is a pure function of (tiles, plot): the same input returns the
/// same output on every call, so a fast ticker storm re-plans
/// deterministically - the rapid-update facts pin exactly that.
/// </para>
/// </summary>
public static class HeatmapTileLayout
{
    /// <summary>
    /// Plan the treemap. A zero-area plot, an empty tile list or a
    /// degenerate all-unpriced set resolve to an empty page (the panel's
    /// empty state), never a fabricated rect.
    /// </summary>
    /// <param name="tiles">The ranked tiles (largest weight first).</param>
    /// <param name="plotRect">The plot the treemap fills (pixels).</param>
    /// <returns>The placed tile rects, in input (rank) order.</returns>
    public static IReadOnlyList<HeatmapTileRect> Layout(
        IReadOnlyList<HeatmapTile> tiles,
        Rect plotRect)
    {
        if (plotRect.Width <= 0 || plotRect.Height <= 0)
        {
            return [];
        }

        var mappable = tiles
            .Where(tile => tile.Weight is { } weight && weight > 0m)
            .ToArray();
        if (mappable.Length == 0)
        {
            return [];
        }

        decimal totalWeight = 0m;
        foreach (var tile in mappable)
        {
            totalWeight += tile.Weight!.Value;
        }

        if (totalWeight <= 0m)
        {
            return [];
        }

        // Weights to pixel areas: the tile set fills the plot exactly.
        var pixelsPerWeight = (double)(plotRect.Width * plotRect.Height) / (double)totalWeight;
        var areas = new double[mappable.Length];
        for (var index = 0; index < mappable.Length; index++)
        {
            areas[index] = (double)mappable[index].Weight!.Value * pixelsPerWeight;
        }

        var placed = new List<HeatmapTileRect>(mappable.Length);
        Squarify(mappable, areas, plotRect, placed);
        return placed;
    }

    /// <summary>
    /// The greedy squarified fill: strips form along the remaining rect's
    /// shorter side while adding the next tile keeps (or improves) the
    /// strip's worst aspect ratio; the last strip always absorbs the
    /// remaining rect in full, so no hairline crumb of unclaimed plot
    /// survives the float arithmetic. The tile list is the mappable prefix
    /// state the caller already filtered: every straight index into it
    /// matches <paramref name="areas"/> one to one (the raw input's
    /// skipped tiles never shift the slots).
    /// </summary>
    private static void Squarify(
        IReadOnlyList<HeatmapTile> tiles,
        double[] areas,
        Rect remaining,
        List<HeatmapTileRect> placed)
    {
        var index = 0;
        while (index < areas.Length)
        {
            var side = Math.Min(remaining.Width, remaining.Height);

            // Grow the strip while the worst aspect ratio does not worsen.
            var strip = new List<int> { index };
            var stripArea = areas[index];
            var worst = WorstAspect(strip, areas, stripArea, side);
            var next = index + 1;
            while (next < areas.Length)
            {
                var candidateArea = stripArea + areas[next];
                strip.Add(next);
                var candidateWorst = WorstAspect(strip, areas, candidateArea, side);
                if (candidateWorst > worst)
                {
                    strip.RemoveAt(strip.Count - 1);
                    break;
                }

                stripArea = candidateArea;
                worst = candidateWorst;
                next++;
            }

            var isLastStrip = next >= areas.Length;
            remaining = PlaceStrip(tiles, strip, areas, stripArea, remaining, isLastStrip, placed);
            index = next;
        }
    }

    /// <summary>
    /// The worst aspect ratio a strip produces against the remaining side:
    /// every tile crosses the strip with the strip's own thickness, and its
    /// length is its area over that thickness - the nearer a tile sits to a
    /// square, the closer its ratio stays to one.
    /// </summary>
    private static double WorstAspect(
        IReadOnlyList<int> strip,
        double[] areas,
        double stripArea,
        double side)
    {
        if (side <= 0 || stripArea <= 0)
        {
            return double.PositiveInfinity;
        }

        var thickness = stripArea / side;
        var worst = 0d;
        foreach (var slot in strip)
        {
            var length = areas[slot] / thickness;
            var ratio = Math.Max(thickness / length, length / thickness);
            worst = Math.Max(worst, ratio);
        }

        return worst;
    }

    /// <summary>
    /// Lay one strip and return the rect that remains after it: a wide
    /// remaining lays a column on its left edge (tiles stack top-down),
    /// a tall remaining lays a row across its top edge (tiles run
    /// left-right); every tile's run along the strip is proportional to
    /// its own area, and the LAST strip's thickness eats the whole
    /// remaining rect so the plan leaves no crumb. Direction and slice
    /// totals come straight from the remaining rect, so the strips tile
    /// the plot edge-to-edge by construction.
    /// </summary>
    private static Rect PlaceStrip(
        IReadOnlyList<HeatmapTile> tiles,
        IReadOnlyList<int> strip,
        double[] areas,
        double stripArea,
        Rect remaining,
        bool isLastStrip,
        List<HeatmapTileRect> placed)
    {
        var vertical = remaining.Width >= remaining.Height;
        var stripLength = vertical ? remaining.Height : remaining.Width;
        var thickness = isLastStrip || stripLength <= 0 || stripArea <= 0
            ? (vertical ? remaining.Width : remaining.Height)
            : stripArea / stripLength;

        var offset = 0d;
        foreach (var slot in strip)
        {
            var run = stripArea <= 0 ? 0 : areas[slot] / stripArea * stripLength;
            var bounds = vertical
                ? new Rect(remaining.X, remaining.Y + offset, thickness, run)
                : new Rect(remaining.X + offset, remaining.Y, run, thickness);
            var tile = tiles[slot];
            placed.Add(new HeatmapTileRect(tile.SymbolText, tile.Change24hPercent, bounds));
            offset += run;
        }

        if (isLastStrip)
        {
            // The plot is consumed: the sentinel is a zero rect (Avalonia's
            // Rect carries no Empty member) - the squarify loop exits right
            // after and never reads this value again.
            return default;
        }

        return vertical
            ? new Rect(
                remaining.X + thickness,
                remaining.Y,
                Math.Max(0d, remaining.Width - thickness),
                remaining.Height)
            : new Rect(
                remaining.X,
                remaining.Y + thickness,
                remaining.Width,
                Math.Max(0d, remaining.Height - thickness));
    }
}
