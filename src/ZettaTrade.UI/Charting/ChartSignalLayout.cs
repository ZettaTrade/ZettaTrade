namespace ZettaTrade.UI.Charting;

using System.Collections.Generic;
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;

/// <summary>
/// One strategy-signal marker resolved against the chart's visible window
/// (T-112): the bar slot the marker's own candle rode (matched by open
/// time), that candle's close level the marker anchors at, and the stance's
/// visual role. The draw maps the slot/close pair through the SAME bar-slot
/// and price mapping the candles use — placement is exact by construction,
/// never an approximation.
/// </summary>
/// <param name="Slot">The visible bars' index of the marker's candle.</param>
/// <param name="Close">The candle's close price the marker anchors at.</param>
/// <param name="Role">The stance's shape (up-triangle for a long, down-triangle for a flat).</param>
public readonly record struct SignalMarkerHit(int Slot, decimal Close, SignalMarkerRole Role);

/// <summary>
/// Pure signal-marker placement math for the Skia candlestick chart
/// (T-112): the markers the ViewModel published are resolved against the
/// visible bar slice, so every hit names the slot its OWN candle rode. The
/// walk is a two-pointer merge (both lists ascend by open time one to one
/// with the compose pass), which pins the acceptance invariant — a marker
/// can never sit next to its signal's candle — and unit tests pin the
/// resolution without pixels (the chart control itself is UIA-invisible).
/// </summary>
public static class ChartSignalLayout
{
    /// <summary>
    /// Resolve the markers against the visible window: every marker whose
    /// OWN candle (same open time) rides the slice becomes a slot hit. A
    /// marker whose candle is not in the slice (a bar the window never
    /// shows, a series the registry outlived) resolves to nothing — an
    /// absence, never a neighbor's slot. Two markers standing on one bar
    /// produce two hits on the same slot; the draw stacks the shapes.
    /// </summary>
    /// <param name="markers">The published markers, ascending by their candles' open times.</param>
    /// <param name="visibleBars">The visible bars slice, ascending open times.</param>
    /// <returns>The anchored hits, in marker order.</returns>
    public static IReadOnlyList<SignalMarkerHit> Resolve(
        IReadOnlyList<SignalMarker> markers,
        IReadOnlyList<Bar> visibleBars)
    {
        if (markers.Count == 0 || visibleBars.Count == 0)
        {
            return [];
        }

        var hits = new List<SignalMarkerHit>(markers.Count);
        var cursor = 0;
        foreach (var marker in markers)
        {
            while (cursor < visibleBars.Count && visibleBars[cursor].Time < marker.Candle.Time)
            {
                cursor++;
            }

            if (cursor >= visibleBars.Count)
            {
                break;
            }

            if (visibleBars[cursor].Time == marker.Candle.Time)
            {
                hits.Add(new SignalMarkerHit(cursor, marker.Candle.Close, marker.Role));
            }
        }

        return hits;
    }
}
