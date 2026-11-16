namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using ZettaTrade.Core.Entities;

/// <summary>
/// The venue-spread pass's pure math (T-253). Three reads, all total
/// functions that answer the honest null/empty instead of throwing:
/// the cross-venue median (the robust reference — no venue's own wire
/// quirk steers the table), the basis-point delta against a reference
/// (a non-positive reference is unanswerable — the refusal), and the
/// perp-spot basis series keyed by the bar-open time (the T-252
/// alignment axis: two series pair ONLY over their shared bar times, a
/// non-positive close refuses its sample and poisons nothing else).
/// Every list is sorted by its key, so a recompute is bit-identical.
/// </summary>
public static class VenueSpreadMath
{
    /// <summary>
    /// The values' median: the sorted mid for an odd count, the mean of
    /// the two mids for an even one. An empty set answers null (no
    /// reference exists — the caller's honest dash, never a zero).
    /// </summary>
    public static decimal? Median(IReadOnlyList<decimal> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var sorted = values.OrderBy(static value => value).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2m;
    }

    /// <summary>
    /// The value's delta against the reference in basis points
    /// ((value − reference) / reference × 10 000). A non-positive
    /// reference (a zero or a corrupt negative price) is unanswerable —
    /// the honest null, never a fabricated ratio.
    /// </summary>
    public static double? Bps(decimal reference, decimal value) =>
        reference > 0m
            ? (double)((value - reference) / reference) * 10000.0
            : null;

    /// <summary>
    /// The answering venues' deltas against their own median last (the
    /// spread table's read). Fewer than two answering venues define no
    /// cross-venue spread at all — every delta answers null (a single
    /// venue's delta against itself would read 0 and dress the absence
    /// up as a verdict). A venue that did not answer keeps null; the
    /// median rides the ANSWERING set only.
    /// </summary>
    /// <param name="lasts">The venues' last prices in the table's venue order (null = did not answer).</param>
    /// <returns>One delta per input slot, in the same order.</returns>
    public static IReadOnlyList<double?> DeltasVsMedian(IReadOnlyList<decimal?> lasts)
    {
        ArgumentNullException.ThrowIfNull(lasts);
        var answered = lasts.Where(static last => last.HasValue)
            .Select(static last => last!.Value)
            .ToArray();
        if (answered.Length < 2)
        {
            return [.. lasts.Select(_ => (double?)null)];
        }

        var median = Median(answered)!.Value;
        return [.. lasts.Select(last => last.HasValue ? Bps(median, last!.Value) : (double?)null)];
    }

    /// <summary>
    /// The perp-spot basis series: for every SHARED bar-open time, the
    /// perp's close against the spot's close in basis points
    /// ((perp − spot) / spot × 10 000) — the carry premium the two
    /// categories price, keyed by the bar the venues BOTH traded (the
    /// T-252 honest alignment axis; a pairing over each series' own
    /// times would stitch gaps and lie). The series sorts by time, so a
    /// recompute is bit-identical. A non-positive close on either side
    /// refuses that bar's sample (corrupt data, never an exception);
    /// the series carries the bars that survived.
    /// </summary>
    /// <param name="spotBars">The spot series' bars (any order; keyed by open time).</param>
    /// <param name="perpBars">The perpetual series' bars (any order; keyed by open time).</param>
    /// <returns>The (time, bps) samples, ascending by time.</returns>
    public static IReadOnlyList<(DateTimeOffset Time, double Bps)> BasisSeries(
        IReadOnlyList<Bar> spotBars,
        IReadOnlyList<Bar> perpBars)
    {
        ArgumentNullException.ThrowIfNull(spotBars);
        ArgumentNullException.ThrowIfNull(perpBars);

        var spotByTime = spotBars
            .GroupBy(static bar => bar.Time)
            .Select(static group => (group.Key, group.First().Close))
            .ToDictionary(static pair => pair.Key, static pair => pair.Item2);
        var samples = new List<(DateTimeOffset Time, double Bps)>(perpBars.Count);
        foreach (var (time, perpClose) in perpBars
            .GroupBy(static bar => bar.Time)
            .Select(static group => (group.Key, group.First().Close))
            .OrderBy(static pair => pair.Key))
        {
            if (!spotByTime.TryGetValue(time, out var spotClose))
            {
                continue;
            }

            if (spotClose <= 0m || perpClose <= 0m)
            {
                continue;
            }

            samples.Add((time, (double)((perpClose - spotClose) / spotClose) * 10000.0));
        }

        return samples;
    }
}
