namespace ZettaTrade.UI.Charting;

using System;

/// <summary>
/// The calendar unit a time-axis rung counts in: clock multiples anchor on
/// the epoch (they divide the day evenly), day multiples land on whole
/// dates, weeks anchor on Mondays, months on month starts and years on
/// January 1st — the exchange-standard calendar scales. A uniform epoch
/// multiple half a year long drifts backward day by day (the user-reported
/// "февр. 6 → февр. 5 → февр. 3" reverse drift); calendar scales cannot.
/// </summary>
public enum TimeTickScale
{
    Clock = 0,
    Day = 1,
    Week = 2,
    Month = 3,
    Year = 4
}

/// <summary>
/// One candidate time-axis rung. The NOMINAL length drives the step picker
/// (months and years have uneven real lengths, so the count fit test uses
/// the nominal one) and (<see cref="Scale"/>, <see cref="Multiple"/>) drive
/// the calendar anchoring of the tick positions.
/// </summary>
public readonly record struct TimeTickRung(
    TimeSpan Nominal,
    TimeTickScale Scale,
    int Multiple);
