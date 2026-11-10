namespace ZettaTrade.App.Tests.Services;

using System;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;

/// <summary>
/// The detach geometry (T-103): the placement stores FRACTIONS of the
/// display's working area, so a DPI or resolution shift between sessions
/// changes nothing but the multipliers — the snapshot/compose pair is
/// lossless on the same display, garbage folds into the sane shape, and
/// the restore lookup finds the stored display exactly, remaps by the
/// stored absolute point when the monitor changed, or answers null (the
/// caller centers).
/// </summary>
public sealed class PanelWindowGeometryTests
{
    /// <summary>A full-HD working area at 150% density.</summary>
    private static readonly PanelDisplaySample Display = new(
        0, 0, 1920, 1080, 1.5);

    /// <summary>The geometry's shipped fallback extent (three quarters of the axis).</summary>
    private const double DefaultExtent = 0.75;

    /// <summary>The geometry's minimum extent clamp (five percent of the axis).</summary>
    private const double MinExtent = 0.05;

    [Fact]
    public void Snapshot_ComposesBack_OnTheSameDisplay()
    {
        var placement = PanelWindowGeometry.Snapshot(
            Display,
            positionLeftPixel: 192,
            positionTopPixel: 108,
            sizeWidthPixel: 960,
            sizeHeightPixel: 540,
            isMaximized: false);

        placement.IsMaximized.Should().BeFalse();
        placement.LeftFraction.Should().BeApproximately(0.10, 1e-9);
        placement.TopFraction.Should().BeApproximately(0.10, 1e-9);
        placement.WidthFraction.Should().BeApproximately(0.50, 1e-9);
        placement.HeightFraction.Should().BeApproximately(0.50, 1e-9);

        var box = PanelWindowGeometry.Compose(Display, placement);
        box.Should().NotBeNull();
        box!.Value.PositionLeftPixel.Should().Be(192);
        box.Value.PositionTopPixel.Should().Be(108);
        box.Value.WidthLogical.Should().BeApproximately(640, 1e-9);
        box.Value.HeightLogical.Should().BeApproximately(360, 1e-9);
    }

    [Fact]
    public void AShiftedDisplay_ComposesTheSameFractions_Sane()
    {
        // The DPI canon (T-103): the SAME fractions compose a sane window
        // against whatever area lives under the stored key now — here a
        // 4K area at 200% keeps the relative box, not a stale pixel box.
        var shifted = new PanelDisplaySample(0, 0, 3840, 2160, 2.0);
        var placement = new PanelWindowPlacement(false, 0.10, 0.10, 0.50, 0.50);

        var box = PanelWindowGeometry.Compose(shifted, placement);

        box.Should().NotBeNull();
        box!.Value.PositionLeftPixel.Should().Be(384);
        box.Value.PositionTopPixel.Should().Be(216);
        box.Value.WidthLogical.Should().BeApproximately(960, 1e-9);
        box.Value.HeightLogical.Should().BeApproximately(540, 1e-9);
    }

    [Fact]
    public void Snapshot_OnAnUnusableDisplay_AnswersTheCenteredDefault()
    {
        var dead = new PanelDisplaySample(0, 0, 0, 0, 1.0);

        var placement = PanelWindowGeometry.Snapshot(
            dead, 500, 300, 800, 600, isMaximized: false);

        placement.IsMaximized.Should().BeFalse();
        placement.LeftFraction.Should().Be(0);
        placement.TopFraction.Should().Be(0);
        placement.WidthFraction.Should().BeApproximately(DefaultExtent, 1e-9);
        placement.HeightFraction.Should().BeApproximately(DefaultExtent, 1e-9);
    }

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    [InlineData(double.NegativeInfinity, 0)]
    [InlineData(1.7, 1)]
    [InlineData(-0.3, 0)]
    [InlineData(0.42, 0.42)]
    public void ClampFraction_FoldsTheGarbageIntoTheShape(double garbage, double expected)
    {
        PanelWindowPlacement.ClampFraction(garbage).Should().BeApproximately(expected, 1e-9);
    }

    [Theory]
    [InlineData(double.NaN, DefaultExtent)]
    [InlineData(0.01, MinExtent)]
    [InlineData(2.5, 1)]
    [InlineData(0.6, 0.6)]
    public void ClampExtent_FoldsTheGarbageIntoTheShape(double garbage, double expected)
    {
        PanelWindowPlacement.ClampExtent(garbage).Should().BeApproximately(expected, 1e-9);
    }

    [Fact]
    public void Compose_OnAnUnusableDisplay_AnswersNull()
    {
        var dead = new PanelDisplaySample(0, 0, 0, 0, 1.0);

        PanelWindowGeometry.Compose(
            dead,
            new PanelWindowPlacement(false, 0.1, 0.1, 0.5, 0.5)).Should().BeNull();
    }

    [Fact]
    public void FindRestorable_MatchesTheExactSample()
    {
        var entry = new DetachedPanelEntry(
            PanelDetachId.Chart,
            Display,
            new PanelWindowPlacement(false, 0.1, 0.2, 0.5, 0.5));

        var found = PanelWindowGeometry.FindRestorable([Display], entry);

        found.Should().NotBeNull();
        found!.Value.Display.Should().Be(Display);
        found.Value.PositionLeftPixel.Should().Be(192);
        found.Value.PositionTopPixel.Should().Be(216);
        found.Value.WidthLogical.Should().BeApproximately(640, 1e-9);
        found.Value.HeightLogical.Should().BeApproximately(360, 1e-9);
    }

    [Fact]
    public void FindRestorable_RemapsByTheStoredAbsolutePoint_WhenTheMonitorMoved()
    {
        // The stored display is gone; its absolute point now lives on the
        // second monitor, which inherits the panel (the record is relative
        // on purpose). The stored fractions (0.9, 0.5) name the absolute
        // point (900, 400) of the stored area — the moved display whose
        // area carries it takes over with its own anchor.
        var stored = new PanelDisplaySample(0, 0, 1000, 800, 1.0);
        var moved = new PanelDisplaySample(400, 0, 1000, 800, 1.0);
        var entry = new DetachedPanelEntry(
            PanelDetachId.Book,
            stored,
            new PanelWindowPlacement(false, 0.9, 0.5, 0.4, 0.4));

        var found = PanelWindowGeometry.FindRestorable([moved], entry);

        found.Should().NotBeNull();
        found!.Value.Display.Should().Be(moved);
        found.Value.PositionLeftPixel.Should().Be(1300);
        found.Value.PositionTopPixel.Should().Be(400);
    }

    [Fact]
    public void FindRestorable_NoDisplayCarriesTheStoredPoint_AnswersNull()
    {
        var stored = new PanelDisplaySample(0, 0, 1000, 800, 1.0);
        var elsewhere = new PanelDisplaySample(5000, 5000, 1000, 800, 1.0);
        var entry = new DetachedPanelEntry(
            PanelDetachId.Assistant,
            stored,
            new PanelWindowPlacement(false, 0.5, 0.5, 0.4, 0.4));

        PanelWindowGeometry.FindRestorable([elsewhere], entry).Should().BeNull();
    }
}
