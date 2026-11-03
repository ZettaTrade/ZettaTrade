namespace ZettaTrade.UI.Tests.Charting;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.UI.Charting;

/// <summary>
/// Pins for the chart's adaptive axis-label math: the time rung ladder,
/// calendar anchoring (epoch clock/day lattice, Monday weeks, month starts,
/// January 1sts), both formatters, the measured budget rule and the price
/// precision rule. The chart canvas is UIA-invisible, so the zoom
/// re-bucketing behavior is pinned in pure form here; the renderer consumes
/// these results unchanged.
/// </summary>
public sealed class ChartAxisFormattingTests
{
    /// <summary>The en-US culture for culture-pinned label facts.</summary>
    private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");

    /// <summary>The +05:30 offset for the clock-lattice offset-stability pin.</summary>
    private static readonly TimeSpan OffsetFiveThirty = new(5, 30, 0);

    private static TimeTickRung ClockRung30 => new(TimeSpan.FromMinutes(30), TimeTickScale.Clock, 30);

    private static TimeTickRung DayRung1 => new(TimeSpan.FromDays(1), TimeTickScale.Day, 1);

    private static TimeTickRung WeekRung1 => new(TimeSpan.FromDays(7), TimeTickScale.Week, 1);

    private static TimeTickRung WeekRung2 => new(TimeSpan.FromDays(14), TimeTickScale.Week, 2);

    private static TimeTickRung MonthRung1 => new(TimeSpan.FromDays(30), TimeTickScale.Month, 1);

    private static TimeTickRung MonthRung6 => new(TimeSpan.FromDays(182), TimeTickScale.Month, 6);

    private static TimeTickRung YearRung2 => new(TimeSpan.FromDays(730), TimeTickScale.Year, 2);

    #region Time Rung Ladder

    /// <summary>
    /// Zoom re-bucketing: the visible span picks the finest rung whose
    /// nominal length keeps the label count inside the budget. A shrunk or
    /// swollen budget both clamp into the slicing band. The intraday rungs
    /// 2m/3m are the user-reported density defect's fix: the 20-bar M1 zoom
    /// floor grids 2-minute (the old fixed budget always landed one rung
    /// above the bars, at 5m).
    /// </summary>
    [Theory]
    [InlineData(0, 1, 10)]
    [InlineData(19, 2, 10)]
    [InlineData(45, 5, 10)]
    [InlineData(995, 120, 10)]
    [InlineData(10080, 1440, 10)]
    [InlineData(47520, 10080, 10)]
    [InlineData(272160, 43200, 10)]
    [InlineData(1008000, 131040, 10)]
    [InlineData(4868640, 525600, 10)]
    [InlineData(8784000, 1051200, 10)]
    [InlineData(19, 5, 4)]
    [InlineData(19, 5, 3)]
    [InlineData(19, 1, 20)]
    [InlineData(19, 1, 100)]
    [InlineData(105120001, 5256000, 10)]
    public void PickTimeStep_PicksTheFinestRungThatFitsTheBudget(
        double spanMinutes,
        double expectedMinutes,
        int budget)
    {
        var rung = ChartAxisFormatting.PickTimeStep(TimeSpan.FromMinutes(spanMinutes), budget);

        rung.Nominal.TotalMinutes.Should().BeApproximately(expectedMinutes, 1e-6);
    }

    /// <summary>
    /// The rung a peak time frame window picks: the M1 zoom floor rides the
    /// 2-minute clock lattice, the H4 default the Monday weekly grid, the D1
    /// default the month starts, the W1 default the quarter starts and the
    /// MN1 default the even-year January 1sts.
    /// </summary>
    [Fact]
    public void PickTimeStep_ThePerTimeFrameWindows_PickTheirCalendarScales()
    {
        var m1Floor = ChartAxisFormatting.PickTimeStep(TimeSpan.FromMinutes(19), ChartAxisFormatting.DefaultTimeTickBudget);
        m1Floor.Scale.Should().Be(TimeTickScale.Clock);
        m1Floor.Multiple.Should().Be(2, "the 20-bar zoom floor must grid below 5 minutes");

        var h4Default = ChartAxisFormatting.PickTimeStep(TimeSpan.FromDays(33), ChartAxisFormatting.DefaultTimeTickBudget);
        h4Default.Scale.Should().Be(TimeTickScale.Week);
        h4Default.Multiple.Should().Be(1, "the 33-day H4 window rides the Monday weekly grid");

        var d1Default = ChartAxisFormatting.PickTimeStep(TimeSpan.FromDays(189), ChartAxisFormatting.DefaultTimeTickBudget);
        d1Default.Scale.Should().Be(TimeTickScale.Month);
        d1Default.Multiple.Should().Be(1, "the 189-day D1 window grids month starts");

        var w1Default = ChartAxisFormatting.PickTimeStep(TimeSpan.FromDays(700), ChartAxisFormatting.DefaultTimeTickBudget);
        w1Default.Scale.Should().Be(TimeTickScale.Month);
        w1Default.Multiple.Should().Be(3, "the 700-day W1 window grids quarter starts");

        var mnDefault = ChartAxisFormatting.PickTimeStep(TimeSpan.FromDays(6100), ChartAxisFormatting.DefaultTimeTickBudget);
        mnDefault.Scale.Should().Be(TimeTickScale.Year);
        mnDefault.Multiple.Should().Be(2, "the 16.7-year MN window grids even-year January 1sts");
    }

    #endregion

    #region Tick Slots

    /// <summary>
    /// Clock rungs anchor on the epoch lattice: the window's floor and
    /// ceiling partial slots drop out and the interior ones land exactly on
    /// the :00/:30 grid, ascending.
    /// </summary>
    [Fact]
    public void PickTimeTicks_ClockRung_LandsInteriorSlotsOnTheGrid()
    {
        var first = new DateTimeOffset(2026, 11, 2, 9, 12, 0, TimeSpan.Zero);
        var last = new DateTimeOffset(2026, 11, 2, 11, 47, 0, TimeSpan.Zero);

        var ticks = ChartAxisFormatting.PickTimeTicks(first, last, ClockRung30, ChartAxisFormatting.DefaultTimeTickBudget);

        ticks.Should().Equal(
            new DateTimeOffset(2026, 11, 2, 9, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 2, 10, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 2, 11, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 2, 11, 30, 0, TimeSpan.Zero));
    }

    /// <summary>
    /// A window straddling no grid point produces no ticks — better honest
    /// silence than a label overflowing the plot (the T-035 rule, kept).
    /// </summary>
    [Fact]
    public void PickTimeTicks_ClockRung_AWindowWithoutASlotStaysSilent()
    {
        var first = new DateTimeOffset(2026, 11, 2, 9, 6, 0, TimeSpan.Zero);
        var last = new DateTimeOffset(2026, 11, 2, 9, 24, 0, TimeSpan.Zero);

        ChartAxisFormatting.PickTimeTicks(first, last, ClockRung30, ChartAxisFormatting.DefaultTimeTickBudget)
            .Should().BeEmpty();
    }

    /// <summary>
    /// A window shorter than the slot still keeps its interior grid point:
    /// the tick is on the lattice and inside the window, so dropping it
    /// would leave a labeled scale unlabeled for no reason.
    /// </summary>
    [Fact]
    public void PickTimeTicks_ClockRung_AWindowShorterThanTheSlotKeepsItsInteriorGridPoint()
    {
        var first = new DateTimeOffset(2026, 11, 2, 9, 51, 0, TimeSpan.Zero);
        var last = new DateTimeOffset(2026, 11, 2, 10, 9, 0, TimeSpan.Zero);

        ChartAxisFormatting.PickTimeTicks(first, last, ClockRung30, ChartAxisFormatting.DefaultTimeTickBudget)
            .Should().Equal(new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero));
    }

    /// <summary>
    /// The clock lattice anchors on the UTC epoch, so it survives non-zero
    /// window offsets: the +05:30 window's ticks keep the very same absolute
    /// :00/:30 instants, rendered in the window's own offset.
    /// </summary>
    [Fact]
    public void PickTimeTicks_ClockRung_TheLatticeSurvivesTheWindowsOffset()
    {
        var first = new DateTimeOffset(2026, 11, 2, 14, 42, 0, OffsetFiveThirty);
        var last = new DateTimeOffset(2026, 11, 2, 17, 17, 0, OffsetFiveThirty);

        var ticks = ChartAxisFormatting.PickTimeTicks(first, last, ClockRung30, ChartAxisFormatting.DefaultTimeTickBudget);

        ticks.Should().Equal(
            new DateTimeOffset(2026, 11, 2, 15, 0, 0, OffsetFiveThirty),
            new DateTimeOffset(2026, 11, 2, 15, 30, 0, OffsetFiveThirty),
            new DateTimeOffset(2026, 11, 2, 16, 0, 0, OffsetFiveThirty),
            new DateTimeOffset(2026, 11, 2, 16, 30, 0, OffsetFiveThirty),
            new DateTimeOffset(2026, 11, 2, 17, 0, 0, OffsetFiveThirty));
    }

    /// <summary>
    /// Week rungs anchor on the first Monday of the epoch: the weekly grid
    /// rides the Monday opens the weekly candles actually own — the old
    /// uniform multiples sliced arbitrary weekdays between the candles.
    /// </summary>
    [Fact]
    public void PickTimeTicks_WeekRung_RidesTheMondayOpens()
    {
        var first = new DateTimeOffset(2026, 11, 7, 0, 0, 0, TimeSpan.Zero);
        var last = new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero);

        var ticks = ChartAxisFormatting.PickTimeTicks(first, last, WeekRung1, ChartAxisFormatting.DefaultTimeTickBudget);

        ticks.Should().Equal(new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero));
        ticks.Single().DayOfWeek.Should().Be(DayOfWeek.Monday);
    }

    /// <summary>
    /// The biweekly scale hits every SECOND Monday of the epoch lattice: a
    /// Monday-to-Wednesday window keeps its two interior Monday slots — the
    /// very grid the terminal daily/weekly convention implies.
    /// </summary>
    [Fact]
    public void PickTimeTicks_WeekRung_ABiweeklyGridHitsEverySecondMonday()
    {
        var first = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
        var last = new DateTimeOffset(2026, 12, 2, 0, 0, 0, TimeSpan.Zero);

        var ticks = ChartAxisFormatting.PickTimeTicks(first, last, WeekRung2, ChartAxisFormatting.DefaultTimeTickBudget);

        ticks.Should().Equal(
            new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 23, 0, 0, 0, TimeSpan.Zero));
        ticks.Select(t => t.DayOfWeek).Should().OnlyContain(day => day == DayOfWeek.Monday);
    }

    /// <summary>
    /// The user-reported D1 repro, pinned: a ~6-month daily window grids the
    /// MONTH STARTS (May 1 … Nov 1) instead of the old 28-day epoch grid's
    /// off-month dates (Apr 27, May 25, Jun 22 …). Every tick of the
    /// pipeline-picked rung is a calendar month start.
    /// </summary>
    [Fact]
    public void PickTimeTicks_MonthRung_TheD1ReproGridsTheMonthStarts()
    {
        var first = new DateTimeOffset(2026, 4, 27, 0, 0, 0, TimeSpan.Zero);
        var last = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
        var span = last - first;

        var rung = ChartAxisFormatting.PickTimeStep(span, ChartAxisFormatting.DefaultTimeTickBudget);
        rung.Scale.Should().Be(TimeTickScale.Month);
        rung.Multiple.Should().Be(1);

        var ticks = ChartAxisFormatting.PickTimeTicks(first, last, rung, ChartAxisFormatting.DefaultTimeTickBudget);

        ticks.Should().Equal(
            new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        ticks.Select(t => t.Day).Should().OnlyContain(day => day == 1);
    }

    /// <summary>
    /// The user-reported W1 repro (the "time runs backward" defect), pinned:
    /// the old 182-day uniform grid drifted its day-of-month backward cycle
    /// by cycle while repeating the yearless month name (февр. 6 → февр. 5 →
    /// февр. 3), and Feb 6 2025 was one of its off-calendar tick instants.
    /// The quarter-run grid of the same window anchors EXACT calendar month
    /// starts instead: each label repeats the same day-of-month (1st) and
    /// every label carries its own year.
    /// </summary>
    [Fact]
    public void PickTimeTicks_MonthRung_TheW1FebDriftReproAnchorsTheCalendarStarts()
    {
        var first = new DateTimeOffset(2025, 2, 6, 0, 0, 0, TimeSpan.Zero);
        var last = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
        var span = last - first;

        var rung = ChartAxisFormatting.PickTimeStep(span, ChartAxisFormatting.DefaultTimeTickBudget);
        rung.Scale.Should().Be(TimeTickScale.Month);
        rung.Multiple.Should().Be(3);

        var ticks = ChartAxisFormatting.PickTimeTicks(first, last, rung, ChartAxisFormatting.DefaultTimeTickBudget);

        ticks.Should().Equal(
            new DateTimeOffset(2025, 4, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2025, 7, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2025, 10, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        ticks.Select(t => t.Day).Should().OnlyContain(day => day == 1);
    }

    /// <summary>
    /// The six-month scale counts CALENDAR months: January and July are the
    /// slots (their ordinals are 6-multiples), February-length months never
    /// push the grid.
    /// </summary>
    [Fact]
    public void PickTimeTicks_MonthRung_ASixMonthScaleGridsJanuaryAndJuly()
    {
        var first = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var last = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);

        var ticks = ChartAxisFormatting.PickTimeTicks(first, last, MonthRung6, ChartAxisFormatting.DefaultTimeTickBudget);

        ticks.Should().Equal(
            new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2025, 7, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
    }

    /// <summary>
    /// The MN1-class window (Aug 2017 → Nov 2026) rides the year scale: the
    /// January 1st slots run 2018 … 2026, each year printing bare — the
    /// terminal monthly convention (years, not arbitrary day multiples).
    /// </summary>
    [Fact]
    public void PickTimeTicks_YearRung_TheMnReproGridsJanuary1sts()
    {
        var first = new DateTimeOffset(2017, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var last = new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);
        var span = last - first;

        var rung = ChartAxisFormatting.PickTimeStep(span, ChartAxisFormatting.DefaultTimeTickBudget);
        rung.Scale.Should().Be(TimeTickScale.Year);
        rung.Multiple.Should().Be(1);

        var ticks = ChartAxisFormatting.PickTimeTicks(first, last, rung, ChartAxisFormatting.DefaultTimeTickBudget);

        ticks.Should().HaveCount(9);
        ticks[0].Should().Be(new DateTimeOffset(2018, 1, 1, 0, 0, 0, TimeSpan.Zero));
        ticks[^1].Should().Be(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        ticks.Select(t => t.Month).Should().OnlyContain(month => month == 1);
        ticks.Select(t => t.Day).Should().OnlyContain(day => day == 1);
    }

    /// <summary>
    /// Real calendar lengths can spill a slot past the nominal count fit
    /// (a 31-day month is longer than the 30-day nominal): the OLDEST ticks
    /// drop out so the fresh right edge keeps its grid, exactly at the
    /// minimum budget.
    /// </summary>
    [Fact]
    public void PickTimeTicks_TheSlotListClampsToTheRightmostBudget()
    {
        var first = new DateTimeOffset(2026, 1, 31, 0, 0, 0, TimeSpan.Zero);
        var last = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

        var atMinBudget = ChartAxisFormatting.PickTimeTicks(first, last, MonthRung1, ChartAxisFormatting.MinTimeTickBudget);

        atMinBudget.Should().Equal(
            new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));

        // A budget below the band clamps up: the same rightmost list wins.
        ChartAxisFormatting.PickTimeTicks(first, last, MonthRung1, budget: 2)
            .Should().Equal(atMinBudget);
    }

    /// <summary>
    /// Degenerate inputs stay silent: a reversed window and a zero rung
    /// (never produced by the ladder, but a public method guards its own).
    /// </summary>
    [Fact]
    public void PickTimeTicks_DegenerateInputsStaySilent()
    {
        var first = new DateTimeOffset(2026, 11, 2, 9, 12, 0, TimeSpan.Zero);
        var last = new DateTimeOffset(2026, 11, 2, 11, 47, 0, TimeSpan.Zero);

        ChartAxisFormatting.PickTimeTicks(last, first, ClockRung30, ChartAxisFormatting.DefaultTimeTickBudget)
            .Should().BeEmpty();
        ChartAxisFormatting.PickTimeTicks(first, last, default, ChartAxisFormatting.DefaultTimeTickBudget)
            .Should().BeEmpty();
    }

    #endregion

    #region Time Labels

    /// <summary>
    /// Within one calendar year the intraday grid prints clock times and
    /// hands a coinciding midnight to the date format (full day context
    /// kept); calendar rungs always print month + day.
    /// </summary>
    [Fact]
    public void FormatTimeTick_WithinOneYear_ClockAndCalendarLabels()
    {
        var noon = new DateTimeOffset(2026, 11, 2, 14, 30, 0, TimeSpan.Zero);
        var midnight = new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);
        var twoDays = TimeSpan.FromDays(2);

        ChartAxisFormatting.FormatTimeTick(noon, ClockRung30, twoDays, EnUs, use12HourClock: false).Should().Be("14:30");
        ChartAxisFormatting.FormatTimeTick(midnight, ClockRung30, twoDays, EnUs, use12HourClock: false).Should().Be("Nov 3");
        ChartAxisFormatting.FormatTimeTick(midnight, DayRung1, TimeSpan.FromDays(30), EnUs, use12HourClock: false).Should().Be("Nov 3");
    }

    /// <summary>
    /// A multi-year span pins every month label with its own year: the OLD
    /// yearless "февр." repeated across three years was unreadable. The
    /// backslash escape renders one visible apostrophe in the label.
    /// </summary>
    [Fact]
    public void FormatTimeTick_MultiYearSpans_PinEveryMonthWithItsYear()
    {
        var span = TimeSpan.FromDays(700);
        var feb25 = new DateTimeOffset(2025, 2, 6, 0, 0, 0, TimeSpan.Zero);
        var feb26 = new DateTimeOffset(2026, 2, 4, 0, 0, 0, TimeSpan.Zero);

        ChartAxisFormatting.FormatTimeTick(feb25, MonthRung1, span, EnUs, use12HourClock: false).Should().Be("Feb '25");
        ChartAxisFormatting.FormatTimeTick(feb26, MonthRung1, span, EnUs, use12HourClock: false).Should().Be("Feb '26");

        new[] { feb25, feb26 }
            .Select(tick => ChartAxisFormatting.FormatTimeTick(tick, MonthRung1, span, EnUs, use12HourClock: false))
            .Distinct()
            .Should().HaveCount(2, "the repeated month names of different years must read apart");
    }

    /// <summary>
    /// The one-year span threshold hands over at STRICTLY wider spans: a
    /// span of exactly one calendar year still prints month + day.
    /// </summary>
    [Fact]
    public void FormatTimeTick_TheOneYearThresholdHandsToTheMonthYearFormat()
    {
        var novFirst = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

        ChartAxisFormatting.FormatTimeTick(novFirst, MonthRung1, TimeSpan.FromDays(365), EnUs, use12HourClock: false)
            .Should().Be("Nov 1", "exactly-one-year spans still read as plain dates");
        ChartAxisFormatting.FormatTimeTick(novFirst, MonthRung1, TimeSpan.FromDays(366), EnUs, use12HourClock: false)
            .Should().Be("Nov '26");
    }

    /// <summary>
    /// Year-scale rungs print the bare year whatever the span: a year grid
    /// is a calendar scale, not a time-of-day one (the MN1 two-year grid).
    /// </summary>
    [Fact]
    public void FormatTimeTick_YearScaleSteps_PrintTheBareYear()
    {
        var newYear2026 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        ChartAxisFormatting.FormatTimeTick(newYear2026, YearRung2, TimeSpan.FromDays(6100), EnUs, use12HourClock: false)
            .Should().Be("2026");
    }

    #endregion

    #region Twelve-Hour Clock

    /// <summary>
    /// The 12-hour clock pins: the hours read the half-day convention with
    /// no leading zero, noon and just-after-midnight ride their AM/PM sides.
    /// </summary>
    [Fact]
    public void FormatTimeTick_TheTwelveHourClock_PrintsTheHalfDayConvention()
    {
        var afternoon = new DateTimeOffset(2026, 11, 2, 14, 30, 0, TimeSpan.Zero);
        var justAfterMidnight = new DateTimeOffset(2026, 11, 2, 0, 30, 0, TimeSpan.Zero);
        var noon = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        var morning = new DateTimeOffset(2026, 11, 2, 9, 15, 0, TimeSpan.Zero);

        ChartAxisFormatting.FormatTimeTick(afternoon, ClockRung30, TimeSpan.FromDays(2), EnUs, use12HourClock: true)
            .Should().Be("2:30 PM");
        ChartAxisFormatting.FormatTimeTick(justAfterMidnight, ClockRung30, TimeSpan.FromDays(2), EnUs, use12HourClock: true)
            .Should().Be("12:30 AM");
        ChartAxisFormatting.FormatTimeTick(noon, ClockRung30, TimeSpan.FromDays(2), EnUs, use12HourClock: true)
            .Should().Be("12:00 PM");
        ChartAxisFormatting.FormatTimeTick(morning, ClockRung30, TimeSpan.FromDays(2), EnUs, use12HourClock: true)
            .Should().Be("9:15 AM", "the 12-hour hour glyph carries no leading zero");
    }

    /// <summary>
    /// The half-day clock in a non-English language (the Russian one): the
    /// hour and minute glyphs stay the culture's own and the designators come
    /// from its culture data — Latin "AM"/"PM" on .NET 10's ICU (verified
    /// empirically: every installed culture carries designators there, even
    /// the languages whose legacy NLS data had none), and an en-US-style
    /// format string in front of it yields the same half-day convention as
    /// the English UI language.
    /// </summary>
    [Fact]
    public void FormatTimeTick_TheTwelveHourClock_DesignatorsComeFromTheCulture()
    {
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var afternoon = new DateTimeOffset(2026, 11, 2, 14, 30, 0, TimeSpan.Zero);
        var beforeDawn = new DateTimeOffset(2026, 11, 2, 2, 5, 0, TimeSpan.Zero);

        ChartAxisFormatting.FormatTimeTick(afternoon, ClockRung30, TimeSpan.FromDays(2), ru, use12HourClock: true)
            .Should().Be("2:30 PM");
        ChartAxisFormatting.FormatTimeTick(beforeDawn, ClockRung30, TimeSpan.FromDays(2), ru, use12HourClock: true)
            .Should().Be("2:05 AM");
    }

    /// <summary>
    /// Midnight hands off to the full-date label in the 12-hour mode too
    /// (the T-035 handoff is a span rule, not a clock-format one), and the
    /// 24-hour mode keeps exactly the glyphs it always printed.
    /// </summary>
    [Fact]
    public void FormatTimeTick_MidnightStillHandsOffToTheDateLabel()
    {
        var midnight = new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);

        ChartAxisFormatting.FormatTimeTick(midnight, ClockRung30, TimeSpan.FromDays(2), EnUs, use12HourClock: true)
            .Should().Be("Nov 3");
        ChartAxisFormatting.FormatTimeTick(midnight, ClockRung30, TimeSpan.FromDays(2), EnUs, use12HourClock: false)
            .Should().Be("Nov 3", "the handoff is format-agnostic");
    }

    /// <summary>
    /// The user-selected clock format touches ONLY the clock text: the
    /// 24-hour glyphs stay and calendar / multi-year labels ignore it.
    /// </summary>
    [Fact]
    public void FormatTimeTick_TheClockFormatTouchesOnlyTheClockText()
    {
        var afternoon = new DateTimeOffset(2026, 11, 2, 14, 30, 0, TimeSpan.Zero);
        var multiYear = new DateTimeOffset(2026, 2, 4, 0, 0, 0, TimeSpan.Zero);

        ChartAxisFormatting.FormatTimeTick(afternoon, ClockRung30, TimeSpan.FromDays(2), EnUs, use12HourClock: false)
            .Should().Be("14:30");
        ChartAxisFormatting.FormatTimeTick(multiYear, MonthRung1, TimeSpan.FromDays(700), EnUs, use12HourClock: true)
            .Should().Be("Feb '26");
    }

    #endregion

    #region Label Budget

    /// <summary>
    /// The measured budget: one label's width per slot plus the gap,
    /// floor-divided into the plot width, clamped into the sane band so a
    /// starved plot still labels (min 4) and a wide one never floods
    /// (max 20).
    /// </summary>
    [Theory]
    [InlineData(1200, 60, 24, 13)]
    [InlineData(1000, 38, 24, 15)]
    [InlineData(100, 60, 24, 4)]
    [InlineData(2000, 60, 24, 20)]
    [InlineData(1200, 0, 24, 20)]
    [InlineData(1200, 60, 0, 19)]
    public void TimeTickBudgetFor_FitsLabelWidthsIntoThePlot(
        double plotWidth,
        double maxLabelWidth,
        double labelGap,
        int expected)
    {
        ChartAxisFormatting.TimeTickBudgetFor(plotWidth, maxLabelWidth, labelGap)
            .Should().Be(expected);
    }

    /// <summary>
    /// A label wider than the whole plot (or an over-long measure) falls
    /// back to the minimum budget instead of flooding the plot.
    /// </summary>
    [Fact]
    public void TimeTickBudgetFor_OverwideLabels_FallBackToTheMinimum()
    {
        ChartAxisFormatting.TimeTickBudgetFor(5000, 6000, 24).Should().Be(4);
        ChartAxisFormatting.TimeTickBudgetFor(1200, 1200, 24).Should().Be(4);
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
