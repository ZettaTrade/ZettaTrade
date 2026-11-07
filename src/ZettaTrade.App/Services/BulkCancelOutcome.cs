namespace ZettaTrade.App.Services;

/// <summary>
/// Terminal state of one bulk cancel attempt, driving which line the panel
/// shows.
/// </summary>
public enum BulkCancelOutcome
{
    /// <summary>
    /// The confirmed pass completed: the per-item reports carry the mixed
    /// result (a set with nothing commandable is a Done pass of skips only).
    /// </summary>
    Done,

    /// <summary>The user refused the confirmation: every order was left untouched.</summary>
    Declined,

    /// <summary>
    /// The pass could not start or was aborted; whatever was reached stays in
    /// the reports and the reason is stated.
    /// </summary>
    Failed,
}
