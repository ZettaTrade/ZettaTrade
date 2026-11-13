namespace ZettaTrade.App.Services;

/// <summary>
/// What a local price alert evaluates (T-096). The first two kinds ride the
/// adapters' EXISTING live ticker stream (the last price the venue already
/// pushes) — the alerts module starts no polling loop of its own. The
/// indicator-cross kind (T-203) rides the same ticker stream: the engine
/// aggregates its ticks into one-minute candles and evaluates the cross on
/// every candle close.
/// </summary>
public enum PriceAlertKind
{
    /// <summary>A price-level trigger: the observed last price is judged against one level exactly per <see cref="AlertTrigger"/>.</summary>
    Price,

    /// <summary>A relative-move trigger: the observed last price moved from any sample retained inside the alert's trailing window by at least the alert's percent (either direction).</summary>
    PercentMove,

    /// <summary>An indicator-cross trigger (T-203): on every one-minute candle close the alert's
    /// <see cref="AlertIndicatorSpec"/> source (the indicator's value, or the close against the
    /// indicator) crosses the level per the stored <see cref="AlertTrigger"/> edge — Above is a
    /// cross from strictly below to at-or-above, Below the mirror. The trigger carries the
    /// cross's direction only; the <see cref="AlertTrigger.At"/> edge is meaningless here and is
    /// refused at draft validation.</summary>
    IndicatorCross,
}
