namespace ZettaTrade.App.Services;

using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// One delivered chart series load with the (symbol, time frame) identity
/// that produced it (T-112): the strategies host's upstream feed address.
/// The chart publishes every full download through this record — a
/// consumer cannot re-derive the bar set's origin from the bars alone, and
/// the feed needs the identity so closed candles enter the host's own
/// (symbol, interval) series untouched.
/// </summary>
/// <param name="Symbol">The trading symbol the page loaded.</param>
/// <param name="TimeFrame">The time frame the page aggregated.</param>
/// <param name="Bars">The delivered ascending bar set.</param>
public sealed record ChartSeriesPage(
    Symbol Symbol,
    TimeFrame TimeFrame,
    IReadOnlyList<Bar> Bars);
