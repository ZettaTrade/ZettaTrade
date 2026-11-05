namespace ZettaTrade.UI.Tests.Charting;

using FluentAssertions;
using Xunit;
using ZettaTrade.UI.Charting;

/// <summary>
/// Pins for the chart pan window math: the visible span (offset-shifted,
/// right-anchored, honestly clipped at the series' first bar), the offset
/// clamp (no wrap right of the newest bar, never an empty plot) and the
/// older-history fetch threshold (the window's left edge standing on the
/// loaded series' head). Pure facts — the canvas itself is UIA-invisible.
/// </summary>
public sealed class ChartPanWindowTests
{
    #region Visible Span

    [Fact]
    public void VisibleSpan_AnUnpannedWideSeries_AnchorsTheWindowAtTheSeriesEnd()
    {
        // 200 loaded, a 100-bar window: the last 100 bars paint.
        var (start, end) = ChartPanWindow.VisibleSpan(200, 0, 100);

        start.Should().Be(100);
        end.Should().Be(200);
    }

    [Fact]
    public void VisibleSpan_AStarvedSeries_PaintsEverythingItHas()
    {
        // Fewer loaded bars than the window wants: the whole series paints.
        var (start, end) = ChartPanWindow.VisibleSpan(50, 0, 100);

        start.Should().Be(0);
        end.Should().Be(50);
    }

    [Theory]
    [InlineData(200, 100, 30, 70, 170)]
    [InlineData(200, 100, 1, 99, 199)]
    [InlineData(400, 200, 150, 50, 250)]
    public void VisibleSpan_APanOffsetShiftsTheWindowTowardOlderBars(
        int barCount,
        int visibleBars,
        int panOffset,
        int expectedStart,
        int expectedEnd)
    {
        var (start, end) = ChartPanWindow.VisibleSpan(barCount, panOffset, visibleBars);

        start.Should().Be(expectedStart);
        end.Should().Be(expectedEnd);
    }

    [Fact]
    public void VisibleSpan_PanningPastTheSeriesHead_ClipsAndShrinksTheSlice()
    {
        // A 200-bar window over 200 loaded bars, panned by one: the slice
        // loses its newest bar and hangs honestly short — empty space grows
        // on the left (the capped-window look), no wrap, no invented bars.
        var (start, end) = ChartPanWindow.VisibleSpan(200, 1, 200);

        start.Should().Be(0);
        end.Should().Be(199);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void VisibleSpan_NoBars_YieldsAnEmptySpan(int barCount)
    {
        ChartPanWindow.VisibleSpan(barCount, 0, 100).Should().Be((0, 0));
    }

    [Fact]
    public void VisibleSpan_ADegenerateWindowPaintsAtLeastOneBar()
    {
        var (start, end) = ChartPanWindow.VisibleSpan(10, 0, 0);

        start.Should().Be(9);
        end.Should().Be(10);
    }

    #endregion

    #region Offset Clamp

    [Fact]
    public void ClampOffset_PinsAtZeroOnTheNewestEdge()
    {
        ChartPanWindow.ClampOffset(50, -3).Should().Be(0);
    }

    [Fact]
    public void ClampOffset_KeepsAtLeastOneVisibleBar()
    {
        // Offset = count would paint an empty plot: it clamps to count - 1.
        ChartPanWindow.ClampOffset(50, 1234).Should().Be(49);
    }

    [Fact]
    public void ClampOffset_KeepsAnInRequestSpan()
    {
        ChartPanWindow.ClampOffset(50, 12).Should().Be(12);
    }

    [Theory]
    [InlineData(0, 100, 0)]
    [InlineData(0, -3, 0)]
    public void ClampOffset_NoBars_ClampsToZero(int barCount, int requestedOffset, int expected)
    {
        ChartPanWindow.ClampOffset(barCount, requestedOffset).Should().Be(expected);
    }

    #endregion

    #region Fetch Threshold

    [Theory]
    [InlineData(200, 100, 99, false)]
    [InlineData(200, 100, 100, true)]
    [InlineData(200, 100, 150, true)]
    [InlineData(50, 100, 0, true)]
    public void OlderFetchThresholdReached_IsTrueWhenTheLeftEdgeStandsOnTheSeriesHead(
        int barCount,
        int visibleBars,
        int panOffset,
        bool expected)
    {
        ChartPanWindow.OlderFetchThresholdReached(barCount, panOffset, visibleBars)
            .Should().Be(expected);
    }

    [Fact]
    public void OlderFetchThresholdReached_TracksTheLoadedSeriesGrowth()
    {
        // A page of 200 older candles just prepended: the same offset now
        // stands clear of the head — the threshold releases.
        ChartPanWindow.OlderFetchThresholdReached(200, 50, 200).Should().BeTrue();
        ChartPanWindow.OlderFetchThresholdReached(400, 50, 200).Should().BeFalse();
    }

    #endregion

    #region Drag Slots

    [Fact]
    public void SlotsForDrag_OnePaintedBarWidth_MovesExactlyOneSlot()
    {
        // The parity contract (BUG-048): the pitch measured the same way
        // the draw pass paints with (the plot width, not the raw window
        // bounds) — dragging exactly one painted bar translates one slot.
        var paintedPitch = ChartBarLayout.BarPitch(921.5, 100, 40);

        ChartPanWindow.SlotsForDrag(paintedPitch, paintedPitch).Should().Be(1);
        ChartPanWindow.SlotsForDrag(10 * paintedPitch, paintedPitch).Should().Be(10);
    }

    [Fact]
    public void SlotsForDrag_AHundredPaintedBarsDrag_PacesWithThePaintedPitch()
    {
        // The witnessed regression: a 1000 px window with a 250 px price
        // gutter paints 100 bars at a 7.5 px pitch, while the old pacing
        // derived 10 px per "bar" from the raw window size. A drag as long
        // as 100 painted bars must move 100 slots — the old derivation
        // ate a quarter of the travel (75 slots for the same drag).
        var paintedPitch = ChartBarLayout.BarPitch(750, 100, 40);

        ChartPanWindow.SlotsForDrag(100 * paintedPitch, paintedPitch).Should().Be(100);
        ChartPanWindow.SlotsForDrag(100 * paintedPitch, 10.0).Should().Be(75);
    }

    [Theory]
    [InlineData(0.49, 0)]
    [InlineData(-0.49, 0)]
    [InlineData(1.5, 2)]
    [InlineData(2.5, 2)]
    [InlineData(-0.5, 0)]
    public void SlotsForDrag_SubBarJitterAndTies_FollowTheRoundedBarMath(
        double draggedDistance,
        int expectedSlots)
    {
        // A drag shorter than half a painted bar parks the window (the
        // anchor makes the wave of move events accumulate), ties round to
        // even, and the sign carries: dragging toward the newest bar
        // un-panns.
        ChartPanWindow.SlotsForDrag(draggedDistance, 1.0).Should().Be(expectedSlots);
    }

    #endregion
}
