namespace ZettaTrade.App.Services;

/// <summary>
/// The whole attempt's terminal state (T-188): a completed sweep (its
/// per-item reports may still carry failures — best effort is honest),
/// a refused confirmation, or a sweep that stopped early.
/// </summary>
public enum KillSwitchOutcome
{
    /// <summary>The sweep ran to its end (per-item failures ride the reports).</summary>
    Done,

    /// <summary>The user refused the confirmation; nothing touched, nothing reported.</summary>
    Declined,

    /// <summary>The sweep stopped early; the reached items ride the reports.</summary>
    Failed,
}
