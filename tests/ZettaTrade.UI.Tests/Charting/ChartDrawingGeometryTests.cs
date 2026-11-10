namespace ZettaTrade.UI.Tests.Charting;

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using ZettaTrade.Charting;
using Xunit;

/// <summary>
/// The drawings layer's geometry goldens (T-091): the Fibonacci ladder's
/// exact prices on pinned anchors (both directions), the segment-distance
/// facts the hit zone rides, and the anchor validation rules the placement
/// and edit flows answer to.
/// </summary>
public sealed class ChartDrawingGeometryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = new(2026, 11, 1, 12, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AnchorCount_Kind_PinsTheToolArity()
    {
        ChartDrawing.AnchorCount(ChartDrawingKind.TrendLine).Should().Be(2);
        ChartDrawing.AnchorCount(ChartDrawingKind.HorizontalLevel).Should().Be(1);
        ChartDrawing.AnchorCount(ChartDrawingKind.FibRetracement).Should().Be(2);
    }

    [Fact]
    public void FibonacciLevels_AnchorPair_PinsTheLadderExactly()
    {
        var a = new ChartDrawingAnchor(T0, 100.00m);
        var b = new ChartDrawingAnchor(T1, 200.00m);

        var levels = ChartDrawingGeometry.FibonacciLevels(a, b);

        levels.Select(l => l.Ratio).Should().BeEquivalentTo(ChartDrawingGeometry.FibonacciRatios);
        levels.Where(l => l.Ratio == 0.000m).Single().Price.Should().Be(200.00m);
        levels.Where(l => l.Ratio == 1.000m).Single().Price.Should().Be(100.00m);
        levels.Where(l => l.Ratio == 0.500m).Single().Price.Should().Be(150.00m);
        levels.Where(l => l.Ratio == 0.618m).Single().Price.Should().Be(200m - 100m * 0.618m);
        levels.Where(l => l.Ratio == 0.236m).Single().Price.Should().Be(200m - 100m * 0.236m);
    }

    [Fact]
    public void FibonacciLevels_FallingSwing_MirrorsTheLadder()
    {
        var a = new ChartDrawingAnchor(T0, 150.00m);
        var b = new ChartDrawingAnchor(T1, 70.00m);

        var levels = ChartDrawingGeometry.FibonacciLevels(a, b);

        levels.Where(l => l.Ratio == 0.000m).Single().Price.Should().Be(70.00m);
        levels.Where(l => l.Ratio == 1.000m).Single().Price.Should().Be(150.00m);
        levels.Where(l => l.Ratio == 0.382m).Single().Price.Should().Be(70m + 80m * 0.382m);
    }

    [Fact]
    public void FibonacciLevels_ConstantPriceSpan_EveryLevelCollapses()
    {
        var a = new ChartDrawingAnchor(T0, 100.00m);
        var b = new ChartDrawingAnchor(T1, 100.00m);

        var levels = ChartDrawingGeometry.FibonacciLevels(a, b);

        levels.All(l => l.Price == 100.00m).Should().BeTrue("a zero price span puts every ratio at the same price.");
    }

    [Fact]
    public void DistanceToSegment_PointOnSegment_IsZero()
    {
        var distance = ChartDrawingGeometry.DistanceToSegment(
            px: 50, py: 50, ax: 0, ay: 0, bx: 100, by: 100);

        distance.Should().BeApproximately(0.0, 1e-9);
    }

    [Fact]
    public void DistanceToSegment_PerpendicularFoot_TracksTheNearestPoint()
    {
        var distance = ChartDrawingGeometry.DistanceToSegment(
            px: 0, py: 30, ax: 0, ay: 0, bx: 100, by: 0);

        distance.Should().BeApproximately(30.0, 1e-9);
    }

    [Fact]
    public void DistanceToSegment_PointBeyondAnEnd_DistanceToEndpoint()
    {
        var distance = ChartDrawingGeometry.DistanceToSegment(
            px: 150, py: 0, ax: 0, ay: 0, bx: 100, by: 0);

        distance.Should().BeApproximately(50.0, 1e-9);
    }

    [Fact]
    public void DistanceToSegment_DegenerateSegment_DistanceToPoint()
    {
        var distance = ChartDrawingGeometry.DistanceToSegment(
            px: 3, py: 4, ax: 0, ay: 0, bx: 0, by: 0);

        distance.Should().BeApproximately(5.0, 1e-9);
    }

    [Fact]
    public void Validate_DescendingAnchors_ThrowsLoudly()
    {
        var anchors = new[]
        {
            new ChartDrawingAnchor(T1, 100m),
            new ChartDrawingAnchor(T0, 90m),
        };

        var act = () => ChartDrawing.Validate(anchors);

        act.Should().Throw<ArgumentException>("anchors must stay ascending by time, a flipped segment reads backwards.");
    }

    [Fact]
    public void Validate_DuplicateAnchors_ThrowsLoudly()
    {
        var anchors = new[]
        {
            new ChartDrawingAnchor(T0, 100m),
            new ChartDrawingAnchor(T0, 100m),
        };

        var act = () => ChartDrawing.Validate(anchors);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Validate_AscendingAnchors_Passes()
    {
        var anchors = new[]
        {
            new ChartDrawingAnchor(T0, 100m),
            new ChartDrawingAnchor(T1, 120m),
        };

        var act = () => ChartDrawing.Validate(anchors);

        act.Should().NotThrow();
    }
}
