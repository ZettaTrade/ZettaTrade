namespace ZettaTrade.App.Services;

/// <summary>
/// A persisted alert's lifecycle state (T-096, T-203). Armed alerts evaluate
/// on every tick; a snoozed alert skips evaluation until its window expires
/// and arms itself again on the next arriving tick (no wake timers — the
/// engine stays purely stream-driven); Fired is terminal: the alert keeps
/// its row in the manager (the honest record of what happened) but is never
/// evaluated again, so a reload can never fire it a second time; Paused
/// (T-203) is the indicator-cross alert's honest stall — the plugin behind
/// its indicator is not loaded, so the alert cannot be honored. A paused
/// alert keeps its row (it does not die): the engine pauses it at the first
/// close it cannot resolve, and a session whose plugin set restores the
/// indicator re-arms it at the roster load.
/// </summary>
public enum AlertStatus
{
    /// <summary>The alert evaluates on every tick of its symbol's ticker stream.</summary>
    Armed,

    /// <summary>The alert skips evaluation until <see cref="AlertDefinition.SnoozedUntilUtc"/>; then it arms again on the next tick.</summary>
    Snoozed,

    /// <summary>The alert fired once; the row stays for the record, evaluation is over.</summary>
    Fired,

    /// <summary>The indicator-cross alert's plugin is not loaded (T-203): the row stays, evaluation stalls until the plugin's set restores the indicator (the next session's roster load re-arms it).</summary>
    Paused,
}
