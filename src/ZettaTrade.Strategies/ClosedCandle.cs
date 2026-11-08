namespace ZettaTrade.Strategies;

using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// One CLOSED candle addressed to its series: the host's input event. The
/// upstream owner of the feed (the app wiring, from the next hosting task
/// on) guarantees the candle is final - evaluate on candle close means the
/// host never judges an open bar - and that candles arrive in ascending
/// open-time order per (symbol, interval); older or duplicate deliveries
/// are ignored by the host, never re-evaluated.
/// </summary>
/// <param name="Symbol">The trading symbol the candle belongs to.</param>
/// <param name="Interval">The candle interval the candle was aggregated by.</param>
/// <param name="Bar">The closed OHLCV bar (open time + final OHLCV).</param>
public readonly record struct ClosedCandle(
    Symbol Symbol,
    TimeFrame Interval,
    Bar Bar);
