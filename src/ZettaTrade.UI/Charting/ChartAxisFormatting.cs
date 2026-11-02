namespace ZettaTrade.UI.Charting;

using System;
using System.Collections.Generic;

/// <summary>
/// Pure axis-label math for the Skia candlestick chart: tick step selection,
/// tick positions and label texts are decided here (not on the canvas) so the
/// adaptive re-bucketing is unit-testable without pixels — the chart control
/// is UIA-invisible. The draw pass consumes these results unchanged.
/// </summary>
public static class ChartAxisFormatting
{
    #region Constants

    /// <summary>
    /// Label budget for the time axis: the visible span is labeled with at
    /// most this many ticks (the same budget the old per-bar-index ticks had).
    /// </summary>
    private const int TimeTickBudget = 10;

    /// <summary>
    /// Steps at or above this length print the year only: a year-scale grid
    /// is a calendar scale, not a time-of-day one.
    /// </summary>
    private static readonly TimeSpan YearTickThreshold = TimeSpan.FromDays(365);

    /// <summary>Smallest step considered a whole-day (or coarser) grid.</summary>
    private static readonly TimeSpan DayTickThreshold = TimeSpan.FromDays(1);

    /// <summary>
    /// Candidate tick steps, fine to coarse. Every entry below a day divides
    /// 24 h evenly, so aligned ticks land on clock boundaries; the day-plus
    /// entries land on calendar dates. The tail covers monthly and yearly
    /// chart windows (MN1 spans many years).
    /// </summary>
    private static readonly TimeSpan[] TimeLadder =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(3),
        TimeSpan.FromHours(6),
        TimeSpan.FromHours(12),
        TimeSpan.FromDays(1),
        TimeSpan.FromDays(2),
        TimeSpan.FromDays(7),
        TimeSpan.FromDays(14),
        TimeSpan.FromDays(28),
        TimeSpan.FromDays(91),
        TimeSpan.FromDays(182),
        TimeSpan.FromDays(365),
        TimeSpan.FromDays(730),
        TimeSpan.FromDays(1825),
        TimeSpan.FromDays(3650),
    ];

    /// <summary>Money labels never drop below two decimal digits.</summary>
    private const int MinPriceDecimals = 2;

    /// <summary>Beyond this many digits even sub-penny quotes stop reading.</summary>
    private const int MaxPriceDecimals = 8;

    #endregion

    #region Public Methods

    /// <summary>
    /// The finest ladder step that labels the visible span with at most the
    /// tick budget: the span divided by the step must fit the budget. This is
    /// what makes the wheel zoom re-bucket the axis — the wheel widens or
    /// narrows the visible window, and a wider window picks a coarser step.
    /// </summary>
    public static TimeSpan PickTimeStep(TimeSpan span)
    {
        foreach (var step in TimeLadder)
        {
            if (span <= TimeSpan.FromTicks(step.Ticks * TimeTickBudget))
            {
                return step;
            }
        }

        return TimeLadder[^1];
    }

    /// <summary>
    /// Tick times on the step grid inside the visible window, ascending.
    /// The grid anchors on the epoch tick (0001-01-01), so sub-day steps land
    /// on clock boundaries and day steps on whole dates; the arithmetic stays
    /// on the integer tick lattice (decimal division, no floating drift).
    /// Ticks are strictly inside <c>[first, last]</c>; a near-boundary tick
    /// can still hug the plot edge, which the renderer clamps. A span beyond
    /// the ladder keeps only the RIGHTmost budget of ticks (the recent end is
    /// the one that matters).
    /// </summary>
    public static IReadOnlyList<DateTimeOffset> PickTimeTicks(
        DateTimeOffset first,
        DateTimeOffset last,
        TimeSpan step)
    {
        var ticks = new List<DateTimeOffset>();
        if (step.Ticks <= 0 || last.Ticks < first.Ticks)
        {
            return ticks;
        }

        var firstSlot = (long)Math.Ceiling((decimal)first.Ticks / step.Ticks);
        var lastSlot = (long)Math.Floor((decimal)last.Ticks / step.Ticks);

        if (lastSlot - firstSlot + 1 > TimeTickBudget)
        {
            firstSlot = lastSlot - TimeTickBudget + 1;
        }

        for (var slot = firstSlot; slot <= lastSlot; slot++)
        {
            ticks.Add(new DateTimeOffset(slot * step.Ticks, first.Offset));
        }

        return ticks;
    }

    /// <summary>
    /// The label for one time tick. Intraday steps print the clock time and
    /// hand coinciding midnights to the date format (full context retained);
    /// whole-day steps print the month and day; year-scale steps print the
    /// year only. Culture follows the app language (the localization manager
    /// resets CultureInfo on every language switch).
    /// </summary>
    public static string FormatTimeTick(
        DateTimeOffset tick,
        TimeSpan step,
        IFormatProvider culture)
    {
        if (step >= YearTickThreshold)
        {
            return tick.ToString("yyyy", culture);
        }

        if (step >= DayTickThreshold || tick.TimeOfDay == TimeSpan.Zero)
        {
            return tick.ToString("MMM d", culture);
        }

        return tick.ToString("HH:mm", culture);
    }

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
