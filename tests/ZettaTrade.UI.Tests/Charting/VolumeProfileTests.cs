namespace ZettaTrade.UI.Tests.Charting;

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;

/// <summary>
/// Pins for the volume-at-price histogram compute (T-093): the bucket ladder
/// alignment over the window's spread, the exact volume conservation, the
/// flat-spread and no-volume edges, and the guarded bucket count. Pure
/// facts — the canvas itself is UIA-invisible.
/// </summary>
public sealed class VolumeProfileTests
{
    /// <summary>The window base the facts build on (UTC, offset zero).</summary>
    private static readonly DateTimeOffset Base = new(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A bar over exactly the given price span with the given volume
    /// (the high and low carry the span ends; the close sits between).</summary>
    private static Bar SpanBar(int hourOffset, decimal low, decimal high, decimal volume) =>
        new(Base.AddHours(hourOffset), low + ((high - low) / 2m), high, low, low + ((high - low) / 2m), volume);

    #region Bucket Alignment

    /// <summary>
    /// The exact alignment golden: a [0, 8] window cut into 8 buckets steps
    /// every bucket at an integer price, the bars' typical prices land one
    /// per bucket in order, and the volumes conserve exactly (every bar
    /// contributes its whole volume to exactly one bucket).
    /// </summary>
    [Fact]
    public void Compute_TheEvenLadder_AlignsBucketsAndConservesTheVolumeExactly()
    {
        var bars = Enumerable.Range(0, 8)
            .Select(i => SpanBar(i, low: i, high: i + 1m, volume: 2m + i))
            .ToArray();

        var profile = VolumeProfile.Compute(bars, 8);

        profile.TotalVolume.Should().Be(2 + 3 + 4 + 5 + 6 + 7 + 8 + 9m);

        profile.Buckets.Should().HaveCount(8);
        for (var i = 0; i < profile.Buckets.Count; i++)
        {
            var bucket = profile.Buckets[i];
            bucket.Low.Should().Be(i, "the ladder starts at the window's low and steps one per bucket");
            bucket.High.Should().Be(i + 1m, "the ladder ends exactly at the window's high");
            bucket.Volume.Should().Be(2m + i, "the bar whose typical price is i+0.5 owns bucket i");
        }

        // Volume conservation, exact: bars straddle no boundary in this window.
        profile.Buckets.Sum(static bucket => bucket.Volume).Should().Be(profile.TotalVolume);
    }

    /// <summary>The buckets form a contiguous non-overlapping ascending ladder.</summary>
    [Theory]
    [InlineData(7)]
    [InlineData(24)]
    [InlineData(64)]
    public void Compute_TheLadderIsContiguousAndClosesAtTheWindowHigh(int bucketCount)
    {
        var bars = new[]
        {
            new Bar(Base, 100m, 150m, 90m, 120m, 3m),
            new Bar(Base.AddHours(1), 140m, 150m, 95m, 110m, 2m),
            new Bar(Base.AddHours(2), 95m, 130m, 91m, 100m, 4m),
        };

        var profile = VolumeProfile.Compute(bars, bucketCount);

        profile.Buckets.Should().HaveCount(bucketCount);
        profile.Buckets[0].Low.Should().Be(90m, "the ladder starts at the window's lowest low");
        profile.Buckets[^1].High.Should().Be(150m, "the ladder ends exactly at the window's highest high (closed)");
        for (var i = 0; i < profile.Buckets.Count - 1; i++)
        {
            profile.Buckets[i].Volume.Should().BeGreaterThanOrEqualTo(0m, "no bucket ever turns negative");
            profile.Buckets[i].High.Should().Be(profile.Buckets[i + 1].Low, "the buckets share their edges, never overlap");
            profile.Buckets[i].High.Should().BeGreaterThan(profile.Buckets[i].Low, "the steps stay positive over a real spread");
        }
    }

    /// <summary>Every bar's volume lands somewhere: the sums conserve over
    /// a window whose typical prices straddle bucket edges.</summary>
    [Theory]
    [InlineData(5)]
    [InlineData(3)]
    public void Compute_AWindowWithEdgeStraddlingBars_ConservesTheVolumeExactly(int bucketCount)
    {
        var bars = new[]
        {
            new Bar(Base, 0m, 10m, 0m, 5m, 7m),
            new Bar(Base.AddHours(1), 2m, 8m, 1m, 4m, 11m),
            new Bar(Base.AddHours(2), 9m, 10m, 9m, 10m, 1m),
        };

        var profile = VolumeProfile.Compute(bars, bucketCount);

        profile.Buckets.Sum(static bucket => bucket.Volume)
            .Should().Be(profile.TotalVolume, "a bar's volume lands whole in the bucket that holds its typical price");
        profile.TotalVolume.Should().Be(19m);
    }

    #endregion

    #region Edges

    /// <summary>A window that prints one price: one bucket spanning it exactly
    /// with every bar's volume, never a zero-width ladder.</summary>
    [Fact]
    public void Compute_AFlatSpread_PutsEveryBarIntoOneSpanningBucket()
    {
        var bars = new[]
        {
            new Bar(Base, 7m, 7m, 7m, 7m, 2m),
            new Bar(Base.AddHours(1), 7m, 7m, 7m, 7m, 3m),
        };

        var profile = VolumeProfile.Compute(bars, 24);

        profile.Buckets.Should().ContainSingle();
        profile.Buckets[0].Low.Should().Be(7m);
        profile.Buckets[0].High.Should().Be(7m, "the bucket spans the single price level exactly");
        profile.Buckets[0].Volume.Should().Be(5m, "every bar's volume lands in the one level");
        profile.TotalVolume.Should().Be(5m);
    }

    /// <summary>A fully zero-volume window: the ladder computes but holds no
    /// volume - the chart presents the honest no-volume state off this.</summary>
    [Fact]
    public void Compute_AZeroVolumeWindow_KeepsTheTotalAtZero()
    {
        var bars = new[]
        {
            new Bar(Base, 9m, 11m, 8m, 10m, 0m),
            new Bar(Base.AddHours(1), 10m, 12m, 9m, 11m, 0m),
        };

        var profile = VolumeProfile.Compute(bars, 6);

        profile.TotalVolume.Should().Be(0m);
        profile.Buckets.Should().HaveCount(6).And.OnlyContain(bucket => bucket.Volume == 0m);
    }

    /// <summary>An empty window yields no buckets and no volume.</summary>
    [Fact]
    public void Compute_AnEmptyWindow_YieldsNothing()
    {
        var profile = VolumeProfile.Compute([], 24);

        profile.Buckets.Should().BeEmpty();
        profile.TotalVolume.Should().Be(0m);
    }

    /// <summary>A bucket count below one rejects instead of clamping.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Compute_ABucketCountBelowOne_Throws(int bucketCount)
    {
        var act = () => VolumeProfile.Compute(
            [new Bar(Base, 9m, 11m, 8m, 10m, 1m)],
            bucketCount);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("bucketCount");
    }

    #endregion
}
