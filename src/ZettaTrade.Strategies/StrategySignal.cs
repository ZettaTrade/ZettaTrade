namespace ZettaTrade.Strategies;

using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// One strategy-signal event the host emits per (strategy, symbol,
/// interval) evaluation: the stamped output of a candle-close evaluation.
/// The host owns stamping - the <see cref="StrategyId"/>, <see cref="Symbol"/>,
/// <see cref="Interval"/> and <see cref="Candle"/> always come from the host's
/// own bookkeeping, so a strategy can never fabricate an identity. The
/// channel carrying these is inert output for later UI tasks: read only,
/// never consumed for trading here.
/// </summary>
/// <param name="StrategyId">The canonical id of the strategy that produced the stance.</param>
/// <param name="Symbol">The trading symbol the evaluated candle belongs to.</param>
/// <param name="Interval">The candle interval the evaluation ran on.</param>
/// <param name="Candle">The closed candle the stance was computed from.</param>
/// <param name="Stance">The strategy's stance at this candle (neutral, long or flat).</param>
public readonly record struct StrategySignal(
    string StrategyId,
    Symbol Symbol,
    TimeFrame Interval,
    Bar Candle,
    StrategyStance Stance);
