namespace ZettaTrade.UI.Charting;

using System;
using System.Collections.Generic;
using ZettaTrade.Core.Entities;

/// <summary>
/// Pure bar-slot layout math for the Skia candlestick chart: the bar pitch,
/// the right-anchored slot positions and the time-to-slot mapping. The draw
/// pass consumes these results unchanged, and unit tests pin them without
/// pixels (the chart control itself is UIA-invisible).
/// </summary>
public static class ChartBarLayout
{
    /// <summary>
    /// The bar pitch — the full slot width one candle occupies: the plot
    /// width divided by the visible bar count, never above the given
    /// maximum. The cap serves the case when FEWER bars than the visible
    /// window are loaded (a fresh listing on a coarse time frame): the old
    /// pitch-without-cap stretched those bars across the whole plot, so one
    /// bar painted a full-width wall. Terminals keep the candle width
    /// normal instead and anchor the recent end to the plot's right edge;
    /// the empty space grows left.
    /// </summary>
    public static double BarPitch(double plotWidth, int barCount, double maxPitch)
    {
        if ((plotWidth <= 0d) || (barCount < 1))
        {
            return maxPitch;
        }

        return Math.Min(plotWidth / barCount, maxPitch);
    }

    /// <summary>
    /// The left boundary x of one bar slot when the slots hang off the
    /// plot's right edge: the last bar's right boundary sits exactly on the
    /// plot edge (the terminal right-offset fact). When the pitch equals
    /// <c>plotWidth / barCount</c> (uncapped), this equals the old
    /// left-anchored layout — the bars fill the plot completely — so the
    /// capped and uncapped states share one formula.
    /// </summary>
    public static double BarSlotLeftX(double plotRight, double pitch, int barCount, int index)
    {
        return plotRight - ((barCount - index) * pitch);
    }

    /// <summary>The horizontal center of one bar slot; the candles ride it.</summary>
    public static double BarCenterX(double plotRight, double pitch, int barCount, int index)
    {
        return BarSlotLeftX(plotRight, pitch, barCount, index) + (pitch / 2d);
    }

    /// <summary>
    /// The slot index whose center band contains the given x — the selection
    /// math of the hover crosshair's OHLC readout. A bar owns the half-pitch
    /// band around its center (Nearest-center: the boundary between two
    /// adjacent centers ties UP to the further index, a deterministic
    /// convention); an x outside the slot space — the left empty zone of a
    /// capped window, or the plot's right half-open band end — is
    /// <c>-1</c>: no bar is hovered, never a wrapped or clamped substitute.
    /// A starved or degenerate input (<paramref name="barCount"/> below one,
    /// non-positive pitch) is <c>-1</c> too.
    /// </summary>
    public static int SlotIndexForX(double plotRight, double pitch, int barCount, double x)
    {
        if ((barCount < 1) || (pitch <= 0d))
        {
            return -1;
        }

        var firstCenter = BarCenterX(plotRight, pitch, barCount, 0);
        var raw = (int)Math.Floor(((x - firstCenter) / pitch) + 0.5d);

        return (raw < 0) || (raw >= barCount) ? -1 : raw;
    }

    /// <summary>
    /// Maps a time tick onto the bar-slot axis: the tick belongs to the bar
    /// whose own open-time interval contains it (the terminal way — grid
    /// lines ride the bars, never float between them), and inside that slot
    /// the position advances linearly through the bar's span (one slot wide
    /// when the bar opens REGULARLY; the last slot uses the previous bar's
    /// span). <paramref name="openTimes"/> is the visible window's ascending
    /// open times. A time before the first open maps into the first slot,
    /// a time after the last open maps out of the plot's right edge — both
    /// extremes of the visible axis, never an exception.
    /// </summary>
    public static double BarXForTime(
        double plotRight,
        double pitch,
        IReadOnlyList<DateTimeOffset> openTimes,
        DateTimeOffset time)
    {
        if (openTimes.Count < 1)
        {
            return plotRight - pitch;
        }

        var slot = SlotIndex(openTimes, time);

        return BarSlotLeftX(plotRight, pitch, openTimes.Count, slot)
            + (SlotFraction(openTimes, slot, time) * pitch);
    }

    /// <summary>
    /// The visible window's open times as an array, for the slot mapping.
    /// Snapshots carry only bars; the times are what the slot axis speaks.
    /// </summary>
    public static IReadOnlyList<DateTimeOffset> CollectOpenTimes(IReadOnlyList<Bar> bars)
    {
        var times = new DateTimeOffset[bars.Count];
        for (var i = 0; i < times.Length; i++)
        {
            times[i] = bars[i].Time;
        }

        return times;
    }

    /// <summary>
    /// The largest slot index whose open time is not after the given time
    /// (binary search over the ascending opens; inputs before the first
    /// open own the first slot — no default substitution, just the axis
    /// edges).
    /// </summary>
    private static int SlotIndex(IReadOnlyList<DateTimeOffset> openTimes, DateTimeOffset time)
    {
        var low = 0;
        var high = openTimes.Count - 1;

        while (low < high)
        {
            var mid = low + ((high - low + 1) / 2);
            if (openTimes[mid].Ticks <= time.Ticks)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return low;
    }

    /// <summary>
    /// The time's position inside its slot as a 0..1 fraction of the bar's
    /// own span (clamped — a slot never overflows into the next one). The
    /// last bar has no following open, so its span borrows the previous
    /// bar's; a one-bar window keeps ticks at the slot's left boundary,
    /// which is all the window can honestly anchor.
    /// </summary>
    private static double SlotFraction(
        IReadOnlyList<DateTimeOffset> openTimes,
        int slot,
        DateTimeOffset time)
    {
        var openTicks = openTimes[slot].Ticks;
        var barTicks = slot < (openTimes.Count - 1)
            ? openTimes[slot + 1].Ticks - openTicks
            : slot > 0
                ? openTicks - openTimes[slot - 1].Ticks
                : 0;

        if (barTicks <= 0)
        {
            return 0d;
        }

        var offset = time.Ticks - openTicks;
        if (offset <= 0)
        {
            return 0d;
        }

        var clamped = Math.Min(offset, barTicks);

        return (double)clamped / barTicks;
    }
}
