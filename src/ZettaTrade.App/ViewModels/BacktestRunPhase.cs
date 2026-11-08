namespace ZettaTrade.App.ViewModels;

/// <summary>
/// The lifecycle phase the backtest run/progress view model renders (T-113):
/// honest absence before the first start, running while the replay is in
/// flight, and the terminal states per the run's outcome or refusal.
/// </summary>
public enum BacktestRunPhase
{
    /// <summary>No run ever started here yet.</summary>
    Idle = 0,

    /// <summary>A replay is in flight; the start command is gated off, the cancel command on.</summary>
    Running = 1,

    /// <summary>The last run completed and produced its report.</summary>
    Completed = 2,

    /// <summary>The last run was cancelled cooperatively; no artifacts were produced.</summary>
    Cancelled = 3,

    /// <summary>The last start was refused or failed; the honest error line carries the reason.</summary>
    Failed = 4,
}
