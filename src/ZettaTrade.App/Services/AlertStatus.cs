namespace ZettaTrade.App.Services;

/// <summary>
/// A persisted alert's lifecycle state (T-096). Armed alerts evaluate on
/// every tick; a snoozed alert skips evaluation until its window expires and
/// arms itself again on the next arriving tick (no wake timers — the engine
/// stays purely stream-driven); Fired is terminal: the alert keeps its row
/// in the manager (the honest record of what happened) but is never
/// evaluated again, so a reload can never fire it a second time.
/// </summary>
public enum AlertStatus
{
    /// <summary>The alert evaluates on every tick of its symbol's ticker stream.</summary>
    Armed,

    /// <summary>The alert skips evaluation until <see cref="AlertDefinition.SnoozedUntilUtc"/>; then it arms again on the next tick.</summary>
    Snoozed,

    /// <summary>The alert fired once; the row stays for the record, evaluation is over.</summary>
    Fired,
}
