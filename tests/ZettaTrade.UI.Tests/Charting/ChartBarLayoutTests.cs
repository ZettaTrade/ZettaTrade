namespace ZettaTrade.UI.Tests.Charting;

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.Core.Entities;
using ZettaTrade.UI.Charting;

/// <summary>
/// Pins for the chart's bar-slot layout math: the pitch cap (a starved
/// window keeps terminal-normal candles), the right-anchored slot placement
/// (the last bar rides the plot's right edge) and the time-to-slot mapping
/// (grid lines and labels ride the candles' own slots). Pure facts — the
/// canvas itself is UIA-invisible.
/// </summary>
public sealed class ChartBarLayoutTests
{
    /// <summary>Plot width used across the facts (pixels).</summary>
    private const double PlotWidth = 1200.0;

    /// <summary>The capped pitch (pixels).</summary>
    private const double CappedPitch = 40.0;

    #region Bar Pitch

    [Theory]
    [InlineData(1200.0, 100, 12.0)]
    [InlineData(593.0, 42, 14.1190476)]
    public void BarPitch_AFullWindow_TakesThePlotWidthPerBar(
        double plotWidth,
        int barCount,
        double expected)
    {
        ChartBarLayout.BarPitch(plotWidth, barCount, CappedPitch)
            .Should().BeApproximately(expected, 1e-6, "below the cap the pitch is the window-driven one");
    }

    [Theory]
    [InlineData(1000.0, 10)]
    [InlineData(1200.0, 20)]
    [InlineData(300.0, 2)]
    public void BarPitch_AStarvedWindowCapsAtTheMax(
        double plotWidth,
        int barCount)
    {
        ChartBarLayout.BarPitch(plotWidth, barCount, CappedPitch)
            .Should().Be(CappedPitch, "one bar never stretches into a full-width wall");
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-5, 10)]
    [InlineData(1200.0, 0)]
    [InlineData(1200.0, -3)]
    public void BarPitch_ADegenerateInputFallsBackToTheCap(
        double plotWidth,
        int barCount)
    {
        ChartBarLayout.BarPitch(plotWidth, barCount, CappedPitch)
            .Should().Be(CappedPitch, "the draw pass never feeds it, but the fallback stays anchored");
    }

    #endregion

    #region Slot Placement

    /// <summary>
    /// The slots hang off the plot's RIGHT edge: the last bar's right slot
    /// boundary sits exactly on the plot edge, so a starved window's empty
    /// space grows left.
    /// </summary>
    [Fact]
    public void BarSlots_AnchorTheLastBarToThePlotRightEdge()
    {
        // Ten capped slots hang off the right edge at 1200: the first
        // starts at 800, the last ends ON the edge.
        ChartBarLayout.BarSlotLeftX(PlotWidth, CappedPitch, 10, 0)
            .Should().Be(800.0);
        ChartBarLayout.BarSlotLeftX(PlotWidth, CappedPitch, 10, 9)
            .Should().Be(1160.0);
        ChartBarLayout.BarCenterX(PlotWidth, CappedPitch, 10, 9)
            .Should().Be(1180.0);
    }

    /// <summary>
    /// When the pitch equals the plot width per bar (not capped), the
    /// right-anchored slots reproduce the legacy left-anchored layout bar
    /// for bar: floating-point round-off keeps them within one ulp.
    /// </summary>
    [Theory]
    [InlineData(1200.0, 100, 0)]
    [InlineData(1200.0, 100, 27)]
    [InlineData(1200.0, 100, 99)]
    [InlineData(593.0, 42, 41)]
    public void BarCenters_UncappedMatchTheLegacyLeftAnchoredLayout(
        double plotWidth,
        int barCount,
        int index)
    {
        var pitch = plotWidth / barCount;
        var legacyCenter = (index * pitch) + (pitch / 2);

        ChartBarLayout.BarCenterX(plotWidth, pitch, barCount, index)
            .Should().BeApproximately(legacyCenter, 1e-9);
    }

    #endregion

    #region Time Mapping

    /// <summary>
    /// Time ticks ride the bar slots: at an open tick the line sits on that
    /// bar slot's LEFT boundary, inside a slot it advances linearly through
    /// the bar's own span, and the last slot borrows the previous bar's
    /// span for times after its own open.
    /// </summary>
    [Fact]
    public void BarXForTime_TicksRideTheirBarSlots()
    {
        var opens = HourlyOpens(10);

        // Thirty minutes into bar 0: the first slot's middle.
        ChartBarLayout.BarXForTime(PlotWidth, CappedPitch, opens, opens[0].AddMinutes(30))
            .Should().Be(820.0);

        // A later open is exactly its own slot's left boundary.
        ChartBarLayout.BarXForTime(PlotWidth, CappedPitch, opens, opens[5])
            .Should().Be(1000.0);

        // Thirty minutes after bar 9's open (the last slot borrows bar 8's span).
        ChartBarLayout.BarXForTime(PlotWidth, CappedPitch, opens, opens[9].AddMinutes(30))
            .Should().Be(1180.0);
    }

    /// <summary>
    /// The mapping never walks off the slot space: a tick before the first
    /// open owns the first slot at its left boundary, and a tick past the
    /// last open clamps at the last slot's right boundary.
    /// </summary>
    [Fact]
    public void BarXForTime_TheAxisEdgesStayInsideTheSlotSpace()
    {
        var opens = HourlyOpens(10);

        ChartBarLayout.BarXForTime(PlotWidth, CappedPitch, opens, opens[0].AddHours(-48))
            .Should().Be(800.0, "before the first open is the first slot, at its left boundary");
        ChartBarLayout.BarXForTime(PlotWidth, CappedPitch, opens, opens[9].AddHours(9))
            .Should().Be(1200.0, "the borrowed span clamps the tick at the last slot's right boundary");
    }

    /// <summary>
    /// A gap in the opens (a weekend on the daily frame): the owning slot
    /// still covers the gap — the tick advances through it linearly, the
    /// slot never borrows a stranger's width.
    /// </summary>
    [Fact]
    public void BarXForTime_AGapBarAdvancesThroughItsOwnSpan()
    {
        var opens = new[]
        {
            new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero)
        };

        var sunday = new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);
        var firstSlotLeft = PlotWidth - (3 * CappedPitch);
        var expected = firstSlotLeft + ((2d / 3d) * CappedPitch);

        ChartBarLayout.BarXForTime(PlotWidth, CappedPitch, opens, sunday)
            .Should().BeApproximately(expected, 1e-9);
    }

    /// <summary>
    /// A one-bar window has no honest span to advance through: every tick
    /// anchors at the single slot's left boundary.
    /// </summary>
    [Fact]
    public void BarXForTime_AOneBarWindowAnchorsAtTheSingleSlotLeftBoundary()
    {
        var opens = HourlyOpens(1);
        var singleSlotLeft = PlotWidth - CappedPitch;

        ChartBarLayout.BarXForTime(PlotWidth, CappedPitch, opens, opens[0])
            .Should().Be(singleSlotLeft);
        ChartBarLayout.BarXForTime(PlotWidth, CappedPitch, opens, opens[0].AddHours(3))
            .Should().Be(singleSlotLeft);
    }

    /// <summary>
    /// An empty window carries no slots: the fallback anchors one pitch
    /// back from the plot edge (the draw pass guards it away first).
    /// </summary>
    [Fact]
    public void BarXForTime_AnEmptyWindowFallsBackOnePitchBack()
    {
        var opens = Array.Empty<DateTimeOffset>();

        ChartBarLayout.BarXForTime(PlotWidth, CappedPitch, opens, HourlyOpens(1)[0])
            .Should().Be(PlotWidth - CappedPitch);
    }

    #endregion

    #region Open Times

    [Fact]
    public void CollectOpenTimes_MapsTheBarsToTheirOpenTimes()
    {
        var opens = HourlyOpens(3);
        var bars = opens
            .Select(time => new Bar(time, 10, 12, 9, 11, 1000))
            .ToArray();

        ChartBarLayout.CollectOpenTimes(bars).Should().Equal(opens);
    }

    [Fact]
    public void CollectOpenTimes_AnEmptyWindowCarriesNoTimes()
    {
        ChartBarLayout.CollectOpenTimes(Array.Empty<Bar>())
            .Should().BeEmpty();
    }

    #endregion

    /// <summary>Hourly opens starting at a fixed UTC midnight.</summary>
    private static IReadOnlyList<DateTimeOffset> HourlyOpens(int count) =>
        Enumerable
            .Range(0, count)
            .Select(offset => new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero).AddHours(offset))
            .ToArray();
}
