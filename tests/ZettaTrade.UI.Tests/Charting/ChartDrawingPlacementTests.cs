namespace ZettaTrade.UI.Tests.Charting;

using System;
using System.Linq;
using FluentAssertions;
using ZettaTrade.Charting;
using Xunit;

/// <summary>
/// The placement machine's goldens (T-091): the uniform press→move→release
/// gesture lands correct anchors per tool, a degenerate drag cancels
/// instead of placing, the tool STAYS armed across commits (several levels
/// in a row) and the resting mode ignores presses that belong to pan.
/// </summary>
public sealed class ChartDrawingPlacementTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = new(2026, 11, 1, 10, 30, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RestingMode_Press_IsIgnored()
    {
        var placement = new ChartDrawingPlacement();

        placement.OnPress(new ChartDrawingAnchor(T0, 100m))
            .Should().Be(ChartPlacementEvent.Ignored);
    }

    [Fact]
    public void TrendLine_ClickDrag_PlacesTwoAscendingCorners()
    {
        var placement = new ChartDrawingPlacement();
        placement.BeginTool(ChartDrawingTool.TrendLine);

        placement.OnPress(new ChartDrawingAnchor(T0, 100m))
            .Should().Be(ChartPlacementEvent.PreviewStarted);
        placement.Dragging.Should().BeTrue();

        placement.OnMove(new ChartDrawingAnchor(T1, 120m));
        placement.PreviewAnchor.Should().Be(new ChartDrawingAnchor(T1, 120m), "the previewed corner follows the pointer live.");

        placement.OnRelease(new ChartDrawingAnchor(T1, 120m)).Should()
            .BeOfType<ChartPlacementEvent.Placed>()
            .Which.Drawing.Anchors.Should().BeEquivalentTo([
                new ChartDrawingAnchor(T0, 100m),
                new ChartDrawingAnchor(T1, 120m),
            ]);
        placement.Dragging.Should().BeFalse();
    }

    [Fact]
    public void TrendLine_ZeroLengthDrag_CancelsInsteadOfPlacing()
    {
        var placement = new ChartDrawingPlacement();
        placement.BeginTool(ChartDrawingTool.TrendLine);

        placement.OnPress(new ChartDrawingAnchor(T0, 100m));
        placement.OnMove(new ChartDrawingAnchor(T0, 100m));

        placement.OnRelease(new ChartDrawingAnchor(T0, 100m))
            .Should().Be(ChartPlacementEvent.CancelledDegenerate, "a duplicated-corner drawing is a placement bug, never a drawing.");
    }

    [Fact]
    public void FibTool_ClassifiesThroughTheKind()
    {
        var placement = new ChartDrawingPlacement();
        placement.BeginTool(ChartDrawingTool.FibRetracement);

        placement.OnPress(new ChartDrawingAnchor(T0, 200m));
        placement.OnMove(new ChartDrawingAnchor(T1, 100m));

        placement.OnRelease(new ChartDrawingAnchor(T1, 100m)).Should()
            .BeOfType<ChartPlacementEvent.Placed>()
            .Which.Drawing.Kind.Should().Be(ChartDrawingKind.FibRetracement);
    }

    [Fact]
    public void HorizontalLevel_CommitsItsSingleAnchorFromThePress()
    {
        var placement = new ChartDrawingPlacement();
        placement.BeginTool(ChartDrawingTool.HorizontalLevel);

        placement.OnPress(new ChartDrawingAnchor(T0, 100m));
        placement.OnMove(new ChartDrawingAnchor(T1, 105m));

        placement.OnRelease(new ChartDrawingAnchor(T1, 105m)).Should()
            .BeOfType<ChartPlacementEvent.Placed>()
            .Which.Drawing.Anchors.Should().ContainSingle("a level rides its press anchor only.")
            .Which.Should().Be(new ChartDrawingAnchor(T0, 100m));
    }

    [Fact]
    public void ToolStaysArmed_AfterCommit_SecondLevelPlaces()
    {
        var placement = new ChartDrawingPlacement();
        placement.BeginTool(ChartDrawingTool.HorizontalLevel);

        placement.OnPress(new ChartDrawingAnchor(T0, 100m));
        placement.OnRelease(new ChartDrawingAnchor(T0, 100m))
            .Should().BeOfType<ChartPlacementEvent.Placed>();

        placement.Tool.Should().Be(ChartDrawingTool.HorizontalLevel, "the trader draws several levels in a row.");

        placement.OnPress(new ChartDrawingAnchor(T1, 110m));
        placement.OnRelease(new ChartDrawingAnchor(T1, 110m))
            .Should().BeOfType<ChartPlacementEvent.Placed>()
            .Which.Drawing.Anchors.Should().ContainSingle()
            .Which.Should().Be(new ChartDrawingAnchor(T1, 110m));
    }

    [Fact]
    public void EscapeCancel_ClearsTheGesture_StaysArmed()
    {
        var placement = new ChartDrawingPlacement();
        placement.BeginTool(ChartDrawingTool.TrendLine);

        placement.OnPress(new ChartDrawingAnchor(T0, 100m));
        placement.OnMove(new ChartDrawingAnchor(T1, 120m));
        placement.Cancel();

        placement.Dragging.Should().BeFalse();
        placement.PreviewAnchor.Should().BeNull();
        placement.Tool.Should().Be(ChartDrawingTool.TrendLine, "Escape discards the gesture, not the armed tool.");

        placement.OnRelease(new ChartDrawingAnchor(T1, 120m))
            .Should().Be(ChartPlacementEvent.Ignored, "a release after a cancelled gesture is noise.");
    }

    [Fact]
    public void ReleaseWithoutDrag_IsIgnored()
    {
        var placement = new ChartDrawingPlacement();
        placement.BeginTool(ChartDrawingTool.TrendLine);

        placement.OnRelease(new ChartDrawingAnchor(T1, 120m)).Should().Be(ChartPlacementEvent.Ignored);
    }
}
