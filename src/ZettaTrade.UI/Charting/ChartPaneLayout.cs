namespace ZettaTrade.UI.Charting;

using System.Linq;
using Avalonia;

/// <summary>
/// The oscillator pane geometry (T-199): the pure split of the chart's
/// drawable height between the main pane and the oscillator sub-panes, in
/// declaration order. Each pane's stored height (the splitter's settled
/// value) clamps into the usable band; a stored null rides the default.
/// The fit pass keeps the main pane's floor: when the clamped panes
/// overdraw the space, they shrink proportionally (never below the pane
/// floor — a window too small for even the floors lets the main pane
/// give way rather than producing negative rects).
/// </summary>
public static class ChartPaneLayout
{
    /// <summary>The pane's floor: below it the polyline and the scale labels stop being readable.</summary>
    public const double PaneMinHeightPx = 72;

    /// <summary>The pane's ceiling: a single oscillator never starves the main pane past this.</summary>
    public const double PaneMaxHeightPx = 480;

    /// <summary>The height a pane without a stored settled value rides.</summary>
    public const double PaneDefaultHeightPx = 140;

    /// <summary>The main pane's floor the pane stack must leave (the candles' own readable minimum).</summary>
    public const double MainMinHeightPx = 160;

    /// <summary>
    /// Resolve the requested heights into clamped, fitted heights in the
    /// same order: per pane <c>clamp(stored ?? default)</c>, then a
    /// proportional fit when the stack overdraws <c>total - mainFloor</c>
    /// (each pane never below the floor; a total too small for the floors
    /// alone leaves the floor clamps standing — the main pane yields).
    /// </summary>
    public static IReadOnlyList<double> ResolveHeights(
        double totalHeight,
        IReadOnlyList<double?> requestedHeights)
    {
        if (requestedHeights.Count == 0)
        {
            return [];
        }

        var heights = new double[requestedHeights.Count];
        var sum = 0d;
        for (var i = 0; i < requestedHeights.Count; i++)
        {
            heights[i] = Math.Clamp(requestedHeights[i] ?? PaneDefaultHeightPx, PaneMinHeightPx, PaneMaxHeightPx);
            sum += heights[i];
        }

        var budget = Math.Max(0d, totalHeight - MainMinHeightPx);
        if (sum > budget)
        {
            var scale = sum > 0 ? budget / sum : 0d;
            for (var i = 0; i < heights.Length; i++)
            {
                heights[i] = Math.Max(PaneMinHeightPx, heights[i] * scale);
            }
        }

        return heights;
    }

    /// <summary>
    /// Slice the drawable height into the main rect (the first element)
    /// followed by the pane rects top-to-bottom in the same order: the
    /// main pane owns the top, the panes stack directly under it, the
    /// stack rides the whole drawable height (the time axis gutter sits
    /// outside it — the caller passes the pre-gutter height). A pane
    /// rect's height stays exactly the resolved height; a stack taller
    /// than the drawable is the degenerate-window case and simply runs
    /// past it (the canvas clip holds).
    /// </summary>
    public static IReadOnlyList<Rect> Split(double plotWidth, double totalHeight, IReadOnlyList<double> paneHeights)
    {
        var rects = new List<Rect>(paneHeights.Count + 1);
        var stackHeight = paneHeights.Count > 0 ? Math.Min(Math.Max(0d, totalHeight), paneHeights.Sum()) : 0d;
        var mainHeight = Math.Max(0d, totalHeight - stackHeight);

        rects.Add(new Rect(0, 0, plotWidth, mainHeight));
        var top = mainHeight;
        foreach (var height in paneHeights)
        {
            rects.Add(new Rect(0, top, plotWidth, height));
            top += height;
        }

        return rects;
    }
}
