namespace ZettaTrade.UI.Charting;

using System;
using System.Collections.Generic;
using System.Globalization;

/// <summary>
/// Pure axis-label math for the Skia candlestick chart: rung selection on the
/// time ladder, calendar-anchored tick positions, label texts and budgets are
/// decided here (not on the canvas) so the adaptive re-bucketing is
/// unit-testable without pixels — the chart control is UIA-invisible. The
/// draw pass consumes these results unchanged.
/// </summary>
public static class ChartAxisFormatting
{
    #region Constants

    /// <summary>
    /// The default label budget, used by the draw pass before the first
    /// measured label width is known.
    /// </summary>
    public const int DefaultTimeTickBudget = 10;

    /// <summary>The lowest label budget the pickers accept.</summary>
    public const int MinTimeTickBudget = 4;

    /// <summary>The highest label budget the pickers accept.</summary>
    public const int MaxTimeTickBudget = 20;

    /// <summary>
    /// Spans wider than this print month labels with a year: a yearless
    /// "февр." repeated across three different years was indistinguishable
    /// (the user-reported coarse-end defect).
    /// </summary>
    private static readonly TimeSpan MultiYearSpanThreshold = TimeSpan.FromDays(365);

    /// <summary>
    /// Multi-year span labels read month + short year ("февр. '25"): the
    /// backslash escape renders ONE visible apostrophe, while quoting pairs
    /// render nothing at the format's top level ("февр. 25"). Verbatim
    /// literal: in a plain one the C# escape would eat the backslash.
    /// </summary>
    private const string MultiYearLabelFormat = @"MMM \'yy";

    /// <summary>
    /// The in-day clock label ("23:45") of the default 24-hour convention.
    /// </summary>
    private const string ClockLabelFormat = "HH:mm";

    /// <summary>
    /// The half-day clock label ("11:45 PM") of the user-selected 12-hour
    /// format, culture-mapped. No designator fallback: ICU's date data ships
    /// the AM/PM designators for every installed culture on .NET 10 (verified
    /// empirically — zero designatorless cultures; the Latin "AM"/"PM" comes
    /// even for the languages whose legacy NLS data had none, Russian
    /// included), so the glyphs below always read the half-day convention.
    /// </summary>
    private const string TwelveHourLabelFormat = "h:mm tt";

    /// <summary>
    /// Candidate rungs, fine to coarse. Clock multiples divide the day
    /// evenly, so epoch anchoring lands them on the :00/:30 lattice; day
    /// multiples land on whole dates. The calendar scales replace the old
    /// uniform epoch multiples, whose half-year grid drifted the day-of-month
    /// BACKWARD cycle by cycle ("февр. 6 → 5 → 3") and sliced arbitrary
    /// weekdays between the Monday-opened weekly candles: weeks anchor on
    /// Mondays, months on month starts, years on January 1st.
    /// </summary>
    private static readonly TimeTickRung[] TimeRungs =
    [
        new(TimeSpan.FromMinutes(1), TimeTickScale.Clock, 1),
        new(TimeSpan.FromMinutes(2), TimeTickScale.Clock, 2),
        new(TimeSpan.FromMinutes(3), TimeTickScale.Clock, 3),
        new(TimeSpan.FromMinutes(5), TimeTickScale.Clock, 5),
        new(TimeSpan.FromMinutes(10), TimeTickScale.Clock, 10),
        new(TimeSpan.FromMinutes(15), TimeTickScale.Clock, 15),
        new(TimeSpan.FromMinutes(30), TimeTickScale.Clock, 30),
        new(TimeSpan.FromHours(1), TimeTickScale.Clock, 1),
        new(TimeSpan.FromHours(2), TimeTickScale.Clock, 2),
        new(TimeSpan.FromHours(3), TimeTickScale.Clock, 3),
        new(TimeSpan.FromHours(4), TimeTickScale.Clock, 4),
        new(TimeSpan.FromHours(6), TimeTickScale.Clock, 6),
        new(TimeSpan.FromHours(12), TimeTickScale.Clock, 12),
        new(TimeSpan.FromDays(1), TimeTickScale.Day, 1),
        new(TimeSpan.FromDays(2), TimeTickScale.Day, 2),
        new(TimeSpan.FromDays(7), TimeTickScale.Week, 1),
        new(TimeSpan.FromDays(14), TimeTickScale.Week, 2),
        new(TimeSpan.FromDays(30), TimeTickScale.Month, 1),
        new(TimeSpan.FromDays(91), TimeTickScale.Month, 3),
        new(TimeSpan.FromDays(182), TimeTickScale.Month, 6),
        new(TimeSpan.FromDays(365), TimeTickScale.Year, 1),
        new(TimeSpan.FromDays(730), TimeTickScale.Year, 2),
        new(TimeSpan.FromDays(1825), TimeTickScale.Year, 5),
        new(TimeSpan.FromDays(3650), TimeTickScale.Year, 10),
    ];

    /// <summary>
    /// The Unix epoch: every clock and day multiple anchors on its UTC tick
    /// lattice, so panning and re-bucketing never slides the lines.
    /// </summary>
    private static readonly DateTimeOffset EpochAnchor = new(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The first Monday of the Unix epoch: week multiples anchor on it, so
    /// week grids ride the Monday opens the weekly candles actually have.
    /// </summary>
    private static readonly DateTimeOffset WeekAnchor = new(1970, 1, 5, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Money labels never drop below two decimal digits.</summary>
    private const int MinPriceDecimals = 2;

    /// <summary>Beyond this many digits even sub-penny quotes stop reading.</summary>
    private const int MaxPriceDecimals = 8;

    #endregion

    #region Time Axis

    /// <summary>
    /// The finest rung whose NOMINAL length labels a whole span with at most
    /// the budget of ticks; this is what makes the wheel zoom re-bucket the
    /// axis. Calendar scales have uneven real lengths (a quarter really is
    /// 91.31 days, February is 28), so the fit is nominal and
    /// <see cref="PickTimeTicks"/> clamps the real slot list to the budget.
    /// The budget clamps into the [<see cref="MinTimeTickBudget"/>,
    /// <see cref="MaxTimeTickBudget"/>] band; a degenerate span picks the
    /// finest rung.
    /// </summary>
    public static TimeTickRung PickTimeStep(TimeSpan span, int budget)
    {
        var limit = Math.Clamp(budget, MinTimeTickBudget, MaxTimeTickBudget);

        foreach (var rung in TimeRungs)
        {
            if (span <= TimeSpan.FromTicks(rung.Nominal.Ticks * limit))
            {
                return rung;
            }
        }

        return TimeRungs[^1];
    }

    /// <summary>
    /// Calendar-anchored tick times for one rung inside the visible window,
    /// ascending. Alignment follows the rung's scale: clock and day multiples
    /// anchor on the Unix epoch (absolute-time lattice, so sub-day steps land
    /// on clock boundaries and day steps on UTC midnights regardless of the
    /// window's offset), week multiples on the first Monday of the epoch,
    /// month rungs on the month starts their ordinal multiple hits, year
    /// rungs on January 1st of the divisible years. Ticks stay inside the
    /// window — a slot before the first bar drops out, one at the last bar's
    /// open stays — so a window straddling no slot yields an EMPTY list
    /// (honest silence beats an overflowing label, kept from the T-035 rule).
    /// Real calendar lengths can spill a slot past the nominal count fit; the
    /// OLDEST ticks then drop so the fresh right edge keeps its grid.
    /// </summary>
    public static IReadOnlyList<DateTimeOffset> PickTimeTicks(
        DateTimeOffset first,
        DateTimeOffset last,
        TimeTickRung rung,
        int budget)
    {
        if (last < first || rung.Nominal <= TimeSpan.Zero || rung.Multiple < 1)
        {
            return [];
        }

        var limit = Math.Clamp(budget, MinTimeTickBudget, MaxTimeTickBudget);
        var slots = CollectSlots(first, last, rung);

        return slots.Count <= limit
            ? slots
            : slots.GetRange(slots.Count - limit, limit);
    }

    /// <summary>
    /// The measured label budget: how many labels of at most
    /// <paramref name="maxLabelWidth"/> px (plus the per-label gap) fit onto
    /// a plot of <paramref name="plotWidth"/> px, clamped into the slicing
    /// band. Pure so the draw pass calibrates it against the on-screen font;
    /// a degenerate measure falls back to the minimum budget.
    /// </summary>
    public static int TimeTickBudgetFor(
        double plotWidth,
        double maxLabelWidth,
        double labelGap)
    {
        var step = maxLabelWidth + labelGap;

        if (step <= 0)
        {
            return MinTimeTickBudget;
        }

        var budget = (int)Math.Floor((plotWidth - maxLabelWidth) / step);

        return Math.Clamp(budget, MinTimeTickBudget, MaxTimeTickBudget);
    }

    /// <summary>
    /// The label for one time tick, span-aware because a yearless month name
    /// repeated across different years is unreadable: year-scale rungs print
    /// the bare year; spans wider than one year print month + short year
    /// ("февр. '25"); the rest print month + day for calendar ticks and
    /// coinciding midnights (so an intraday grid keeps its day context at
    /// midnight), clock times otherwise. Culture follows the app language
    /// (the caller passes <see cref="ChartTickCulture"/>), and the clock
    /// itself prints 24-hour by default or the user-selected 12-hour one.
    /// </summary>
    public static string FormatTimeTick(
        DateTimeOffset tick,
        TimeTickRung rung,
        TimeSpan span,
        IFormatProvider culture,
        bool use12HourClock)
    {
        if (rung.Scale == TimeTickScale.Year)
        {
            return tick.ToString("yyyy", culture);
        }

        if (span > MultiYearSpanThreshold)
        {
            return tick.ToString(MultiYearLabelFormat, culture);
        }

        if (rung.Scale != TimeTickScale.Clock || tick.TimeOfDay == TimeSpan.Zero)
        {
            return tick.ToString("MMM d", culture);
        }

        return use12HourClock
            ? tick.ToString(TwelveHourLabelFormat, culture)
            : tick.ToString(ClockLabelFormat, culture);
    }

    /// <summary>
    /// Rung dispatch: one slot enumerator per scale.
    /// </summary>
    private static List<DateTimeOffset> CollectSlots(
        DateTimeOffset first,
        DateTimeOffset last,
        TimeTickRung rung) => rung.Scale switch
        {
            TimeTickScale.Clock or TimeTickScale.Day => EpochSlots(first, last, rung.Nominal, EpochAnchor),
            TimeTickScale.Week => EpochSlots(first, last, rung.Nominal, WeekAnchor),
            TimeTickScale.Month => MonthSlots(first, last, rung.Multiple),
            TimeTickScale.Year => YearSlots(first, last, rung.Multiple),
            _ => throw new ArgumentOutOfRangeException(nameof(rung), rung.Scale, "Unknown time tick scale.")
        };

    /// <summary>
    /// Multiples of one nominal length anchored on an absolute-time anchor,
    /// from the first slot inside the window up to the last one at or before
    /// it (integer tick math, no floating drift). Ticks render in the
    /// window's offset.
    /// </summary>
    private static List<DateTimeOffset> EpochSlots(
        DateTimeOffset first,
        DateTimeOffset last,
        TimeSpan nominal,
        DateTimeOffset anchor)
    {
        var stepTicks = nominal.Ticks;
        var slot = CeilDiv(first.UtcTicks - anchor.UtcTicks, stepTicks);
        var list = new List<DateTimeOffset>();

        for (; anchor.UtcTicks + (slot * stepTicks) <= last.UtcTicks; slot++)
        {
            list.Add(new DateTimeOffset(anchor.UtcTicks + (slot * stepTicks), TimeSpan.Zero).ToOffset(first.Offset));
        }

        return list;
    }

    /// <summary>
    /// Month starts every <paramref name="multiple"/> months, counted on the
    /// calendar since January of year 1 (a 3-multiple grid is Jan/Apr/Jul/
    /// Oct, a 6-multiple is Jan/Jul). The slot covering the window's opening
    /// month counts as a candidate: its start may sit before the first bar,
    /// and the validity filter drops it.
    /// </summary>
    private static List<DateTimeOffset> MonthSlots(
        DateTimeOffset first,
        DateTimeOffset last,
        int multiple)
    {
        var list = new List<DateTimeOffset>();
        var firstOrdinal = (first.Year - 1L) * 12 + (first.Month - 1);
        var lastOrdinal = (last.Year - 1L) * 12 + (last.Month - 1);

        for (var slot = (firstOrdinal / multiple) * multiple; slot <= lastOrdinal; slot += multiple)
        {
            var monthStart = new DateTimeOffset(
                (int)((slot / 12) + 1),
                (int)((slot % 12) + 1),
                1,
                0,
                0,
                0,
                first.Offset);

            if (monthStart >= first && monthStart <= last)
            {
                list.Add(monthStart);
            }
        }

        return list;
    }

    /// <summary>
    /// January 1st of every year the rung's multiple hits, from the first
    /// divisible year covering the window's opening year (its start may sit
    /// before the first bar and drops out) through the last one at or before
    /// the closing bar.
    /// </summary>
    private static List<DateTimeOffset> YearSlots(
        DateTimeOffset first,
        DateTimeOffset last,
        int multiple)
    {
        var list = new List<DateTimeOffset>();

        for (
            var year = Math.Max(1, (first.Year / multiple) * multiple);
            year <= last.Year;
            year += multiple)
        {
            var newYear = new DateTimeOffset(year, 1, 1, 0, 0, 0, first.Offset);

            if (newYear >= first && newYear <= last)
            {
                list.Add(newYear);
            }
        }

        return list;
    }

    /// <summary>
    /// Integer ceiling of a division, exact on both signs (Math.DivRem
    /// truncates toward zero, so only a positive remainder rounds up).
    /// </summary>
    private static long CeilDiv(long numerator, long denominator)
    {
        var quotient = Math.DivRem(numerator, denominator, out var remainder);

        if (remainder > 0)
        {
            quotient++;
        }

        return quotient;
    }

    #endregion

    #region Price Axis

    /// <summary>
    /// Even price ticks spanning <c>[min, max]</c> inclusive — the same
    /// positions the horizontal grid maps, so lines and labels agree. The
    /// callers pass the PADDED price extremes of the visible window.
    /// </summary>
    public static IReadOnlyList<decimal> PickPriceTicks(
        decimal min,
        decimal max,
        int slotCount)
    {
        var ticks = new decimal[slotCount + 1];
        for (var slot = 0; slot <= slotCount; slot++)
        {
            ticks[slot] = min + ((max - min) * slot / slotCount);
        }

        return ticks;
    }

    /// <summary>
    /// The smallest decimal digit count that keeps adjacent tick labels
    /// distinct for the given step: the step's decimal magnitude, clamped to
    /// money conventions — never below two digits, never above eight. A
    /// non-positive step (a flat visible window) keeps the two-digit default;
    /// every label is then the same price by definition.
    /// </summary>
    public static int PickPriceDecimals(decimal step)
    {
        if (step <= 0)
        {
            return MinPriceDecimals;
        }

        var magnitude = (int)Math.Ceiling(-Math.Log10((double)step));

        return Math.Clamp(magnitude, MinPriceDecimals, MaxPriceDecimals);
    }

    /// <summary>
    /// The label text for one price tick, culture-aware (the ru locale shows
    /// the decimal comma the users' screenshots count on).
    /// </summary>
    public static string FormatPriceTick(
        decimal price,
        int decimals,
        IFormatProvider culture)
    {
        var format = FormattableString.Invariant($"F{decimals}");
        return price.ToString(format, culture);
    }

    #endregion
}
