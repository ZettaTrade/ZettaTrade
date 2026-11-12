namespace ZettaTrade.App.Services;

/// <summary>
/// What one kill-switch item became (T-188): the per-item outcome the
/// honest summary reports. One item's fault produces exactly its own
/// report — the sweep continues with the rest either way.
/// </summary>
public enum KillSwitchItemOutcome
{
    /// <summary>The working order's cancel command was accepted.</summary>
    OrderCancelled,

    /// <summary>The position's reduce-only market exit placed (the order id rides the report).</summary>
    PositionClosed,

    /// <summary>The position the plan named was already gone at the fresh re-read.</summary>
    AlreadyFlat,

    /// <summary>The item faulted; the fault text rides the report.</summary>
    Failed,

    /// <summary>The sweep stopped before the item was commanded.</summary>
    NotAttempted,
}
