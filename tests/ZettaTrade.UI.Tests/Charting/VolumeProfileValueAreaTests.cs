namespace ZettaTrade.UI.Tests.Charting;

using System;
using FluentAssertions;
using Xunit;
using ZettaTrade.Charting;

/// <summary>
/// Pins for the volume profile's annotations (T-248): the POC and the
/// standard 70% value-area expansion, on crafted bucket ladders — the exact
/// golden numbers, the tie rules (the POC keeps the lowest-priced maximum,
/// the expansion's tie joins the below side), the side exhaustion, the
/// flat-spread single bucket and the honest no-volume nulls. Pure facts —
/// the canvas itself is UIA-invisible.
/// </summary>
public sealed class VolumeProfileValueAreaTests
{
    /// <summary>A bucket ladder built by hand: the lows step by 10, the
    /// highs by 10 (each bucket's high IS the next bucket's low — the
    /// T-093 ladder discipline), the volumes given.</summary>
    private static VolumeProfile Profile(params decimal[] volumes)
    {
        var buckets = new VolumeProfileBucket[volumes.Length];
        for (var i = 0; i < volumes.Length; i++)
        {
            buckets[i] = new VolumeProfileBucket(10m * (i + 1), 10m * (i + 2), volumes[i]);
        }

        var total = 0m;
        foreach (var volume in volumes)
        {
            total += volume;
        }

        return new VolumeProfile(buckets, total);
    }

    /// <summary>
    /// The exact expansion golden: the POC's two-bucket steps climb the
    /// heavier side until the area holds 70% of the window's volume — here
    /// the first step already covers it.
    /// </summary>
    [Fact]
    public void From_TheStandardExpansion_CoversSeventyPercent()
    {
        // total 14, target 9.8: the POC (6) joins the heavier neighbor pair
        // (3 + 2 above, 2 + 1 below) and the area stops at 11 >= 9.8.
        var annotations = VolumeProfileValueArea.From(Profile(1, 2, 6, 3, 2));

        annotations.PocPrice.Should().Be(35m, "the POC bucket [30, 40] reads its own mid");
        annotations.VahPrice.Should().Be(60m, "the area's top bucket is [50, 60]");
        annotations.ValPrice.Should().Be(30m, "the area's bottom bucket is the POC's own");
    }

    /// <summary>
    /// The expansion's tie rule: the two candidate sides weigh equal and the
    /// BELOW side joins — the classic rule's own else branch. The mirrored
    /// answer (the above side) would put the area's edges on the other
    /// buckets entirely.
    /// </summary>
    [Fact]
    public void From_AnExpansionTie_JoinsTheBelowSide()
    {
        // total 10, target 7: the POC (4) sees 3 above and 3 below — the
        // below pair joins, the area closes at exactly 7.
        var annotations = VolumeProfileValueArea.From(Profile(2, 1, 4, 1, 2));

        annotations.PocPrice.Should().Be(35m);
        annotations.VahPrice.Should().Be(40m, "the area never left the POC bucket upward");
        annotations.ValPrice.Should().Be(10m, "the below pair joined the area");
    }

    /// <summary>
    /// A side shorter than two buckets offers what it has; the walk keeps
    /// expanding until the 70% target holds.
    /// </summary>
    [Fact]
    public void From_AShortSide_JoinsWhatItHas()
    {
        // total 5, target 3.5: the POC (2) sees one bucket above (2) against
        // one below (1) — the above joins and the area stops at 4.
        var annotations = VolumeProfileValueArea.From(Profile(1, 2, 2));

        annotations.PocPrice.Should().Be(25m);
        annotations.VahPrice.Should().Be(40m, "the single above bucket joined");
        annotations.ValPrice.Should().Be(20m);
    }

    /// <summary>
    /// The ladder's exhaustion: a zero-volume neighbor offers nothing, the
    /// walk takes the other side and the area still reaches the target.
    /// </summary>
    [Fact]
    public void From_AZeroVolumeNeighbor_TakesTheOtherSide()
    {
        // total 5, target 3.5: the POC (3) sees a zero-volume bucket above
        // against two below (1 + 1) — the below pair joins.
        var annotations = VolumeProfileValueArea.From(Profile(1, 1, 3, 0));

        annotations.PocPrice.Should().Be(35m);
        annotations.VahPrice.Should().Be(40m);
        annotations.ValPrice.Should().Be(10m, "the below pair joined, the zero bucket stayed out");
    }

    /// <summary>
    /// The POC's own tie rule: two equally heavy buckets keep the
    /// LOWEST-priced maximum (the ascending strict scan).
    /// </summary>
    [Fact]
    public void From_APointOfControlTie_KeepsTheLowestPricedMaximum()
    {
        var annotations = VolumeProfileValueArea.From(Profile(5, 5));

        annotations.PocPrice.Should().Be(15m, "the first maximum in ascending order wins");
        annotations.VahPrice.Should().Be(30m);
        annotations.ValPrice.Should().Be(10m);
    }

    /// <summary>
    /// The flat spread: a single-bucket profile is its own POC and its own
    /// value area — one price, three labels on it.
    /// </summary>
    [Fact]
    public void From_ASingleBucketProfile_IsItsOwnValueArea()
    {
        var annotations = VolumeProfileValueArea.From(new VolumeProfile(
            [new VolumeProfileBucket(100m, 100m, 10m)],
            10m));

        annotations.PocPrice.Should().Be(100m);
        annotations.VahPrice.Should().Be(100m);
        annotations.ValPrice.Should().Be(100m);
    }

    /// <summary>
    /// The honest no-volume absences: a zero-total profile and a null answer
    /// the nulls — never a fabricated annotation.
    /// </summary>
    [Fact]
    public void From_AbsentVolume_AnswersTheHonestNulls()
    {
        VolumeProfileValueArea.From(null).PocPrice.Should().BeNull();
        VolumeProfileValueArea.From(null).VahPrice.Should().BeNull();
        VolumeProfileValueArea.From(null).ValPrice.Should().BeNull();

        var empty = VolumeProfileValueArea.From(new VolumeProfile([], 0m));
        empty.PocPrice.Should().BeNull();
        empty.VahPrice.Should().BeNull();
        empty.ValPrice.Should().BeNull();

        var noVolume = VolumeProfileValueArea.From(Profile(0, 0, 0));
        noVolume.PocPrice.Should().BeNull();
        noVolume.VahPrice.Should().BeNull();
        noVolume.ValPrice.Should().BeNull();
    }

    /// <summary>The standard fraction is the documented 70%.</summary>
    [Fact]
    public void TheValueAreaFraction_IsTheDocumentedStandard()
    {
        VolumeProfileValueArea.ValueAreaFraction.Should().Be(0.70m);
    }
}
