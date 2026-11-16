namespace ZettaTrade.Charting;

using System;
using System.Collections.Generic;
using System.Linq;
using ZettaTrade.Core.Entities;

/// <summary>
/// One automatic S/R level (T-249): the price a cluster of swing pivots
/// agrees on and the cluster's weight. The kind (support or resistance) is
/// NOT carried — it is decided in the draw pass against the last visible
/// close (a level flips kind when price crosses it, so a stored kind would
/// go stale mid-session).
/// </summary>
/// <param name="Price">The level's price: the members' exact mean (the
/// cluster's consensus, never a single pivot's own extreme).</param>
/// <param name="Weight">The cluster's summed pivot strength — more or
/// more-dominant pivots make a heavier level (the draw pass normalizes
/// the set's weights into each line's emphasis).</param>
public sealed record AutoLevel(decimal Price, int Weight);

/// <summary>
/// The automatic support/resistance levels (T-249): the swing pivots'
/// price-proximity clusters, computed over the WHOLE loaded series the
/// same lane the session analytics and the naked-POC track ride. Pure and
/// side-effect free.
/// <para>
/// The clustering, exactly: the pivots sort by price; a cluster's ANCHOR
/// is its first (lowest-priced) pivot, and the next pivot joins the
/// standing cluster while its price sits within the tolerance of the
/// ANCHOR — never of the previous member (no chaining: a slow drift of
/// pivots each within tolerance of the last cannot walk a cluster across
/// the whole axis). The level answers the members' exact mean. The
/// tolerance speaks as a fraction of price (0.005 = half a percent), so
/// the band scales with the instrument instead of a fixed tick size.
/// </para>
/// </summary>
/// <param name="Levels">The levels, ascending by price. Deterministic: the
/// same series and knobs answer the same levels on every recompute — the
/// pan-stability pin (a visible window never enters the compute).</param>
public sealed record SupportResistanceLevels(IReadOnlyList<AutoLevel> Levels)
{
    /// <summary>
    /// The default cluster tolerance: half a percent of price — tight
    /// enough to keep distinct pivots apart, loose enough to merge a
    /// retest's jitter at any liquid instrument's scale.
    /// </summary>
    public const decimal DefaultTolerance = 0.005m;

    /// <summary>
    /// Compute the levels straight from the loaded series: detect the
    /// swing pivots, cluster them. The composition the ViewModel's
    /// bars-applied lane calls — the same knobs the detector's own
    /// defaults speak unless the toolbar's knobs say otherwise.
    /// </summary>
    /// <param name="bars">The loaded ascending open-time bars — the WHOLE
    /// series, never a visible window (a window-scoped compute would
    /// flicker on every pan; the pan stability rides the series-scoped
    /// input).</param>
    /// <param name="lookback">The detection arm in bars each side (the
    /// detector clamps it into its legal range).</param>
    /// <param name="minStrength">The minimum measured dominance a pivot
    /// must reach to survive (the detector clamps it at zero).</param>
    /// <param name="tolerance">The cluster tolerance as a fraction of
    /// price; clamped at zero (each pivot its own level).</param>
    public static SupportResistanceLevels FromBars(
        IReadOnlyList<Bar> bars,
        int lookback,
        int minStrength,
        decimal tolerance) =>
        Compute(SwingPivots.Detect(bars, lookback, minStrength), tolerance);

    /// <summary>
    /// Cluster the pivots into levels: sort by price, walk once, each
    /// cluster anchored at its first member and closed by the first price
    /// beyond the anchor's tolerance band; the level answers the members'
    /// exact mean, ascending output. Pure: nothing outside the input is
    /// read, the input is never mutated.
    /// </summary>
    /// <param name="pivots">The detected swing pivots (any order; the walk
    /// sorts).</param>
    /// <param name="tolerance">The cluster tolerance as a fraction of
    /// price; clamped at zero.</param>
    public static SupportResistanceLevels Compute(IReadOnlyList<SwingPivot> pivots, decimal tolerance)
    {
        ArgumentNullException.ThrowIfNull(pivots);

        if (pivots.Count == 0)
        {
            return new SupportResistanceLevels([]);
        }

        var band = Math.Max(0m, tolerance);
        var ordered = pivots.OrderBy(static p => p.Price).ToArray();

        var levels = new List<AutoLevel>(ordered.Length);
        var anchor = ordered[0].Price;
        var priceSum = ordered[0].Price;
        var weightSum = ordered[0].Strength;
        var members = 1;

        for (var i = 1; i < ordered.Length; i++)
        {
            var pivot = ordered[i];
            if (pivot.Price > anchor * (1m + band))
            {
                // The anchor's band closed: the standing cluster emits and
                // the pivot seeds the next one (the anchor is the SEED's
                // price — a member never drags the boundary).
                levels.Add(new AutoLevel(priceSum / members, weightSum));
                anchor = pivot.Price;
                priceSum = pivot.Price;
                weightSum = pivot.Strength;
                members = 1;
                continue;
            }

            priceSum += pivot.Price;
            weightSum += pivot.Strength;
            members++;
        }

        levels.Add(new AutoLevel(priceSum / members, weightSum));

        return new SupportResistanceLevels(levels);
    }
}
