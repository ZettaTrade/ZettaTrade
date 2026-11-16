namespace ZettaTrade.UI.Tests.Charting;

using System;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;

/// <summary>
/// Pins for the swing-pivot detector (T-249): the triangular detection,
/// the plateau's earliest-bar tie-break, the dominance measurement (a
/// retest is a break, the series bounds are not), the min-strength
/// filter and the honest absences. Pure facts — no feed, no canvas.
/// </summary>
public sealed class SwingPivotsTests
{
    /// <summary>The base time the crafted series' bars ascend from (minute steps).</summary>
    private static readonly DateTimeOffset Base = new(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A bar whose whole range rides one price (open, high, low and close carry it).</summary>
    private static Bar FlatBar(int minute, decimal price) =>
        new(Base.AddMinutes(minute), price, price, price, price, 1m);

    /// <summary>
    /// The triangular golden: a rising-then-falling series answers one
    /// swing high at the peak and one swing low at each end's tail (the
    /// tails' arms are bounded by the series and the emptiness is not a
    /// break). The peak's strength answers zero here — it sits within
    /// two bars of both ends, so the beyond-arm stretch cannot open on
    /// either side.
    /// </summary>
    [Fact]
    public void Detect_TriangularSeries_FindsThePeakAndTheTails()
    {
        var prices = new[] { 1m, 2m, 3m, 2m, 1m };
        var bars = prices.Select((p, i) => FlatBar(i, p)).ToArray();

        var pivots = SwingPivots.Detect(bars, lookback: 2, minStrength: 0);

        pivots.Should().HaveCount(3);
        pivots.Should().ContainSingle(p => p.Kind == SwingPivotKind.High).Which
            .Should().Match<SwingPivot>(p => p.Index == 2 && p.Price == 3m);
        pivots.Where(p => p.Kind == SwingPivotKind.Low)
            .Should().SatisfyRespectively(
                p => p.Index.Should().Be(0),
                p => p.Index.Should().Be(4));
    }

    /// <summary>
    /// The plateau tie-break: two equal highs resolve to the EARLIEST bar
    /// — the left-strict arm forbids the follower, the right-inclusive arm
    /// admits the leader. Exactly one pivot per plateau.
    /// </summary>
    [Fact]
    public void Detect_PlateauOfEqualHighs_TakesTheEarliestBar()
    {
        var prices = new[] { 1m, 3m, 3m, 1m };
        var bars = prices.Select((p, i) => FlatBar(i, p)).ToArray();

        var highs = SwingPivots.Detect(bars, lookback: 1, minStrength: 0)
            .Where(p => p.Kind == SwingPivotKind.High)
            .ToArray();

        highs.Should().HaveCount(1);
        highs[0].Index.Should().Be(1, "the plateau's earliest bar wins the pivot");
        highs[0].Price.Should().Be(3m);
    }

    /// <summary>
    /// The dominance golden: the pivot's strength counts the bars BEYOND
    /// the arm on both sides until the first equal-or-higher extreme — an
    /// equal RETEST breaks the count exactly as a higher one does, while a
    /// strictly lower neighbor does not.
    /// </summary>
    [Fact]
    public void Detect_RetestAtThePivotPrice_BreaksTheDominance()
    {
        // Both peaks answer strength 1: the equal retest at index 5 caps
        // the first peak's right count at one bar, and the first peak's
        // own price caps the retest's left count the same way (the tails
        // run far enough that the beyond-arm stretch opens on both sides
        // of each peak).
        var retest = new[] { 1m, 2m, 5m, 2m, 1m, 5m, 1m, 2m, 1m };
        var lower = new[] { 1m, 2m, 5m, 2m, 1m, 4m, 1m, 1m, 1m };

        var retestPivots = SwingPivots.Detect(retest.Select((p, i) => FlatBar(i, p)).ToArray(), 1, 1)
            .Where(p => p.Kind == SwingPivotKind.High).ToArray();
        var lowerPivots = SwingPivots.Detect(lower.Select((p, i) => FlatBar(i, p)).ToArray(), 1, 1)
            .Where(p => p.Kind == SwingPivotKind.High).ToArray();

        retestPivots.Should().HaveCount(2);
        retestPivots[0].Index.Should().Be(2);
        retestPivots[0].Strength.Should().Be(1, "the equal retest breaks the right count at one bar");
        retestPivots[1].Index.Should().Be(5);
        retestPivots[1].Strength.Should().Be(1, "the first peak's own price breaks the left count at one bar");

        lowerPivots.Should().HaveCount(2);
        lowerPivots[0].Strength.Should().Be(1, "the left stretch bounds the min — the strictly lower run does not break");
        lowerPivots[1].Strength.Should().Be(1, "the first peak's own price (5) breaks this pivot's left count");
    }

    /// <summary>
    /// The series bounds stop the count without breaking it: a pivot's
    /// outer stretches run to the series' ends and the min answers the
    /// SHORTER side — the tail is never a break.
    /// </summary>
    [Fact]
    public void Detect_SeriesBounds_StopTheCountWithoutBreaking()
    {
        var prices = new[] { 1m, 2m, 5m, 2m, 1m, 1m, 1m };
        var bars = prices.Select((p, i) => FlatBar(i, p)).ToArray();

        var high = SwingPivots.Detect(bars, lookback: 1, minStrength: 0)
            .Single(p => p.Kind == SwingPivotKind.High);

        high.Index.Should().Be(2);
        high.Strength.Should().Be(1, "the left stretch stops at the series' head (one bar) and the min takes it");
    }

    /// <summary>
    /// The min-strength filter keeps only the pivots whose measured
    /// dominance reaches it; zero (or less) filters nothing.
    /// </summary>
    [Fact]
    public void Detect_MinStrengthFilter_KeepsTheDominantPivotsOnly()
    {
        var prices = new[] { 1m, 2m, 5m, 2m, 1m, 5m, 1m, 2m, 1m };
        var bars = prices.Select((p, i) => FlatBar(i, p)).ToArray();

        SwingPivots.Detect(bars, 1, 2).Should().BeEmpty("no pivot's dominance reaches two");
        SwingPivots.Detect(bars, 1, 1).Should().HaveCount(2, "only the two peaks' dominance answers one");
        SwingPivots.Detect(bars, 1, -5).Should().HaveCount(
            7,
            "a negative requirement clamps to no filter — the vacuous-strength candidates (the tail pivots) join too");
    }

    /// <summary>
    /// The honest absences: an empty series answers no pivots; a single
    /// bar (both arms vacuous) answers one high AND one low at strength
    /// zero — the detector never invents, never throws.
    /// </summary>
    [Fact]
    public void Detect_EmptyAndShortSeries_StayHonest()
    {
        SwingPivots.Detect([], 10, 0).Should().BeEmpty();

        var single = new[] { FlatBar(0, 42m) };
        var pivots = SwingPivots.Detect(single, 10, 0);

        pivots.Should().HaveCount(2, "the vacuous arms admit the bar as both a high and a low");
        pivots.Should().Contain(p => p.Kind == SwingPivotKind.High && p.Price == 42m && p.Strength == 0);
        pivots.Should().Contain(p => p.Kind == SwingPivotKind.Low && p.Price == 42m && p.Strength == 0);
    }

    /// <summary>
    /// The arm's legal floor: a lookback under one clamps to one — a
    /// zero arm would admit every noise wiggle, so the detector never
    /// runs without a neighborhood.
    /// </summary>
    [Fact]
    public void Detect_LookbackClampsToTheLegalFloor()
    {
        // With arm 1, the peak at index 2 dominates [2,2]; with a zero
        // arm it would still be a pivot but the tails would lose theirs —
        // the clamp keeps the two behaviors identical instead.
        var prices = new[] { 1m, 2m, 3m, 2m, 1m };
        var bars = prices.Select((p, i) => FlatBar(i, p)).ToArray();

        var clamped = SwingPivots.Detect(bars, lookback: 0, minStrength: 0);
        var explicitOne = SwingPivots.Detect(bars, lookback: 1, minStrength: 0);

        clamped.Should().Equal(explicitOne);
    }
}
