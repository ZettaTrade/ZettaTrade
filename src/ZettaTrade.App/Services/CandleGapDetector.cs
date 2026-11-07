namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;

/// <summary>
/// Calendar-aware in-series gap detector (T-086): between two known bars the
/// calendar claims the missing opens — see
/// <see cref="TimeFrameCalendar.NextOpenAfter"/> — and a contiguous run of
/// them is one <see cref="CandleGap"/>. Bars beyond the horizon (a future
/// instant) are never missing, and a hole wider than
/// <see cref="MaxMissingBarsPerGap"/> reports as
/// <see cref="CandleGap.IsTruncated"/>: its enumerated width stops at the
/// bound while a bounded repair pass skips it entirely (deeper history is
/// the paging machinery's own bounded passes, never a subscription's job).
/// <para>
/// The guard convention follows <see cref="BarSeriesMerge"/>: a contract
/// trip returns <c>null</c> — the caller keeps the view exactly as it stood
/// (the honest-view rule), never a repaired or defaulted substitute.
/// </para>
/// </summary>
public static class CandleGapDetector
{
    /// <summary>
    /// Upper bound of the missing-run enumeration per pair, and with it the
    /// widest hole one repair pass will fetch: a half-day of one-minute
    /// candles plus slack — comfortably an outage of hours on every frame
    /// (a hole this wide is only the remainder of an even wider one, which
    /// the paging machinery continues).
    /// </summary>
    public const int MaxMissingBarsPerGap = 1_200;

    /// <summary>
    /// Every gap in the series, in pair order. An empty or single-bar series
    /// has no pairs and no gaps; a non-strictly-ascending series trips the
    /// guard (<c>null</c>).
    /// </summary>
    /// <param name="ascendingSeries">The series (strictly ascending open
    /// times, the T-023 contract).</param>
    /// <param name="timeFrame">The cadence the series is built on.</param>
    /// <param name="assetClass">The venue the cadence walks (weekday
    /// sessions for the non-crypto classes).</param>
    /// <param name="horizon">The "now" instant: expected opens beyond it do
    /// not exist yet and are never missing.</param>
    public static IReadOnlyList<CandleGap>? DetectGaps(
        IReadOnlyList<Bar> ascendingSeries,
        TimeFrame timeFrame,
        AssetClass assetClass,
        DateTimeOffset horizon)
    {
        ArgumentNullException.ThrowIfNull(ascendingSeries);

        if (ascendingSeries.Count < 2)
        {
            return [];
        }

        for (var i = 1; i < ascendingSeries.Count; i++)
        {
            if (ascendingSeries[i].Time <= ascendingSeries[i - 1].Time)
            {
                return null;
            }
        }

        var gaps = new List<CandleGap>();
        for (var i = 1; i < ascendingSeries.Count; i++)
        {
            if (WalkMissingRun(
                    ascendingSeries[i - 1].Time,
                    ascendingSeries[i].Time,
                    timeFrame,
                    assetClass,
                    horizon) is { } gap)
            {
                gaps.Add(gap);
            }
        }

        return gaps;
    }

    /// <summary>
    /// The missing run between the open times of two consecutive known bars:
    /// the expected opens the calendar walks from the left bar strictly below
    /// the right one, capped at <see cref="MaxMissingBarsPerGap"/> — the
    /// truncation marks <see cref="CandleGap.IsTruncated"/> (the run continues
    /// beyond the bound). Nothing below the horizon counts as missing.
    /// </summary>
    private static CandleGap? WalkMissingRun(
        DateTimeOffset from,
        DateTimeOffset until,
        TimeFrame timeFrame,
        AssetClass assetClass,
        DateTimeOffset horizon)
    {
        var expected = TimeFrameCalendar.NextOpenAfter(timeFrame, assetClass, from);
        if (expected >= until)
        {
            return null;
        }

        var first = expected;
        DateTimeOffset last = default;
        var missing = 0;
        var truncated = false;
        while (expected < until && expected < horizon)
        {
            if (missing >= MaxMissingBarsPerGap)
            {
                truncated = true;
                break;
            }

            missing++;
            last = expected;
            expected = TimeFrameCalendar.NextOpenAfter(timeFrame, assetClass, expected);
        }

        return missing == 0
            ? null
            : new CandleGap(first, last, missing, truncated);
    }
}
