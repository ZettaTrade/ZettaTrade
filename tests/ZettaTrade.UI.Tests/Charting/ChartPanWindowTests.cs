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
}
