namespace ZettaTrade.App.Services;

/// <summary>
/// What a local price alert evaluates (T-096). The first two kinds ride the
/// adapters' EXISTING live ticker stream (the last price the venue already
/// pushes) — the alerts module starts no polling loop of its own. The
/// indicator-cross kind (T-203) rides the same ticker stream: the engine
/// aggregates its ticks into one-minute candles and evaluates the cross on
/// every candle close. The whale-print kind (T-245) is the one kind on its
/// OWN stream: the adapters' existing trade stream (the same prints the
/// tape shows), because a print's size is a fact the ticker never carries.
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

    /// <summary>A funding-rate-cross trigger (T-240): the observed funding rate crosses the
    /// alert's signed threshold between two consecutive ticker observations — Above is a cross
    /// from strictly below to at-or-above, Below the mirror. The threshold rides
    /// <see cref="AlertDefinition.TargetPrice"/> in PERCENT units (the same percent the ticker
    /// cell displays; 0.05 = 0.05%) and the engine converts it once at evaluation; zero and
    /// negatives are honest thresholds. The trigger carries the cross's direction only; the
    /// <see cref="AlertTrigger.At"/> edge is meaningless here and is refused at draft
    /// validation. The evaluation rides the adapters' existing ticker stream — the funding
    /// rate the venue already pushes — no polling of its own.</summary>
    FundingCross,

    /// <summary>A big-trade trigger (T-245): a single print whose notional (price × quantity)
    /// is strictly above the alert's threshold. The threshold rides
    /// <see cref="AlertDefinition.TargetPrice"/> in absolute quote-currency units (100000 =
    /// 100 000 USDT on a USDT-quoted symbol) and the trigger carries no edge beyond
    /// <see cref="AlertTrigger.Above"/> — a trade alert reads "bigger than", so At and Below
    /// are meaningless and are refused at draft validation. The evaluation rides the
    /// adapters' existing trade stream — the same prints the tape shows — no polling and
    /// no second feed of its own; the engine's one trade subscription per armed symbol is
    /// the kind's own necessary cost (the tape filter shares the venue's tape feed, never
    /// the engine's).</summary>
    WhalePrint,
}
