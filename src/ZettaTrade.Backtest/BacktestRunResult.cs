namespace ZettaTrade.Backtest;

/// <summary>
/// How a backtest run ended: with a completed report, or cancelled
/// cooperatively mid-replay. A cancelled run carries no report and no
/// partial artifacts — the stop is the outcome, never an error.
/// </summary>
public enum BacktestRunOutcome
{
    /// <summary>The replay finished over the whole loaded series and produced its report.</summary>
    Completed = 0,

    /// <summary>The replay stopped cooperatively on its cancellation token; nothing was produced.</summary>
    Cancelled = 1,
}

/// <summary>
/// The result of one backtest run: the outcome plus the report (present
/// only when the run completed). The record carries a reference, not a
/// copy: a completed report is immutable.
/// </summary>
/// <param name="Outcome">How the run ended.</param>
/// <param name="Report">The run's report, or null on a cancelled run.</param>
public sealed record BacktestRunResult(BacktestRunOutcome Outcome, BacktestReport? Report)
{
    /// <summary>The cancelled-shirt envelope: no report, no artifacts.</summary>
    public static BacktestRunResult Cancelled { get; } = new(BacktestRunOutcome.Cancelled, Report: null);

    /// <summary>True when the run completed and carries its report.</summary>
    public bool IsCompleted => Outcome == BacktestRunOutcome.Completed;
}
