namespace ZettaTrade.Charting;

using System;
using System.Collections.Generic;
using ZettaTrade.Core.Entities;

/// <summary>One naked POC (T-248): the owning session's UTC date and the
/// POC price the track carries.</summary>
public sealed record NakedPoc(DateTimeOffset Session, decimal Price);

/// <summary>
/// The naked-POC track (T-248): the past sessions' points of control price
/// has not revisited. A session's POC computes over that session's own bars
/// (the same OHLCV fidelity as the window profile — the typical price rides
/// one bucket per bar, the POC is the heaviest bucket, ties keep the
/// lowest-priced maximum) and stays naked until a LATER bar's range touches
/// the price again — the first later-day bar whose low sits at or under the
/// price and whose high sits at or over it clears the POC. The session anchor is the UTC
/// calendar-date boundary the whole chart family rides (the
/// <see cref="VwapSessionAnchor"/> rule — a venue's quiet days carry no
/// bars, so a stock's weekend sessions simply do not exist in the track).
/// Pure and side-effect free; the track never assumes anything outside the
/// loaded series — the last session's POC has no later bar to answer to and
/// stays naked by construction.
/// </summary>
public sealed record NakedPocTrack(IReadOnlyList<NakedPoc> Items)
{
    /// <summary>
    /// The session profiles' bucket count: the window histogram's own ladder
    /// size (the renderer's <c>VolumeProfileBucketCount</c>), so a session
    /// POC and the drawn histogram speak the same granularity.
    /// </summary>
    public const int SessionBucketCount = 24;

    /// <summary>
    /// Compute the naked-POC track over the loaded series: one ascending
    /// session walk — every session's bars first clear the live candidates
    /// they touch (only earlier sessions' POCs are live: a session never
    /// answers to its own POC, which sits inside its own range by
    /// construction), then the session's own POC joins the live list. The
    /// survivors after the last session are the track. One O(N·live) pass,
    /// no per-bar allocations beyond the session slices. Pure: nothing
    /// outside the input is read, the input is never mutated.
    /// </summary>
    /// <param name="bars">The loaded ascending open-time bars (the chart's
    /// whole series, not a visible window — a past session's naked POC
    /// matters long after it left the lens).</param>
    public static NakedPocTrack Compute(IReadOnlyList<Bar> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);

        if (bars.Count == 0)
        {
            return new NakedPocTrack([]);
        }

        var live = new List<NakedPoc>();
        var currentDay = bars[0].Time.UtcDateTime.Date;
        var dayStart = 0;

        for (var i = 1; i <= bars.Count; i++)
        {
            var dayBreak = i == bars.Count ||
                           bars[i].Time.UtcDateTime.Date != currentDay;
            if (!dayBreak)
            {
                continue;
            }

            // This session's bars clear every earlier session's POC they
            // touch.
            for (var b = dayStart; b < i; b++)
            {
                var bar = bars[b];
                for (var p = live.Count - 1; p >= 0; p--)
                {
                    var poc = live[p];
                    if (bar.Low <= poc.Price && poc.Price <= bar.High)
                    {
                        live.RemoveAt(p);
                    }
                }
            }

            // The session's own POC joins the live list (a volume-less
            // session joins nothing — no profile, no POC).
            var session = VolumeProfile.Compute(Slice(bars, dayStart, i), SessionBucketCount);
            if (session.TotalVolume > 0m)
            {
                var buckets = session.Buckets;
                var pocIndex = 0;
                for (var b = 1; b < buckets.Count; b++)
                {
                    if (buckets[b].Volume > buckets[pocIndex].Volume)
                    {
                        pocIndex = b;
                    }
                }

                live.Add(new NakedPoc(
                    new DateTimeOffset(currentDay, TimeSpan.Zero),
                    (buckets[pocIndex].Low + buckets[pocIndex].High) / 2m));
            }

            if (i < bars.Count)
            {
                currentDay = bars[i].Time.UtcDateTime.Date;
                dayStart = i;
            }
        }

        return new NakedPocTrack([.. live]);
    }

    /// <summary>Copy [start, end) of the bar series into a fresh array.</summary>
    private static Bar[] Slice(IReadOnlyList<Bar> bars, int start, int end)
    {
        var copy = new Bar[end - start];
        for (var i = start; i < end; i++)
        {
            copy[i - start] = bars[i];
        }

        return copy;
    }
}
