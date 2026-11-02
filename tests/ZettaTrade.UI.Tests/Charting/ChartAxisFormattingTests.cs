namespace ZettaTrade.UI.Tests.Charting;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.UI.Charting;

/// <summary>
/// Pins for the chart's adaptive axis-label math: the time-step ladder, tick
/// alignment, both formatters and the price precision rule. The chart canvas
/// is UIA-invisible, so the zoom re-bucketing behavior is pinned in pure form
/// here; the renderer consumes these results unchanged.
/// </summary>
public sealed class ChartAxisFormattingTests
{
    #region Time Step Ladder

    /// <summary>
    /// Zoom re-bucketing: the visible span picks the finest ladder step that
    /// still keeps the label count inside the budget. Zoomed out (H1 window
    /// ~7 d) the labels become whole days; zoomed in (~20 M1 bars) they are
    /// five minutes apart.
    /// </summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(19, 5)]
    [InlineData(60, 10)]
    [InlineData(70, 10)]
    [InlineData(181, 30)]
    [InlineData(995, 120)]
    [InlineData(1140, 120)]
    [InlineData(1199, 120)]
    [InlineData(10020, 1440)]
    [InlineData(288000, 40320)]
    [InlineData(8766000, 1051200)]
    public void PickTimeStep_PicksTheFinestLadderStepThatFitsTheLabelBudget(
        double spanMinutes,
        double expectedMinutes)
    {
        var step = ChartAxisFormatting.PickTimeStep(TimeSpan.FromMinutes(spanMinutes));

        step.TotalMinutes.Should().Be(expectedMinutes);
    }

    /// <summary>
    /// Ticks align to the step grid strictly inside the window: the floor and
    /// ceiling boundary slots drop out, so no label ever sits outside the
    /// plot's time span.
    /// </summary>
    [Fact]
    public void PickTimeTicks_AlignsToTheStepGridInsideTheWindow()
    {
        var first = new DateTimeOffset(2026, 11, 2, 9, 12, 0, TimeSpan.Zero);
        var last = new DateTimeOffset(2026, 11, 2, 11, 47, 0, TimeSpan.Zero);

        var ticks = ChartAxisFormatting.PickTimeTicks(first, last, TimeSpan.FromMinutes(30));

        ticks.Should().Equal(
            new DateTimeOffset(2026, 11, 2, 9, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 2, 10, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 2, 11, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 2, 11, 30, 0, TimeSpan.Zero));
    }

    /// <summary>
    /// A window straddling no grid point produces no ticks — better honest
    /// silence than a label overflowing the plot.
    /// </summary>
    [Fact]
    public void PickTimeTicks_WindowWithoutAGridPoint_IsEmpty()
    {
        var first = new DateTimeOffset(2026, 11, 2, 9, 6, 0, TimeSpan.Zero);
        var last = new DateTimeOffset(2026, 11, 2, 9, 24, 0, TimeSpan.Zero);

        ChartAxisFormatting.PickTimeTicks(first, last, TimeSpan.FromMinutes(30)).Should().BeEmpty();
    }

    /// <summary>
    /// A window shorter than the step still keeps its interior grid point:
    /// the tick is on the grid and strictly inside the window, so dropping
    /// it would leave a labeled scale unlabeled for no reason.
    /// </summary>
    [Fact]
    public void PickTimeTicks_WindowShorterThanTheStep_KeepsItsInteriorGridPoint()
    {
        var first = new DateTimeOffset(2026, 11, 2, 9, 51, 0, TimeSpan.Zero);
        var last = new DateTimeOffset(2026, 11, 2, 10, 9, 0, TimeSpan.Zero);

        ChartAxisFormatting.PickTimeTicks(first, last, TimeSpan.FromMinutes(30))
            .Should().Equal(new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero));
    }

    /// <summary>
    /// A span beyond the ladder tail (multi-year windows) never floods the
    /// axis: the rightmost budget of ticks wins, and every tick stays inside
    /// the window in ascending order.
    /// </summary>
    [Fact]
    public void PickTimeTicks_SpanBeyondTheLadder_KeepsOnlyTheRightmostBudget()
    {
        var first = new DateTimeOffset(1826, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var last = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var ticks = ChartAxisFormatting.PickTimeTicks(first, last, ChartAxisFormatting.PickTimeStep(last - first));

        ticks.Should().HaveCount(10).And.BeInAscendingOrder();
        ticks[0].Should().BeOnOrAfter(first);
        ticks[^1].Should().BeOnOrBefore(last);
    }

    #endregion

    #region Time Labels

    /// <summary>
    /// Intraday steps print clock times; a midnight coinciding with the step
    /// grid names its day instead, so day boundaries stay readable.
    /// </summary>
    [Fact]
    public void FormatTimeTick_IntradaySteps_PrintClockTimesAndNameMidnights()
    {
        var culture = CultureInfo.GetCultureInfo("en-US");
        var noon = new DateTimeOffset(2026, 11, 2, 14, 30, 0, TimeSpan.Zero);
        var midnight = new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);

        ChartAxisFormatting.FormatTimeTick(noon, TimeSpan.FromMinutes(30), culture).Should().Be("14:30");
        ChartAxisFormatting.FormatTimeTick(midnight, TimeSpan.FromMinutes(30), culture).Should().Be("Nov 3");
    }

    /// <summary>
    /// Whole-day steps print the month and day; year-scale steps (MN1-class
    /// windows) print the year only — a two-year grid is a calendar scale.
    /// </summary>
    [Fact]
    public void FormatTimeTick_DaySteps_PrintMonthDayAndYearStepsPrintYears()
    {
        var culture = CultureInfo.GetCultureInfo("en-US");
        var day = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);

        ChartAxisFormatting.FormatTimeTick(day, TimeSpan.FromDays(1), culture).Should().Be("Nov 2");
        ChartAxisFormatting.FormatTimeTick(day, TimeSpan.FromDays(730), culture).Should().Be("2026");
    }

    #endregion

    #region Price Precision

    /// <summary>
    /// The digit count grows with the tick step's magnitude and clamps to the
    /// money conventions: always at least two digits, never more than eight.
    /// </summary>
    [Theory]
    [InlineData("0", 2)]
    [InlineData("1000", 2)]
    [InlineData("0.02", 2)]
    [InlineData("0.005", 3)]
    [InlineData("0.000208", 4)]
    [InlineData("0.0000015", 6)]
    [InlineData("0.0000000015", 8)]
    [InlineData("12345.6", 2)]
    public void PickPriceDecimals_GrowsWithTheTickStep(
        string stepText,
        int expectedDecimals)
    {
        var step = decimal.Parse(stepText, CultureInfo.InvariantCulture);

        ChartAxisFormatting.PickPriceDecimals(step).Should().Be(expectedDecimals);
    }

    /// <summary>
    /// The reported ETH/BTC defect reproduced as a pin: with F2-bound labels
    /// every bar read the same "0,03". The four-digit rule keeps all labels
    /// of the narrow padded window distinct.
    /// </summary>
    [Fact]
    public void PriceTicks_NarrowEthBtcWindow_NeverCollapseIntoOneLabel()
    {
        var min = 0.02948m;
        var max = 0.03052m;

        var ticks = ChartAxisFormatting.PickPriceTicks(min, max, slotCount: 5);
        var decimals = ChartAxisFormatting.PickPriceDecimals((max - min) / 5);
        var labels = ticks
            .Select(tick => ChartAxisFormatting.FormatPriceTick(tick, decimals, CultureInfo.InvariantCulture))
            .ToList();

        decimals.Should().BeGreaterThan(2, "the sub-penny step needs more digits than money convention");
        labels.Distinct().Should().HaveCount(ticks.Count);
    }

    /// <summary>
    /// The price ladder spans the padded window evenly with both extremes
    /// included — the same positions the horizontal grid maps.
    /// </summary>
    [Fact]
    public void PickPriceTicks_SpansTheWindowEvenlyWithBothExtremes()
    {
        var ticks = ChartAxisFormatting.PickPriceTicks(100m, 200m, slotCount: 5);

        ticks.Should().HaveCount(6);
        ticks[0].Should().Be(100m);
        ticks[^1].Should().Be(200m);
        ticks.Should().BeInAscendingOrder();
        ticks[2].Should().Be(140m);
    }

    #endregion
}
