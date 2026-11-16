namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// The estimated liquidation levels' pure math (T-243): the local estimator
/// that turns the chart's own open-interest deltas and the venue's public
/// leverage-bracket table into the level bands the chart overlays on the
/// price pane. Static and side-effect free so the goldens pin the
/// semantics without a transport, a clock or a scheduler — the same
/// discipline as <see cref="LiquidationCascadeAggregator"/>.
/// <para>
/// The math, exactly: a bucket's OI delta (coin units — both served
/// histories report base-coin size) times the bucket's close price is the
/// delta's notional; the notional picks ONE bracket tier (the first whose
/// cap still bounds it — the tiers ascend by cap and chain their floors;
/// a notional past the top cap clamps onto the top tier); the tier's
/// maximum leverage and maintenance-margin rate scale the bucket's close
/// into the liquidation price: <c>entry × (1 ∓ 1/L + MMR)</c> — minus for
/// the long side (force-closed below the entry), plus for the short.
/// </para>
/// <para>
/// The ENTRY-DISTRIBUTION ASSUMPTION, stated (never hidden — the chart
/// carries the note with the bands): only GROWTH deltas create levels —
/// the bucket's growth is assumed to have ENTERED at the bucket's close,
/// its side taken from the bucket's own direction (an up bucket entered
/// longs, a down bucket shorts; a flat bucket carries no side and stays
/// unattributed). A SHRINKING bucket removes no level: which price
/// bucket's positions closed is unknowable from the series, so the
/// estimator refuses to guess. The consequence is honest: the overlay is a
/// floor of the real map, the same way the liquidation tape is.
/// </para>
/// </summary>
public static class LiquidationLevelEstimator
{
    /// <summary>
    /// The overlay's band bound (the draw pass's budget — a session's worth
    /// of buckets can carry hundreds of deltas and the pane is an overview,
    /// not a firehose): the LARGEST-notional bands stand, the long tail of
    /// small ones stays off the pane. The cap counts the published bands;
    /// the estimator keeps the whole computation and truncates at the end.
    /// </summary>
    public const int MaxBands = 32;

    /// <summary>
    /// Estimate the levels over ONE symbol's loaded window: the bars are
    /// the price buckets, the open-interest series rides them index-aligned
    /// (the OI pane's alignment, T-241), the brackets are the venue's
    /// public risk table (ascending by cap — the adapters pin the order).
    /// </summary>
    /// <param name="bars">The ascending bar series (the price buckets).</param>
    /// <param name="brackets">The venue's bracket table for the symbol, ascending by cap; null or empty is the venue's explicit no-table absence.</param>
    /// <param name="openInterest">The OI series index-aligned with the bars (nulls are the honest gaps; a stale alignment contributes nothing).</param>
    /// <returns>The overlay in its honesty state; the bands empty unless the estimate stands.</returns>
    public static LiquidationLevelsOverlay Compute(
        IReadOnlyList<Bar> bars,
        IReadOnlyList<LeverageBracket>? brackets,
        IReadOnlyList<decimal?> openInterest)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(openInterest);

        if (brackets is not { Count: > 0 })
        {
            return new LiquidationLevelsOverlay(LiquidationLevelsOverlayState.NoBrackets, []);
        }

        if (bars.Count == 0 || openInterest.Count != bars.Count)
        {
            return new LiquidationLevelsOverlay(LiquidationLevelsOverlayState.NoDeltas, []);
        }

        var candidates = new List<LiquidationLevelBand>();

        // The delta walk starts at the SECOND bar: the first bar's slot has
        // no previous OI to differ against (its delta is unknowable, never
        // a zero). A null on either side of the pair is the alignment's
        // honest gap — the pair contributes nothing.
        for (var i = 1; i < bars.Count; i++)
        {
            var current = openInterest[i];
            var previous = openInterest[i - 1];
            if (current is not { } now || previous is not { } before)
            {
                continue;
            }

            var delta = now - before;
            if (delta <= 0m)
            {
                // Flat: no new positions. Shrinking: the closure's bucket
                // is unknowable — the assumption refuses to attribute it
                // (the stated entry-distribution assumption).
                continue;
            }

            var bar = bars[i];
            if (bar.Close == bar.Open)
            {
                // A flat bucket carries no direction, so the growth's side
                // is unattributable — the same refusal the shrink gets.
                continue;
            }

            var notional = delta * bar.Close;
            if (notional <= 0m)
            {
                continue;
            }

            var tier = PickTier(brackets, notional);
            if (tier is not { } picked)
            {
                continue;   // No sane tier bound the notional — no fabrication.
            }

            var side = bar.Close > bar.Open ? LiquidationSide.Long : LiquidationSide.Short;
            var price = side == LiquidationSide.Long
                ? bar.Close * (1m - 1m / picked.MaxLeverage + picked.MaintenanceMarginRate)
                : bar.Close * (1m + 1m / picked.MaxLeverage - picked.MaintenanceMarginRate);
            if (price <= 0m)
            {
                // A degenerate tier could floor the long's multiplier at
                // zero or below: the level would be a fabrication, skip it.
                continue;
            }

            candidates.Add(new LiquidationLevelBand(price, notional, side));
        }

        if (candidates.Count == 0)
        {
            return new LiquidationLevelsOverlay(LiquidationLevelsOverlayState.NoDeltas, []);
        }

        // The band cap: the LARGEST notional stands, ties break by input
        // order (the stable sort), the survivors re-sort ascending by
        // price (largest notional first at a tie) for the draw's order.
        var bands = candidates
            .Select((band, index) => (band, index))
            .OrderByDescending(row => row.band.Notional)
            .ThenBy(row => row.index)
            .Take(MaxBands)
            .Select(row => row.band)
            .OrderBy(band => band.Price)
            .ThenByDescending(band => band.Notional)
            .ToList();

        return new LiquidationLevelsOverlay(LiquidationLevelsOverlayState.Estimated, bands);
    }

    /// <summary>
    /// Pick the ONE tier the notional falls into: the first whose cap still
    /// bounds it (the tiers ascend by cap and chain their floors, so the
    /// first cap ≥ notional is the tier — its floor is the previous cap ≤
    /// notional). A notional past every cap clamps onto the top tier (the
    /// stated clamp — the top tier's tighter leverage and higher margin
    /// stand for anything larger). Degenerate tiers (non-positive leverage
    /// or a negative margin rate) never bound a pick; none left means no
    /// estimate.
    /// </summary>
    private static LeverageBracket? PickTier(IReadOnlyList<LeverageBracket> brackets, decimal notional)
    {
        for (var i = 0; i < brackets.Count; i++)
        {
            var tier = brackets[i];
            if (tier.MaxLeverage <= 0m || tier.MaintenanceMarginRate < 0m)
            {
                continue;
            }

            if (tier.NotionalCap >= notional)
            {
                return tier;
            }
        }

        // The clamp: walk back to the last sane tier (the table's top).
        for (var i = brackets.Count - 1; i >= 0; i--)
        {
            var tier = brackets[i];
            if (tier.MaxLeverage > 0m && tier.MaintenanceMarginRate >= 0m)
            {
                return tier;
            }
        }

        return null;
    }
}
