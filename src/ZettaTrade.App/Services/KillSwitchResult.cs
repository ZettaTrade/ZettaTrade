namespace ZettaTrade.App.Services;

/// <summary>
/// The whole outcome of one kill-switch sweep (T-188): the per-item reports
/// in execution order and the localized multi-line summary the report
/// window renders. A refused confirmation resolves into the declined state
/// with no reports and nothing touched.
/// </summary>
public sealed record KillSwitchResult
{
    /// <summary>The terminal state of the sweep.</summary>
    public required KillSwitchOutcome Outcome { get; init; }

    /// <summary>Per-item reports in execution order.</summary>
    public required IReadOnlyList<KillSwitchItemReport> Reports { get; init; }

    /// <summary>The localized multi-line summary (the Done and Failed paths).</summary>
    public string? Summary { get; init; }

    /// <summary>True when the user refused the confirmation; no report window shows.</summary>
    public bool IsDeclined => Outcome == KillSwitchOutcome.Declined;

    /// <summary>A completed sweep (per-item failures stay honest in the reports and summary).</summary>
    public static KillSwitchResult Done(
        IReadOnlyList<KillSwitchItemReport> reports,
        string summary) => new()
        {
            Outcome = KillSwitchOutcome.Done,
            Reports = reports,
            Summary = summary,
        };

    /// <summary>The user refused the confirmation: nothing touched, nothing reported.</summary>
    public static KillSwitchResult Declined() => new()
    {
        Outcome = KillSwitchOutcome.Declined,
        Reports = [],
    };

    /// <summary>A sweep that stopped early, with whatever was reached.</summary>
    public static KillSwitchResult Failed(
        IReadOnlyList<KillSwitchItemReport> reports,
        string summary) => new()
        {
            Outcome = KillSwitchOutcome.Failed,
            Reports = reports,
            Summary = summary,
        };
}
