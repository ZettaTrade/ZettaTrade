namespace ZettaTrade.App.Services;

/// <summary>
/// The price-level trigger's edge vocabulary (T-096). The words are strict
/// comparisons against the one observed last price, decided exactly once in
/// <see cref="AlertEdge"/> — never an accidental epsilon (the acceptance's
/// "edge semantics exact (above/at/below)").
/// </summary>
public enum AlertTrigger
{
    /// <summary>Fires when the observed price goes STRICTLY above the target; a tick exactly at the target does not satisfy Above.</summary>
    Above,

    /// <summary>Fires when the observed price goes STRICTLY below the target; a tick exactly at the target does not satisfy Below.</summary>
    Below,

    /// <summary>Fires when the observed price touches the target EXACTLY (decimal equality, no tolerance — wire prices are tick-exact decimals and any epsilon would silently widen the semantics).</summary>
    At,
}
