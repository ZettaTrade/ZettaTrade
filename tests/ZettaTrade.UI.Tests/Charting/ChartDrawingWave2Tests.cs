namespace ZettaTrade.UI.Tests.Charting;

using System;
using System.Collections.Generic;
using FluentAssertions;
using Xunit;
using ZettaTrade.Charting;

/// <summary>
/// The wave-2 kinds' goldens (T-200): the zone, the text annotation and the
/// measure ruler ride the same placement machine as wave 1 — the zone and
/// the ruler frame two corners, the annotation commits its single press
/// anchor, and every kind validates its own anchor count.
/// </summary>
public sealed class ChartDrawingWave2Tests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = new(2026, 11, 1, 10, 30, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AnchorCount_Wave2Kinds_PinTheToolArity()
    {
        ChartDrawing.AnchorCount(ChartDrawingKind.Zone).Should().Be(2, "the zone bounds a band with two corners.");
        ChartDrawing.AnchorCount(ChartDrawingKind.Text).Should().Be(1, "the annotation pins one spot.");
        ChartDrawing.AnchorCount(ChartDrawingKind.Measure).Should().Be(2, "the ruler frames the move with two ends.");
    }

    [Fact]
    public void ZoneTool_ClickDrag_PlacesTwoAscendingCorners()
    {
        var placement = new ChartDrawingPlacement();
        placement.BeginTool(ChartDrawingTool.Zone);

        placement.OnPress(new ChartDrawingAnchor(T0, 100m))
            .Should().Be(ChartPlacementEvent.PreviewStarted);

        placement.OnMove(new ChartDrawingAnchor(T1, 120m));
        placement.PreviewAnchor.Should().Be(new ChartDrawingAnchor(T1, 120m));

        var placed = placement.OnRelease(new ChartDrawingAnchor(T1, 120m))
            .Should().BeOfType<ChartPlacementEvent.Placed>().Which.Drawing;

        placed.Kind.Should().Be(ChartDrawingKind.Zone);
        placed.Anchors.Should().BeEquivalentTo([
            new ChartDrawingAnchor(T0, 100m),
            new ChartDrawingAnchor(T1, 120m),
        ]);
    }

    [Fact]
    public void ZoneTool_ZeroAreaDrag_CancelsInsteadOfPlacing()
    {
        var placement = new ChartDrawingPlacement();
        placement.BeginTool(ChartDrawingTool.Zone);

        placement.OnPress(new ChartDrawingAnchor(T0, 100m));
        placement.OnRelease(new ChartDrawingAnchor(T0, 100m))
            .Should().Be(ChartPlacementEvent.CancelledDegenerate,
                "a one-corner zone is a placement bug, never a drawing.");
    }

    [Fact]
    public void TextTool_CommitsItsSingleAnchorFromThePress()
    {
        var placement = new ChartDrawingPlacement();
        placement.BeginTool(ChartDrawingTool.Text);

        placement.OnPress(new ChartDrawingAnchor(T0, 100m));
        placement.OnMove(new ChartDrawingAnchor(T1, 105m));

        placement.PreviewAnchors.Should().ContainSingle(
            "the annotation previews its press anchor alone, no trailing corner.");

        placement.OnRelease(new ChartDrawingAnchor(T1, 105m))
            .Should().BeOfType<ChartPlacementEvent.Placed>()
            .Which.Drawing.Anchors.Should().ContainSingle()
            .Which.Should().Be(new ChartDrawingAnchor(T0, 100m));
    }

    [Fact]
    public void TextTool_ReleaseOnAnotherSpot_StillCommitsThePressAnchor()
    {
        var placement = new ChartDrawingPlacement();
        placement.BeginTool(ChartDrawingTool.Text);

        placement.OnPress(new ChartDrawingAnchor(T0, 99m));
        placement.OnRelease(new ChartDrawingAnchor(T1, 120m))
            .Should().BeOfType<ChartPlacementEvent.Placed>(
                "the annotation is a spot, not a drag: the release lands it wherever the pointer went.")
            .Which.Drawing.Anchors.Should().ContainSingle()
            .Which.Should().Be(new ChartDrawingAnchor(T0, 99m));
    }

    [Fact]
    public void MeasureTool_PlacesTheFramedEnds()
    {
        var placement = new ChartDrawingPlacement();
        placement.BeginTool(ChartDrawingTool.Measure);

        placement.OnPress(new ChartDrawingAnchor(T0, 100m));
        placement.OnRelease(new ChartDrawingAnchor(T1, 105m))
            .Should().BeOfType<ChartPlacementEvent.Placed>()
            .Which.Drawing.Kind.Should().Be(ChartDrawingKind.Measure);
    }

    [Fact]
    public void Wave2Kinds_ValidateTheirAnchorCounts()
    {
        var twoAnchors = new[]
        {
            new ChartDrawingAnchor(T0, 100m),
            new ChartDrawingAnchor(T1, 105m),
        };
        var oneAnchor = new[] { new ChartDrawingAnchor(T0, 100m) };

        ChartDrawing.Validate(ChartDrawingKind.Zone, twoAnchors);
        ChartDrawing.Validate(ChartDrawingKind.Measure, twoAnchors);
        ChartDrawing.Validate(ChartDrawingKind.Text, oneAnchor);

        var act = () => ChartDrawing.Validate(ChartDrawingKind.Text, twoAnchors);
        act.Should().Throw<ArgumentException>("a second anchor never happened for an annotation.");
    }

    [Fact]
    public void TextDrawing_CarriesItsString_OtherKindsStayNull()
    {
        var annotation = new ChartDrawing
        {
            Kind = ChartDrawingKind.Text,
            Anchors = [new ChartDrawingAnchor(T0, 100m)],
            Text = "retest of supply",
        };
        annotation.Text.Should().Be("retest of supply");

        var zone = new ChartDrawing
        {
            Kind = ChartDrawingKind.Zone,
            Anchors = [new ChartDrawingAnchor(T0, 100m), new ChartDrawingAnchor(T1, 110m)],
        };
        zone.Text.Should().BeNull("only the annotation kind carries a string.");

        var ruler = new ChartDrawing
        {
            Kind = ChartDrawingKind.Measure,
            Anchors = [new ChartDrawingAnchor(T0, 100m), new ChartDrawingAnchor(T1, 105m)],
        };
        ruler.Text.Should().BeNull();
    }

    [Fact]
    public void Wave2Tools_ToKind_MapsLoudly()
    {
        ChartDrawingTool.Zone.ToKind().Should().Be(ChartDrawingKind.Zone);
        ChartDrawingTool.Text.ToKind().Should().Be(ChartDrawingKind.Text);
        ChartDrawingTool.Measure.ToKind().Should().Be(ChartDrawingKind.Measure);
    }
}
