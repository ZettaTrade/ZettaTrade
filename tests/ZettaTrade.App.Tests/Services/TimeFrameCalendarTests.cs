namespace ZettaTrade.App.Tests.Services;

using System;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Enums;

/// <summary>
/// Pins for the session calendar the gap detector walks (T-086): the
/// intraday frames step fixed lengths for every asset class, the day and
/// week scales skip weekends for the session venues while crypto trades
/// straight through, the monthly frame rides calendar months, and an
/// unknown frame is an error — never a silently substituted default (a
/// wrong cadence would invent gaps or hide real ones). Pure facts.
/// </summary>
public sealed class TimeFrameCalendarTests
{
    /// <summary>Friday, 2026-01-02 00:00 UTC: two sessions days before the
    /// weekend the weekday facts walk across.</summary>
    private static readonly DateTimeOffset Friday = new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A neutral open (not weekend-adjacent) for the fixed-step facts.</summary>
    private static readonly DateTimeOffset TuesdayMorning = new(2026, 1, 6, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MinuteAndHourFramesStepTheirFixedLengthForEveryClass()
    {
        TimeFrameCalendar.NextOpenAfter(TimeFrame.M5, AssetClass.Stock, TuesdayMorning)
            .Should().Be(TuesdayMorning.AddMinutes(5));
        TimeFrameCalendar.NextOpenAfter(TimeFrame.H4, AssetClass.Crypto, TuesdayMorning)
            .Should().Be(TuesdayMorning.AddHours(4));
        TimeFrameCalendar.NextOpenAfter(TimeFrame.H1, AssetClass.Forex, Friday)
            .Should().Be(Friday.AddHours(1), "the intraday cadence never skips a session hour");
    }

    [Fact]
    public void CryptoDailyStepsStraightIntoTheWeekend()
    {
        TimeFrameCalendar.NextOpenAfter(TimeFrame.D1, AssetClass.Crypto, Friday)
            .Should().Be(Friday.AddDays(1), "a crypto venue trades the Saturday where a session venue is closed");
    }

    [Fact]
    public void MarketDailySkipsTheWeekend()
    {
        TimeFrameCalendar.NextOpenAfter(TimeFrame.D1, AssetClass.Stock, Friday)
            .Should().Be(new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero), "Friday's daily continues Monday");
        TimeFrameCalendar.NextOpenAfter(TimeFrame.D1, AssetClass.Forex, Friday)
            .Should().Be(TimeFrameCalendar.NextOpenAfter(TimeFrame.D1, AssetClass.Stock, Friday));
    }

    [Fact]
    public void CryptoWeeklyStepsSevenDays()
    {
        var monday = new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);

        TimeFrameCalendar.NextOpenAfter(TimeFrame.W1, AssetClass.Crypto, monday)
            .Should().Be(monday.AddDays(7), "the continuous weekly frame steps exactly one week");
    }

    [Fact]
    public void MarketWeeklyAnchorsTheNextMonday()
    {
        TimeFrameCalendar.NextOpenAfter(TimeFrame.W1, AssetClass.Stock, Friday)
            .Should().Be(
                new DateTimeOffset(2026, 1, 12, 0, 0, 0, TimeSpan.Zero),
                "Friday's weekly candle (off-anchor open) walks onto the FOLLOWING calendar week's Monday");
    }

    [Fact]
    public void MonthlyStepsCalendarMonths()
    {
        var monthEnd = new DateTimeOffset(2026, 1, 31, 0, 0, 0, TimeSpan.Zero);

        TimeFrameCalendar.NextOpenAfter(TimeFrame.MN1, AssetClass.Crypto, monthEnd)
            .Should().Be(
                new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero),
                "AddMonths clamps the month-end onto February's own length");
        TimeFrameCalendar.NextOpenAfter(TimeFrame.MN1, AssetClass.Stock, monthEnd.AddDays(-3))
            .Should().Be(new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void TheNominalBarLengthsMatchTheFrames()
    {
        TimeFrameCalendar.NominalBarLength(TimeFrame.M1).Should().Be(TimeSpan.FromMinutes(1));
        TimeFrameCalendar.NominalBarLength(TimeFrame.M5).Should().Be(TimeSpan.FromMinutes(5));
        TimeFrameCalendar.NominalBarLength(TimeFrame.H1).Should().Be(TimeSpan.FromHours(1));
        TimeFrameCalendar.NominalBarLength(TimeFrame.H4).Should().Be(TimeSpan.FromHours(4));
        TimeFrameCalendar.NominalBarLength(TimeFrame.D1).Should().Be(TimeSpan.FromDays(1));
        TimeFrameCalendar.NominalBarLength(TimeFrame.W1).Should().Be(TimeSpan.FromDays(7));
        TimeFrameCalendar.NominalBarLength(TimeFrame.MN1).Should().Be(
            TimeSpan.FromDays(30), "the monthly frame keeps the shared nominal month length");
    }

    [Fact]
    public void TheUnknownFrameIsAnErrorNeverADefault()
    {
        var unknown = (TimeFrame)999;

        var length = () => TimeFrameCalendar.NominalBarLength(unknown);
        length.Should().Throw<ArgumentOutOfRangeException>();

        var cadence = () => TimeFrameCalendar.NextOpenAfter(unknown, AssetClass.Crypto, TuesdayMorning);
        cadence.Should().Throw<ArgumentOutOfRangeException>();
    }
}
