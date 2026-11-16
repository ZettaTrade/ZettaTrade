namespace ZettaTrade.Charting;

/// <summary>
/// The volume profile's annotations (T-248): the point of control and the
/// value area the standard 70% expansion rule derives from one computed
/// profile. Pure and side-effect free — the goldens pin every number
/// without a feed (the <see cref="VolumeProfile"/> discipline).
/// <para>
/// The math, exactly: the POC is the profile's heaviest bucket — ties keep
/// the LOWEST-priced maximum (an ascending strict scan, so one crafted
/// profile always answers one deterministic POC) — and its label price is
/// the bucket's own mid: OHLCV bars carry no per-tick story, so a bucket is
/// the finest price unit the profile honestly carries. The VALUE AREA grows
/// from the POC by the standard expansion: while the area's cumulative
/// volume is below <see cref="ValueAreaFraction"/> of the total, the next
/// two buckets above and the next two below the area weigh against each
/// other and the heavier side joins (a tie joins the BELOW side — the
/// classic rule's own else branch; a side with fewer than two buckets left
/// joins what it has; a side with none loses the comparison). VAH and VAL
/// are the finished area's outer bucket edges — the top bucket's high and
/// the bottom bucket's low. A zero-total profile answers the honest nulls:
/// no volume, no annotations, never a fabricated shape.
/// </para>
/// </summary>
public sealed record VolumeProfileValueArea(
    decimal? PocPrice,
    decimal? VahPrice,
    decimal? ValPrice)
{
    /// <summary>The standard value-area fraction: 70% of the window's volume.</summary>
    public const decimal ValueAreaFraction = 0.70m;

    /// <summary>
    /// Derive the annotations from the given profile. Pure: nothing outside
    /// the input is read, the input is never mutated.
    /// </summary>
    public static VolumeProfileValueArea From(VolumeProfile? profile)
    {
        if (profile is null || profile.TotalVolume <= 0m || profile.Buckets.Count == 0)
        {
            return new VolumeProfileValueArea(null, null, null);
        }

        var buckets = profile.Buckets;

        // The POC: the heaviest bucket, ties keep the lowest-priced (the
        // ascending strict scan).
        var pocIndex = 0;
        for (var i = 1; i < buckets.Count; i++)
        {
            if (buckets[i].Volume > buckets[pocIndex].Volume)
            {
                pocIndex = i;
            }
        }

        var target = ValueAreaFraction * profile.TotalVolume;
        var from = pocIndex;
        var to = pocIndex;
        var cumulative = buckets[pocIndex].Volume;

        while (cumulative < target)
        {
            // Each side offers at most its next two buckets; what a side
            // lacks it does not offer.
            var aboveCount = Math.Min(2, buckets.Count - 1 - to);
            var belowCount = Math.Min(2, from);

            var above = 0m;
            for (var i = to + 1; i <= to + aboveCount; i++)
            {
                above += buckets[i].Volume;
            }

            var below = 0m;
            for (var i = from - belowCount; i < from; i++)
            {
                below += buckets[i].Volume;
            }

            if (aboveCount == 0 && belowCount == 0)
            {
                // The whole ladder is inside the area already.
                break;
            }

            if (above > below && aboveCount > 0)
            {
                to += aboveCount;
                cumulative += above;
            }
            else if (belowCount > 0)
            {
                from -= belowCount;
                cumulative += below;
            }
            else
            {
                to += aboveCount;
                cumulative += above;
            }
        }

        return new VolumeProfileValueArea(
            (buckets[pocIndex].Low + buckets[pocIndex].High) / 2m,
            buckets[to].High,
            buckets[from].Low);
    }
}
