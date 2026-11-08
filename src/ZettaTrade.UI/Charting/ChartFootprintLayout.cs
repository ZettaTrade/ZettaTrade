namespace ZettaTrade.UI.Charting;

using System;
using System.Collections.Generic;
using Avalonia;
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;

/// <summary>
/// One fill marker resolved against the chart's visible window (T-122):
/// the bar slot the fill's own candle rode (matched by open-time
/// CONTAINMENT — a fill time inside the candle's interval anchors at that
/// candle), the executed price the marker anchors at, and the fill's side.
/// The draw maps the slot/price pair through the SAME bar-slot and price
/// mapping the candles use — placement is exact by construction, never an
/// approximation.
/// </summary>
/// <param name="Slot">The visible bars' index of the fill's candle.</param>
/// <param name="Price">The executed price the marker anchors at.</param>
/// <param name="Side">The fill's side (its color).</param>
public readonly record struct FillMarkerHit(int Slot, decimal Price, OrderSide Side);

/// <summary>
/// Pure footprint-placement math for the Skia candlestick chart (T-122):
/// the price levels the position entry line and the working-order lines
/// anchor at, and the fill markers resolved against the visible slice.
/// Every mapping rides the shared price scale and bar layout, so an
/// overlay can never re-scale the axis or land next to its own candle —
/// the same anchoring discipline the signal markers keep (T-112).
/// A level that left the plotted range resolves to NO line (an honest
/// absence): a footprint line may not repaint outside the plot, over the
/// axis gutters or the canvas edge. Unit tests pin the math without
/// pixels (the chart control itself is UIA-invisible).
/// </summary>
public static class ChartFootprintLayout
{
    #region Price Levels

    /// <summary>
    /// The y position of one price level inside the plot, through the
    /// candles' own mapping; null when the level left the plotted price
    /// range — the window shows no boundary for it, so nothing draws
    /// (an absence, never a clamped or extrapolated line).
    /// </summary>
    public static double? LevelY(
        Rect plotRect,
        decimal price,
        decimal minPrice,
        decimal priceRange)
    {
        if (price < minPrice || price > minPrice + priceRange)
        {
            return null;
        }

        return ChartPriceScale.PriceToY(plotRect, price, minPrice, priceRange);
    }

    /// <summary>
    /// The y position the floating PnL tag snaps to (the latest observed
    /// market price, mapped through the shared price scale), CLAMPED into
    /// the plot's headroom band: a fast move that left the padded candle
    /// spread pins the tag at the visible window's edge instead of letting
    /// it ride into the gutters — a floating tag stays visible.
    /// </summary>
    public static double TagY(
        Rect plotRect,
        decimal snappedPrice,
        decimal minPrice,
        decimal priceRange)
    {
        var mapped = ChartPriceScale.PriceToY(plotRect, snappedPrice, minPrice, priceRange);

        var bandTop = plotRect.Top + ChartPriceScale.HeadroomPx;
        var bandBottom = plotRect.Bottom - ChartPriceScale.HeadroomPx;

        return Math.Clamp(mapped, bandTop, bandBottom);
    }

    #endregion

    #region Fill Marker Resolution

    /// <summary>
    /// Resolve the fill markers against the visible window: every marker
    /// whose fill time falls INSIDE a displayed candle's own interval
    /// ([open, next open); the last bar's span borrows the previous bar's,
    /// the same convention the time-axis mapping keeps) becomes a slot hit
    /// at the fill's own price. A fill before the window's first open or
    /// behind the last displayed interval (its candle is forming, not in
    /// the closed series) resolves to nothing — an absence, never a
    /// neighbor's slot. Two fills inside one candle produce two hits; the
    /// draw stacks the shapes.
    /// </summary>
    /// <param name="fills">The published fill markers, ascending by their own times.</param>
    /// <param name="visibleBars">The visible bars slice, ascending open times.</param>
    /// <returns>The anchored hits, in marker order.</returns>
    public static IReadOnlyList<FillMarkerHit> Resolve(
        IReadOnlyList<FillMarker> fills,
        IReadOnlyList<Bar> visibleBars)
    {
        if (fills.Count == 0 || visibleBars.Count == 0)
        {
            return [];
        }

        var hits = new List<FillMarkerHit>(fills.Count);
        foreach (var fill in fills)
        {
            if (fill.Time < visibleBars[0].Time)
            {
                continue;
            }

            var slot = SlotForTime(visibleBars, fill.Time);
            if (slot < 0 || !InsideCandleInterval(visibleBars, slot, fill.Time))
            {
                // Behind the last displayed candle's interval: the fill's
                // own candle is forming and not part of the closed series.
                continue;
            }

            hits.Add(new FillMarkerHit(slot, fill.Price, fill.Side));
        }

        return hits;
    }

    /// <summary>
    /// The visible slot whose open time is the largest one not after the
    /// given time; -1 for a time before the first open (the caller's
    /// earlier guard makes this unreachable, kept for symmetry with the
    /// axis mapping's own no-wrapping contract).
    /// </summary>
    private static int SlotForTime(IReadOnlyList<Bar> visibleBars, DateTimeOffset time)
    {
        var low = 0;
        var high = visibleBars.Count - 1;

        while (low < high)
        {
            var mid = low + ((high - low + 1) / 2);
            if (visibleBars[mid].Time.Ticks <= time.Ticks)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return visibleBars[low].Time.Ticks <= time.Ticks ? low : -1;
    }

    /// <summary>
    /// Whether the time stands inside the candle's own interval: [open,
    /// next open) for every slot but the last, whose end borrows the
    /// previous bar's span (the time-axis mapping's convention). A
    /// single-bar window cannot disprove any later time — the one
    /// interval it owns is all it can honestly anchor.
    /// </summary>
    private static bool InsideCandleInterval(
        IReadOnlyList<Bar> visibleBars,
        int slot,
        DateTimeOffset time)
    {
        if (slot < visibleBars.Count - 1)
        {
            return time.Ticks < visibleBars[slot + 1].Time.Ticks;
        }

        var spanTicks = slot > 0
            ? visibleBars[slot].Time.Ticks - visibleBars[slot - 1].Time.Ticks
            : 0;

        return spanTicks <= 0 ||
               time.Ticks < visibleBars[slot].Time.Ticks + spanTicks;
    }

    #endregion
}
