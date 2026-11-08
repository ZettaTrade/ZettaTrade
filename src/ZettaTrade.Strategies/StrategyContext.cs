namespace ZettaTrade.Strategies;

using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The evaluation input one strategy sees per closed candle: the series
/// identity plus the window of closed bars the host retained, oldest
/// first, the just-closed candle last. The window is read-only fuel - an
/// implementation must never mutate it - and it is capped by the host's
/// retained window length, so an implementation's warmup needs are its
/// own responsibility to express through <see cref="StrategyStance.Neutral"/>
/// when the window is too short to judge.
/// </summary>
/// <param name="Symbol">The trading symbol the window belongs to.</param>
/// <param name="Interval">The candle interval the window was aggregated by.</param>
/// <param name="ClosedBars">The closed bars, oldest first (the last entry is the just-closed candle).</param>
public sealed record StrategyContext(
    Symbol Symbol,
    TimeFrame Interval,
    IReadOnlyList<Bar> ClosedBars);
