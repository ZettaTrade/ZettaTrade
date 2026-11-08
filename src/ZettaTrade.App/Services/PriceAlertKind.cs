namespace ZettaTrade.App.Services;

/// <summary>
/// What a local price alert evaluates (T-096). Both kinds ride the adapters'
/// EXISTING live ticker stream (the last price the venue already pushes) —
/// the alerts module starts no polling loop of its own.
/// </summary>
public enum PriceAlertKind
{
    /// <summary>A price-level trigger: the observed last price is judged against one level exactly per <see cref="AlertTrigger"/>.</summary>
    Price,

    /// <summary>A relative-move trigger: the observed last price moved from any sample retained inside the alert's trailing window by at least the alert's percent (either direction).</summary>
    PercentMove,
}
