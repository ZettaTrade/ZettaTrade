namespace ZettaTrade.Charting;

using ZettaTrade.Core.Entities;

/// <summary>
/// One price bucket of the volume-at-price histogram (T-093): the price span
/// the bucket covers (inclusive low, the last bucket's high closed at the
/// window's high) and the volume the window's bars allocated into it.
/// </summary>
public sealed record VolumeProfileBucket(decimal Low, decimal High, decimal Volume);

/// <summary>
/// The volume-at-price histogram (T-093) over ONE chart window: the buckets
/// cut the window's price spread into an even ladder, and every bar's
/// volume rides the bucket that holds its typical price (H+L+C)/3 — the
/// same weighted price the VWAP overlay weights. OHLCV bars carry no
/// per-tick story, so the profile claims exactly this fidelity and nothing
/// finer: no volume is ever split or interpolated between buckets, an
/// unchanged window computes the same buckets bit-for-bit, and a zero
/// <see cref="TotalVolume"/> means the honest no-volume state, never a
/// fabricated shape.
/// </summary>
public sealed record VolumeProfile(IReadOnlyList<VolumeProfileBucket> Buckets, decimal TotalVolume)
{
    /// <summary>
    /// Compute the histogram over the given window's bars: the buckets cut
    /// the spread between the window's lowest low and its highest high into
    /// <paramref name="bucketCount"/> even steps. Pure: nothing outside the
    /// input is read, the input is never mutated.
    /// </summary>
    /// <param name="bars">The window's ascending open-time bars (the chart
    /// passes its visible snapshot).</param>
    /// <param name="bucketCount">The even bucket ladder's size, at least one.</param>
    /// <returns>The buckets in ascending price order and the window's total volume.</returns>
    public static VolumeProfile Compute(IReadOnlyList<Bar> bars, int bucketCount)
    {
        if (bucketCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bucketCount),
                bucketCount,
                "The volume profile needs at least one bucket.");
        }

        if (bars.Count == 0)
        {
            return new VolumeProfile([], 0m);
        }

        var lowest = bars[0].Low;
        var highest = bars[0].High;
        foreach (var bar in bars)
        {
            if (bar.Low < lowest)
            {
                lowest = bar.Low;
            }

            if (bar.High > highest)
            {
                highest = bar.High;
            }
        }

        var total = 0m;
        foreach (var bar in bars)
        {
            total += bar.Volume;
        }

        // A window that prints one price (a fully flat spread) is a single
        // price level: every bar's volume lands in the one bucket that spans
        // it exactly.
        if (highest == lowest)
        {
            return new VolumeProfile([new VolumeProfileBucket(lowest, highest, total)], total);
        }

        var step = (highest - lowest) / bucketCount;
        var volumes = new decimal[bucketCount];        foreach (var bar in bars)
        {
            if (bar.Volume == 0m)
            {
                continue;
            }

            var typical = (bar.High + bar.Low + bar.Close) / 3m;
            var slot = (int)decimal.Floor((typical - lowest) / step);

            // The highest typical price floors exactly onto the count (its
            // bucket's own high edge): it belongs to the last bucket.
            if (slot >= bucketCount)
            {
                slot = bucketCount - 1;
            }

            volumes[slot] += bar.Volume;
        }

        // The ladder walks additively: each bucket's high IS the next
        // bucket's low (the same decimal value), so the buckets share their
        // edges exactly no matter how the step division rounded - a
        // multiplicative re-derivation would drift neighbors apart by the
        // rounding of the last digit.
        List<VolumeProfileBucket> buckets = new(bucketCount);
        var low = lowest;
        for (var i = 0; i < bucketCount; i++)
        {
            if (i == bucketCount - 1)
            {
                buckets.Add(new VolumeProfileBucket(low, highest, volumes[i]));
                break;
            }

            var high = low + step;
            buckets.Add(new VolumeProfileBucket(low, high, volumes[i]));
            low = high;
        }

        return new VolumeProfile(buckets, total);
    }
}
