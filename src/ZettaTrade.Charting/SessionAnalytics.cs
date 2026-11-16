namespace ZettaTrade.Charting;

using System;
using ZettaTrade.Core.Entities;

/// <summary>The UTC session window a bar's open falls into (T-247).</summary>
public enum SessionSlot
{
    /// <summary>No session window owns the bar (never produced by the shipped UTC partition — kept for the honest extensibility).</summary>
    None,

    /// <summary>The [00:00, 08:00) UTC window.</summary>
    Asia,

    /// <summary>The [08:00, 16:00) UTC window.</summary>
    Europe,

    /// <summary>The [16:00, 24:00) UTC window.</summary>
    Us,
}

/// <summary>Whether the session analytics carry a computable series.</summary>
public enum SessionAnalyticsState
{
    /// <summary>The series carries bars — the arrays align with it one slot per bar.</summary>
    Computed,

    /// <summary>The series was empty — the arrays are empty and the draw pass draws nothing.</summary>
    EmptySeries,
}

/// <summary>
/// The session analytics overlay (T-247): the daily and weekly open lines,
/// the opening-range band and the session shading, computed over the loaded
/// bar series the chart already carries — no transport, no clock, no
/// scheduler. Static and side-effect free so the goldens pin the semantics
/// without a feed (the <see cref="VwapOverlay"/> discipline).
/// <para>
/// The math, exactly: a DAY breaks wherever the bars' UTC opens cross a
/// calendar-date boundary (the <see cref="VwapSessionAnchor"/> rule — the
/// session anchor the VWAP accumulation already rides, so the daily open
/// line and the VWAP reset cut on the same boundaries by construction); the
/// day's open is its first bar's open and rides every bar of that day (the
/// drawn line is flat inside the day and never connects across the
/// boundary — the draw pass segments per value run). A WEEK breaks wherever
/// the bars' UTC dates cross a Monday-anchored week boundary (the T-037
/// calendar's own weekly anchor: <c>NextCalendarWeek</c> walks onto the
/// session week's first day, so a stock series' Friday→Monday weekend skip
/// lands the weekly open on the Monday bar and a holiday-shifted Tuesday
/// opener is still the week's honest first bar — nothing outside the
/// loaded window is ever assumed, the mid-week series head anchors its
/// partial week at its own first bar). The OPENING RANGE weighs the bars
/// whose opens fall inside <c>[dayStart, dayStart + minutes)</c> — the
/// day's highest high and lowest low among them, and the window's FINAL
/// shape backfills every bar of the owning day (one band shape per day); a
/// day with no bar inside the window (a sparse series whose first bar
/// opens past the range) carries the honest nulls and the band skips the
/// day. The SHADING maps every bar's open time-of-day into
/// the fixed UTC partition Asia [00:00, 08:00), Europe [08:00, 16:00), US
/// [16:00, 24:00) — UTC-anchored for every venue class (the venue's own
/// quiet hours carry no bars, so a stock's weekend and its local session
/// shape ride the bars' presence, and a DST shift shows up as the honest
/// UTC drift of the session mix, never a fabricated alignment).
/// </para>
/// </summary>
public sealed record SessionAnalytics(
    SessionAnalyticsState State,
    IReadOnlyList<decimal> DailyOpens,
    IReadOnlyList<decimal> WeeklyOpens,
    IReadOnlyList<decimal?> RangeHigh,
    IReadOnlyList<decimal?> RangeLow,
    IReadOnlyList<SessionSlot> Slots)
{
    /// <summary>The default opening-range window (minutes) when no configuration says otherwise.</summary>
    public const int DefaultOpeningRangeMinutes = 30;

    /// <summary>The opening-range window's legal bounds (minutes): one minute to one full day.</summary>
    public const int MinOpeningRangeMinutes = 1;

    /// <summary>The opening-range window's legal bounds (minutes): one minute to one full day.</summary>
    public const int MaxOpeningRangeMinutes = 1440;

    /// <summary>
    /// Compute the session analytics for the loaded series. One O(N) pass,
    /// no per-bar allocations beyond the aligned arrays (the compute runs
    /// per bars-applied or per toggle, never per frame).
    /// </summary>
    /// <param name="bars">The displayed ascending bar series.</param>
    /// <param name="openingRangeMinutes">The range window in minutes; clamped into the legal bounds.</param>
    public static SessionAnalytics Compute(IReadOnlyList<Bar> bars, int openingRangeMinutes)
    {
        ArgumentNullException.ThrowIfNull(bars);

        var minutes = Math.Clamp(openingRangeMinutes, MinOpeningRangeMinutes, MaxOpeningRangeMinutes);

        if (bars.Count == 0)
        {
            return new SessionAnalytics(
                SessionAnalyticsState.EmptySeries,
                [],
                [],
                [],
                [],
                []);
        }

        var dailyOpens = new decimal[bars.Count];
        var weeklyOpens = new decimal[bars.Count];
        var rangeHigh = new decimal?[bars.Count];
        var rangeLow = new decimal?[bars.Count];
        var slots = new SessionSlot[bars.Count];

        var currentDay = bars[0].Time.UtcDateTime.Date;
        var currentWeek = WeekStart(currentDay);
        var dayStart = 0;
        var dayOpen = bars[0].Open;
        var weekOpen = bars[0].Open;
        var dayRangeDeadline = currentDay.AddMinutes(minutes);
        var orHigh = decimal.MinValue;
        var orLow = decimal.MaxValue;

        for (var i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            var utc = bar.Time.UtcDateTime;
            var date = utc.Date;
            var week = WeekStart(date);

            if (date != currentDay)
            {
                // The day break (the anchor rule): the closing day's range
                // window settles onto its final shape (the backfill below),
                // then the new day's first bar opens the day's line and
                // restarts the range window.
                CloseRangeWindow(rangeHigh, rangeLow, dayStart, i, orHigh, orLow);
                currentDay = date;
                dayStart = i;
                dayOpen = bar.Open;
                dayRangeDeadline = date.AddMinutes(minutes);
                orHigh = decimal.MinValue;
                orLow = decimal.MaxValue;
            }

            if (utc < dayRangeDeadline)
            {
                orHigh = orHigh > bar.High ? orHigh : bar.High;
                orLow = orLow < bar.Low ? orLow : bar.Low;
            }

            if (week != currentWeek)
            {
                currentWeek = week;
                weekOpen = bar.Open;
            }

            dailyOpens[i] = dayOpen;
            weeklyOpens[i] = weekOpen;
            slots[i] = SlotOf(utc.TimeOfDay);
        }

        // The series tail closes its own day the same way the day breaks do.
        CloseRangeWindow(rangeHigh, rangeLow, dayStart, bars.Count, orHigh, orLow);

        return new SessionAnalytics(
            SessionAnalyticsState.Computed,
            dailyOpens,
            weeklyOpens,
            rangeHigh,
            rangeLow,
            slots);
    }

    /// <summary>
    /// Slice the analytics with the SAME [start, end) window the bars slice
    /// uses (the draw pass's alignment rule): the arrays shrink together
    /// with the visible bars, so a length mismatch can never reach the draw
    /// pass as a silently shifted polyline.
    /// </summary>
    /// <param name="start">The window's first bar index.</param>
    /// <param name="end">The index one past the window's last bar.</param>
    /// <returns>The window-scoped analytics.</returns>
    public SessionAnalytics Slice(int start, int end)
    {
        if (start < 0 || start > end || end > DailyOpens.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(start), start, "The window is not inside the analytics' value range.");
        }

        var count = end - start;
        return new SessionAnalytics(
            State,
            CopyRange(DailyOpens, start, count),
            CopyRange(WeeklyOpens, start, count),
            CopyRange(RangeHigh, start, count),
            CopyRange(RangeLow, start, count),
            CopyRange(Slots, start, count));
    }

    /// <summary>
    /// Settle the closed day's range window: the window's final (high, low)
    /// pair backfills every bar of the owning day — a bar inside the forming
    /// window carries the day's final range too, so the drawn band is one
    /// shape per day — and the honest null pair fills the day when no bar
    /// weighed.
    /// </summary>
    private static void CloseRangeWindow(
        decimal?[] rangeHigh,
        decimal?[] rangeLow,
        int startInclusive,
        int endExclusive,
        decimal high,
        decimal low)
    {
        var hasRange = low <= high;
        for (var i = startInclusive; i < endExclusive; i++)
        {
            rangeHigh[i] = hasRange ? high : null;
            rangeLow[i] = hasRange ? low : null;
        }
    }

    /// <summary>The Monday 00:00 UTC of the given UTC date's week (the T-037 weekly anchor).</summary>
    private static DateTime WeekStart(DateTime date) =>
        date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    /// <summary>The fixed UTC partition: Asia [00:00, 08:00), Europe [08:00, 16:00), US [16:00, 24:00).</summary>
    private static SessionSlot SlotOf(TimeSpan timeOfDay) => timeOfDay.Hours switch
    {
        < 8 => SessionSlot.Asia,
        < 16 => SessionSlot.Europe,
        _ => SessionSlot.Us,
    };

    /// <summary>Copy [start, start + count) of a value list into a fresh array.</summary>
    private static T[] CopyRange<T>(IReadOnlyList<T> values, int start, int count)
    {
        var copy = new T[count];
        for (var i = 0; i < count; i++)
        {
            copy[i] = values[start + i];
        }

        return copy;
    }
}
