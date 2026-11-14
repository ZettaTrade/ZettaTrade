namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using ZettaTrade.Core.Entities;

/// <summary>
/// The pure MAE/MFE math of the trade-statistics deepening (T-251): the
/// maximum adverse and favorable excursion of one trade, read from the
/// cached candles that cover the trade's window. For a long, the adverse
/// excursion is the deepest dip of the bar lows below the entry and the
/// favorable one the highest reach of the bar highs above it; a short
/// mirrors both around the entry. Both excursions floor at zero (a trade
/// the price never moved against simply used none of its risk), and a
/// window with no candles at all — or an entry price that is not positive
/// — derives nothing: the honest absence, never a fabricated excursion.
/// <para>
/// The bar window convention: bars whose open time lies in
/// [<c>from</c>, <c>to</c>) — the candles the trade's window opens onto,
/// the exit's own bar included up to its close. The bar already running at
/// the entry moment is excluded on purpose: its early action predates the
/// position, and a full-inclusion rule would fabricate pre-entry
/// excursions.
/// </para>
/// </summary>
public static class TradeExcursionMath
{
    /// <summary>
    /// Derive one trade's excursions from the bars of its window:
    /// <c>(Mae, Mfe)</c> in price units, or null when the window holds no
    /// bars or the entry price is not positive.
    /// </summary>
    /// <param name="entryPrice">The trade's weighted-average entry price (positive).</param>
    /// <param name="isLong">Whether the closed position was long.</param>
    /// <param name="bars">The trade's window bars (any order; the excursion reads every bar).</param>
    public static (decimal Mae, decimal Mfe)? Derive(
        decimal entryPrice,
        bool isLong,
        IReadOnlyList<Bar> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);

        if (entryPrice <= 0m || bars.Count == 0)
        {
            return null;
        }

        decimal mae = 0m;
        decimal mfe = 0m;
        foreach (var bar in bars)
        {
            // The excursion against the trade: for a long the price going
            // BELOW the entry hurts, for a short the price going ABOVE it
            // does. The best case mirrors: the long's reward is a rise
            // above the entry, the short's a fall below it.
            var adverse = isLong ? entryPrice - bar.Low : bar.High - entryPrice;
            var favorable = isLong ? bar.High - entryPrice : entryPrice - bar.Low;
            if (adverse > mae)
            {
                mae = adverse;
            }

            if (favorable > mfe)
            {
                mfe = favorable;
            }
        }

        return (mae, mfe);
    }

    /// <summary>
    /// Slice one ascending bar series down to the window
    /// [<c>from</c>, <c>to</c>): the bars whose open time lies inside, by
    /// binary search — a six-figure series never pays a linear scan per
    /// trade. Bars before <c>from</c> and at-or-after <c>to</c> stay out.
    /// </summary>
    /// <param name="ascendingBars">The series sorted by open time, oldest first.</param>
    /// <param name="from">The window's inclusive start.</param>
    /// <param name="to">The window's exclusive end.</param>
    public static IReadOnlyList<Bar> SliceWindow(
        IReadOnlyList<Bar> ascendingBars,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        ArgumentNullException.ThrowIfNull(ascendingBars);

        if (to <= from)
        {
            return [];
        }

        // The first bar at-or-after `from`: the binary search finds SOME
        // match or the insertion point — both are the leftmost index whose
        // time is not below `from` once equal times walk forward.
        var low = 0;
        var high = ascendingBars.Count;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (ascendingBars[mid].Time < from)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        var start = low;

        // The first bar at-or-after `to`: everything from there on is out.
        low = start;
        high = ascendingBars.Count;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (ascendingBars[mid].Time < to)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        var end = low;
        var slice = new Bar[end - start];
        for (var i = start; i < end; i++)
        {
            slice[i - start] = ascendingBars[i];
        }

        return slice;
    }
}
