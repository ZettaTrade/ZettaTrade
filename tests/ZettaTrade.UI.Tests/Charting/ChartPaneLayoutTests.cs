namespace ZettaTrade.UI.Tests.Charting;

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using FluentAssertions;
using Xunit;
using ZettaTrade.UI.Charting;

/// <summary>
/// Pins for the oscillator pane geometry (T-199): the pure split of the
/// drawable height between the main pane and the oscillator sub-panes —
/// the stored heights clamp into the usable band, the fit pass keeps the
/// main pane's floor, and the split lays the stack top-to-bottom under the
/// main pane. Pure facts — the canvas itself is UIA-invisible.
/// </summary>
public sealed class ChartPaneLayoutTests
{
    #region Resolve Heights

    /// <summary>A stored null rides the layout default; the in-band values pass through untouched (a stack that fits the main floor's budget is never re-scaled).</summary>
    [Fact]
    public void ResolveHeights_ANullRidesTheDefault_AndInBandValuesPassThrough()
    {
        var heights = ChartPaneLayout.ResolveHeights(
            1200,
            [null, 200, ChartPaneLayout.PaneMinHeightPx, ChartPaneLayout.PaneMaxHeightPx]);

        heights.Should().Equal(
            ChartPaneLayout.PaneDefaultHeightPx,
            200,
            ChartPaneLayout.PaneMinHeightPx,
            ChartPaneLayout.PaneMaxHeightPx);
    }

    /// <summary>An out-of-band stored height clamps on resolve — the stored truth is never an out-of-band geometry.</summary>
    [Fact]
    public void ResolveHeights_AnOutOfBandHeight_ClampsIntoTheUsableBand()
    {
        var heights = ChartPaneLayout.ResolveHeights(1000, [5, 10_000]);

        heights.Should().Equal(ChartPaneLayout.PaneMinHeightPx, ChartPaneLayout.PaneMaxHeightPx);
    }

    /// <summary>
    /// The fit pass: panes that overdraw the main pane's floor shrink
    /// proportionally to fill the budget exactly, never below the pane
    /// floor — a window sized for the floors alone leaves the clamps
    /// standing and the main pane yields.
    /// </summary>
    [Fact]
    public void ResolveHeights_AnOverdrawingStack_ShieldsTheMainFloorByProportionalFit()
    {
        // 600 px drawable: the main floor leaves a 440 px pane budget; two
        // 300 px panes overdraw it by 160 and the proportional scale lands
        // both panes on the budget's half.
        var heights = ChartPaneLayout.ResolveHeights(600, [300, 300]);

        heights[0].Should().BeApproximately(220, 0.01, "each pane lands on the budget's proportional half");
        heights[1].Should().BeApproximately(220, 0.01);
        heights.Sum().Should().BeApproximately(440, 0.01, "the fitted stack fills the main floor's budget exactly");
    }

    /// <summary>A window too small for even the floors keeps the floor clamps standing (the main pane gives way, never a negative rect).</summary>
    [Fact]
    public void ResolveHeights_AWindowSmallerThanTheFloors_KeepsTheFloorClamps()
    {
        var heights = ChartPaneLayout.ResolveHeights(120, [300, 300]);

        heights.Should().OnlyContain(height => height == ChartPaneLayout.PaneMinHeightPx);
    }

    /// <summary>No panes resolves nothing — the split is the historical single-pane shape.</summary>
    [Fact]
    public void ResolveHeights_NoPanes_IsEmpty()
    {
        ChartPaneLayout.ResolveHeights(600, Array.Empty<double?>()).Should().BeEmpty();
    }

    #endregion

    #region Split

    /// <summary>The split: the main pane owns the top, the panes stack directly under it in order.</summary>
    [Fact]
    public void Split_StacksThePanesUnderTheMainPane_InOrder()
    {
        var split = ChartPaneLayout.Split(1200, 600, [140, 80]);

        split.Should().HaveCount(3);
        split[0].Should().Be(new Rect(0, 0, 1200, 380), "the main pane owns the drawable's top");
        split[1].Should().Be(new Rect(0, 380, 1200, 140));
        split[2].Should().Be(new Rect(0, 520, 1200, 80));
    }

    /// <summary>No panes: the split is the single full-height main rect — the historical layout, bit for bit.</summary>
    [Fact]
    public void Split_WithoutPanes_IsTheSingleMainRect()
    {
        var split = ChartPaneLayout.Split(1200, 600, []);

        split.Should().ContainSingle().Which.Should().Be(new Rect(0, 0, 1200, 600));
    }

    /// <summary>A stack taller than the drawable (the degenerate small-window case) clamps the main pane to zero and runs the stack past the edge — the canvas clip holds, no negative rect is produced.</summary>
    [Fact]
    public void Split_AStackTallerThanTheDrawable_ClampsTheMainPaneToZero()
    {
        var split = ChartPaneLayout.Split(1200, 60, [200, 200]);

        split[0].Height.Should().Be(0);
        split[1].Should().Be(new Rect(0, 0, 1200, 200));
        split[2].Should().Be(new Rect(0, 200, 1200, 200));
    }

    #endregion
}
