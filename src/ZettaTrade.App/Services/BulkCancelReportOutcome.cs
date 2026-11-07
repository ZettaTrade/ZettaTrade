namespace ZettaTrade.App.Services;

/// <summary>
/// Terminal state of ONE order inside a bulk cancel pass.
/// </summary>
public enum BulkCancelReportOutcome
{
    /// <summary>The cancel command reached the trading adapter and was accepted.</summary>
    Cancelled,

    /// <summary>
    /// The order was working, but an in-flight panel pipeline already owns it
    /// (a per-row cancel or an amend): the pass skips it and reports instead
    /// of double-commanding it.
    /// </summary>
    SkippedInFlight,

    /// <summary>
    /// The cancel command faulted; the pass carried on with the remaining
    /// orders (one adapter fault never stops the others).
    /// </summary>
    Failed,

    /// <summary>The pass stopped before the order's command was ever sent.</summary>
    NotAttempted,
}
