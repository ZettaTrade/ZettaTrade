namespace ZettaTrade.UI.Charting;

using System;
using System.Collections.Generic;
using Avalonia;
using ZettaTrade.Charting;

/// <summary>
/// The drawings layer's pointer mapping (T-091), pure: the inverse of the
/// draw pass's time→x + price→y chain, and the grid snapping a mapped
/// anchor lands on. The control feeds this type the frame's cached
/// constants (the painted geometry, cross-copied per frame); tests pin
/// the inverse and the snap against the SAME pure helpers the draw pass
/// uses (<see cref="ChartBarLayout"/>, <see cref="ChartPriceScale"/>,
/// <see cref="ChartPriceTransform"/>) — no pixels, no chart state.
/// </summary>
public static class ChartPointerMapping
{
    /// <summary>
    /// Maps a plot point back to the (time, price) anchor it stands for:
    /// the inverse of <see cref="ChartBarLayout.BarXForTime"/> + the
    /// shared price scale. The fractional slot unwinds linearly off the
    /// plot's right edge; the fraction advances through the slot's own
    /// bar span exactly like the forward <c>SlotFraction</c> (the last
    /// slot borrows the previous bar's span); a reading before the first
    /// open clamps to the first open (the forward map pulls earlier times
    /// into the first slot the same way). The price unwinds through the
    /// axis transform — the hover pill's own honest-extrapolation rule.
    /// Null whenever nothing valid has painted (an empty window, a
    /// degenerate pitch).
    /// </summary>
    public static ChartDrawingAnchor? MapPointToAnchor(
        Rect plotRect,
        double barPitch,
        IReadOnlyList<DateTimeOffset> openTimes,
        decimal minAxis,
        decimal axisRange,
        PriceScaleMode scaleMode,
        decimal axisAnchor,
        decimal axisFloor,
        double pixelX,
        double pixelY)
    {
        if (openTimes.Count < 1 || barPitch <= 0d)
        {
            return null;
        }

        // BarXForTime maps slot s + fraction f onto
        // plotRight - (count - s - f) * pitch — the press unwinds into
        // fractional slot space linearly.
        var slotFloat = openTimes.Count - ((plotRect.Right - pixelX) / barPitch);
        int slot;
        double fraction;
        if (slotFloat < 0d)
        {
            slot = 0;
            fraction = 0d;
        }
        else
        {
            slot = (int)Math.Min(Math.Floor(slotFloat), openTimes.Count - 1);
            fraction = Math.Clamp(slotFloat - slot, 0d, 1d);
        }

        // The slot's own span (the forward SlotFraction's boundary rule
        // mirrored: the last bar borrows its predecessor's span; a
        // one-bar window keeps the reading at the open).
        var spanTicks = slot < (openTimes.Count - 1)
            ? openTimes[slot + 1].Ticks - openTimes[slot].Ticks
            : slot > 0
                ? openTimes[slot].Ticks - openTimes[slot - 1].Ticks
                : 0L;

        var time = new DateTimeOffset(
            openTimes[slot].Ticks + (long)(fraction * spanTicks),
            openTimes[slot].Offset);

        var axisValue = ChartPriceScale.YToPrice(pixelY, plotRect, minAxis, axisRange);
        var price = ChartPriceTransform.ToPrice(axisValue, scaleMode, axisAnchor, axisFloor);

        return new ChartDrawingAnchor(time, price);
    }

    /// <summary>
    /// Snaps one mapped anchor onto the chart's own grid: the time onto
    /// the nearest bar-open (the opens ride the slot left edges, so the
    /// distance to open i is |slotFloat − i| · pitch — an O(1) nearest),
    /// the price onto the nearest axis rung (the rung unwound back to a
    /// raw price through the axis transform). Nothing within the pixel
    /// threshold passes through untouched — a sloppy drag stays free.
    /// </summary>
    public static ChartDrawingAnchor SnapToGrid(
        Rect plotRect,
        double barPitch,
        IReadOnlyList<DateTimeOffset> openTimes,
        IReadOnlyList<decimal> axisRungs,
        decimal minAxis,
        decimal axisRange,
        PriceScaleMode scaleMode,
        decimal axisAnchor,
        decimal axisFloor,
        ChartDrawingAnchor anchor,
        double thresholdPx)
    {
        if (openTimes.Count < 1)
        {
            return anchor;
        }

        var snappedTime = anchor.Time;
        if (barPitch > 0d)
        {
            var x = ChartBarLayout.BarXForTime(plotRect.Right, barPitch, openTimes, anchor.Time);
            var slotFloat = openTimes.Count - ((plotRect.Right - x) / barPitch);
            var nearestSlot = Math.Clamp((int)Math.Floor(slotFloat + 0.5d), 0, openTimes.Count - 1);
            if (Math.Abs(slotFloat - nearestSlot) * barPitch <= thresholdPx)
            {
                snappedTime = openTimes[nearestSlot];
            }
        }

        var snappedPrice = anchor.Price;
        var axisValue = ChartPriceTransform.ToAxisValue(anchor.Price, scaleMode, axisAnchor, axisFloor);
        var y = ChartPriceScale.PriceToY(plotRect, axisValue, minAxis, axisRange);
        decimal? nearestRung = null;
        var nearestDistance = double.MaxValue;
        foreach (var rung in axisRungs)
        {
            var distance = Math.Abs(ChartPriceScale.PriceToY(plotRect, rung, minAxis, axisRange) - y);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearestRung = rung;
            }
        }

        if (nearestRung is { } rungAxis && nearestDistance <= thresholdPx)
        {
            snappedPrice = ChartPriceTransform.ToPrice(rungAxis, scaleMode, axisAnchor, axisFloor);
        }

        return new ChartDrawingAnchor(snappedTime, snappedPrice);
    }
}
