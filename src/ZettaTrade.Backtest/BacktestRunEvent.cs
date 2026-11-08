namespace ZettaTrade.Backtest;

/// <summary>
/// The per-run state stream's event of the backtest harness (T-113). One
/// event per state transition: Started once, Progress at a fixed stride
/// over the replay, and the terminal Completed (with the report attached)
/// or Cancelled (never a report). UI consumers ride the stream behind
/// ObserveOnUiThread; the events never influence the replay itself.
/// </summary>
/// <param name="Kind">What transition this event carries.</param>
/// <param name="BarsRequested">The bar target of the run (the request's Bars).</param>
/// <param name="BarsProcessed">Bars consumed so far (zero on Started; the final count on the terminal events).</param>
/// <param name="Report">The completed run's report (attached to Completed only).</param>
public sealed record BacktestRunEvent(
    BacktestRunEventKind Kind,
    int BarsRequested,
    int BarsProcessed,
    BacktestReport? Report);

/// <summary>The event kinds of the backtest run stream, in the order a run owns them.</summary>
public enum BacktestRunEventKind
{
    /// <summary>The run began replaying (once, before the first rose).</summary>
    Started = 0,

    /// <summary>The replay advanced by its fixed progress stride.</summary>
    Progress = 1,

    /// <summary>The run completed and carries its report.</summary>
    Completed = 2,

    /// <summary>The run was cancelled cooperatively; no report was produced.</summary>
    Cancelled = 3,
}
