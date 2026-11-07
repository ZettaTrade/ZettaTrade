namespace ZettaTrade.App.Services;

using System;
using ZettaTrade.Core.Enums;

/// <summary>
/// Session/timeframe calendar for candle cadence (T-086): the expected bar
/// OPEN sequence every chart series is built on. Crypto venues trade around
/// the clock, so the intraday and day-scale frames step continuously; the
/// stock-style venues keep weekday sessions, so their day-scale frames skip
/// weekends; the monthly frame rides the calendar month for every class.
/// <para>
/// The detector consumes this through
/// <see cref="NextOpenAfter"/> and never interrupts: an unknown time frame is
/// an error, never a silently substituted default (the standing rule — a
/// wrong cadence would invent gaps or hide real ones).
/// </para>
/// </summary>
public static class TimeFrameCalendar
{
    /// <summary>The nominal month length of one MN1 bar (the shared
    /// <c>HistoryStart</c> precedent: monthly steps are calendar months, the
    /// nominal length only sizes fetch windows).</summary>
    private const int NominalMonthDays = 30;

    /// <summary>
    /// The nominal length of one bar: exact for minutes, hours and weekdays,
    /// nominal for the monthly frame (calendar months are not a fixed span).
    /// The backfill segmenter and the eviction math walk back with it; the
    /// gap detector never uses it for expected opens (see
    /// <see cref="NextOpenAfter"/>).
    /// </summary>
    public static TimeSpan NominalBarLength(TimeFrame timeFrame) => timeFrame switch
    {
        TimeFrame.M1 => TimeSpan.FromMinutes(1),
        TimeFrame.M5 => TimeSpan.FromMinutes(5),
        TimeFrame.M15 => TimeSpan.FromMinutes(15),
        TimeFrame.M30 => TimeSpan.FromMinutes(30),
        TimeFrame.H1 => TimeSpan.FromHours(1),
        TimeFrame.H4 => TimeSpan.FromHours(4),
        TimeFrame.D1 => TimeSpan.FromDays(1),
        TimeFrame.W1 => TimeSpan.FromDays(7),
        TimeFrame.MN1 => TimeSpan.FromDays(NominalMonthDays),
        _ => throw new ArgumentOutOfRangeException(
            nameof(timeFrame),
            timeFrame,
            "Unknown time frame has no bar length."),
    };

    /// <summary>
    /// The open of the bar right after <paramref name="open"/> on the frame's
    /// cadence: the instant the next candle is EXPECTED, before any data
    /// arrives. Intraday frames step by their fixed length for every asset
    /// class; crypto steps continuously on the day scale while the session
    /// venues (stocks, forex, commodities) skip weekends on daily and weekly
    /// candles; the month steps calendar months. A Friday-to-Monday weekend
    /// is then not a gap at all — no bar is expected where the venue is
    /// closed.
    /// </summary>
    public static DateTimeOffset NextOpenAfter(
        TimeFrame timeFrame,
        AssetClass assetClass,
        DateTimeOffset open) => timeFrame switch
        {
            TimeFrame.M1 => open.AddMinutes(1),
            TimeFrame.M5 => open.AddMinutes(5),
            TimeFrame.M15 => open.AddMinutes(15),
            TimeFrame.M30 => open.AddMinutes(30),
            TimeFrame.H1 => open.AddHours(1),
            TimeFrame.H4 => open.AddHours(4),
            TimeFrame.D1 => assetClass == AssetClass.Crypto
                ? open.AddDays(1)
                : NextMarketWeekday(open),
            TimeFrame.W1 => assetClass == AssetClass.Crypto
                ? open.AddDays(7)
                : NextCalendarWeek(open),
            TimeFrame.MN1 => open.AddMonths(1),
            _ => throw new ArgumentOutOfRangeException(
                nameof(timeFrame),
                timeFrame,
                "Unknown time frame has no bar cadence."),
        };

    /// <summary>The first session weekday strictly after the open:</summary>
    private static DateTimeOffset NextMarketWeekday(DateTimeOffset open)
    {
        var next = open.AddDays(1);
        while (next.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            next = next.AddDays(1);
        }

        return next;
    }

    /// <summary>The Monday open of the following week (a non-Monday weekly
    /// open walks onto the first Monday — the stock-style venues anchor
    /// weekly candles on the session week's first day).</summary>
    private static DateTimeOffset NextCalendarWeek(DateTimeOffset open)
    {
        var next = open.AddDays(7);
        while (next.DayOfWeek != DayOfWeek.Monday)
        {
            next = next.AddDays(1);
        }

        return next;
    }
}
