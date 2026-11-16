namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;

/// <summary>
/// The CVD pane's pure math (T-244): the trade-to-bucket attribution and
/// the aligned cumulative buy-minus-sell series the chart's own lane
/// serves. Static and side-effect free (the one explicit accumulator is the
/// passed dictionary — the lane owns it) so the goldens pin the semantics
/// without a transport, a clock or a scheduler — the same discipline as
/// <see cref="LiquidationCascadeAggregator"/>.
/// <para>
/// The math, exactly: a trade belongs to the bar slot whose window
/// <c>[open, open + barLength)</c> contains its timestamp (the standing
/// bars' own opens, binary-searched — never a grid-floor guess); the
/// aggressor's side sizes the delta (a Buy adds, a Sell subtracts); a
/// NEUTRAL trade (the T-198 wire's own no-aggressor absence) contributes
/// NOTHING to the delta — the volume is real but the side is unknowable,
/// so an only-neutral bar is an honest GAP (null), never a fabricated flat
/// zero. The pane's value is the RUNNING SUM over the counted bars — the
/// accumulator carries through the gaps (a gap bar renders null and
/// contributes nothing, the zero-volume bar's VWAP share discipline);
/// the SESSION-ANCHORED RESET (the shipped semantics, the VWAP anchor's
/// own UTC-day rule via <see cref="VwapSessionAnchor.OpensNewSession"/>)
/// restarts the sum at every session break, so a session's CVD reads its
/// own flow, not the chain of sessions before it.
/// </para>
/// </summary>
public static class CvdAggregation
{
    /// <summary>
    /// The one bar's raw delta pair: the summed aggressor sizes the bar's
    /// trades carried, keyed by their side. Public so the goldens build
    /// buckets directly; the lane never reads the pair outside the
    /// aggregator.
    /// </summary>
    public sealed class Bucket
    {
        /// <summary>The summed buy-side size the bar's trades carried.</summary>
        public decimal Buy { get; private set; }

        /// <summary>The summed sell-side size the bar's trades carried.</summary>
        public decimal Sell { get; private set; }

        /// <summary>Whether at least one SIDED trade landed (the honest counted bar — an only-neutral bar is a gap).</summary>
        public bool HasSidedTrade { get; private set; }

        /// <summary>Attribute one trade to the bucket: a Buy adds, a Sell subtracts, a Neutral leaves the delta standing (the volume is real, the side is not).</summary>
        public void Add(OrderSide side, decimal size)
        {
            switch (side)
            {
                case OrderSide.Buy:
                    Buy += size;
                    HasSidedTrade = true;
                    break;
                case OrderSide.Sell:
                    Sell += size;
                    HasSidedTrade = true;
                    break;
            }
        }
    }

    /// <summary>
    /// Attribute one trade to its bar slot: the slot whose window
    /// <c>[open, open + barLength)</c> contains the timestamp, binary-searched
    /// over the standing bars' own opens. A trade before the window's first
    /// bar or past its last bar's close is OUTSIDE the standing series —
    /// unattributed (returns false), never squeezed into a neighbor. A
    /// non-positive size is dropped (a broken wire row never fabricates a
    /// delta — the BUG-027 discipline).
    /// </summary>
    /// <param name="bars">The ascending bar series the lane stands on.</param>
    /// <param name="buckets">The accumulator the lane owns (keyed by bar open time).</param>
    /// <param name="trade">The trade to attribute.</param>
    /// <param name="barLength">The frame's nominal bar length (the last bar's window bound).</param>
    /// <returns>true when the trade landed in a bucket (the lane marks the lane dirty on it).</returns>
    public static bool AddTrade(
        IReadOnlyList<Bar> bars,
        IDictionary<DateTimeOffset, Bucket> buckets,
        MarketTrade trade,
        TimeSpan barLength)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(buckets);
        ArgumentNullException.ThrowIfNull(trade);
        if (barLength <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(barLength), barLength, "The bar length must be positive.");
        }

        var size = trade.Quantity.Value;
        if (size <= 0m)
        {
            return false;
        }

        var timestamp = trade.Timestamp;

        // The binary search: the LAST bar whose open is not after the
        // timestamp — the slot the window contains it in. An empty series
        // attributes nothing (no slots stand).
        if (bars.Count == 0 || timestamp < bars[0].Time)
        {
            return false;
        }

        var last = bars[^1];
        if (timestamp >= last.Time + barLength)
        {
            return false;
        }

        var low = 0;
        var high = bars.Count - 1;
        while (low < high)
        {
            var mid = low + ((high - low + 1) / 2);
            if (bars[mid].Time <= timestamp)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        var key = bars[low].Time;
        if (!buckets.TryGetValue(key, out var bucket))
        {
            bucket = new Bucket();
            buckets[key] = bucket;
        }

        bucket.Add(trade.Side, size);
        return true;
    }

    /// <summary>
    /// The aligned CVD series: one value per bar slot, index-aligned with the
    /// bars. The first counted bar starts the running sum; a session break
    /// (the VWAP anchor's UTC-day rule) restarts it; a bar with no sided
    /// trades is the honest gap (null) the polyline breaks on — the running
    /// sum carries underneath it, so the next counted bar continues the
    /// standing accumulation.
    /// </summary>
    /// <param name="bars">The ascending bar series (the pane aligns to it).</param>
    /// <param name="buckets">The attributed deltas (keys outside the bars' opens are ignored).</param>
    /// <param name="sessionReset">Whether a session break restarts the sum (the shipped session-anchored semantics).</param>
    /// <returns>The aligned series; empty when the series is empty.</returns>
    public static IReadOnlyList<decimal?> Aligned(
        IReadOnlyList<Bar> bars,
        IReadOnlyDictionary<DateTimeOffset, Bucket> buckets,
        bool sessionReset)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(buckets);

        var values = new decimal?[bars.Count];
        if (bars.Count == 0)
        {
            return values;
        }

        decimal running = 0m;
        var counted = false;
        Bar previous = default;
        for (var i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            if (sessionReset && counted && VwapSessionAnchor.OpensNewSession(previous, bar))
            {
                // The session break restarts the sum: the next counted bar
                // reads its own flow. The break bar itself stays a gap until
                // its own trades land (the reset defers to the first counted
                // bar of the new session).
                running = 0m;
                counted = false;
            }

            if (buckets.TryGetValue(bar.Time, out var bucket) && bucket.HasSidedTrade)
            {
                var delta = bucket.Buy - bucket.Sell;
                running = counted ? running + delta : delta;
                counted = true;
                values[i] = running;
            }

            previous = bar;
        }

        return values;
    }
}
