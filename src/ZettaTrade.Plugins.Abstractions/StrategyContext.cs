namespace ZettaTrade.Strategies;

using ZettaTrade.Plugins.Abstractions;

/// <summary>
/// The evaluation input one strategy sees per closed candle (contract
/// 2.1.0, T-168): the series identity plus the window of closed bars the
/// host retained, oldest first, the just-closed candle last. The window is
/// read-only fuel - an implementation must never mutate it - and it is
/// capped by the host's retained window length, so an implementation's
/// warmup needs are its own responsibility to express through
/// <see cref="StrategyStance.Neutral"/> when the window is too short to
/// judge.
/// </summary>
/// <remarks>
/// The SDK-owned shape out-of-tree strategies compute against, independent
/// of the application's internal entities: the series identity rides the
/// canonical spellings (the normalized symbol, the interval's name) and
/// the bars are the contract's <see cref="IndicatorBar"/> OHLC shape - the
/// one bar grammar the SDK carries. The host adapts the application's
/// candles once per evaluation; the bar time axis is the ascending order,
/// implementations needing the clock own it from the bar index.
/// </remarks>
/// <param name="Symbol">The canonical symbol spelling the window belongs to (e.g. "BTC/USDT").</param>
/// <param name="Interval">The interval name the window was aggregated by (e.g. "H1").</param>
/// <param name="ClosedBars">The closed bars, oldest first (the last entry is the just-closed candle).</param>
public sealed record StrategyContext(
    string Symbol,
    string Interval,
    IReadOnlyList<IndicatorBar> ClosedBars);
