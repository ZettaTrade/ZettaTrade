namespace ZettaTrade.Backtest;

/// <summary>
/// One parameterization's outcome in a sweep's ranked result table (T-212):
/// the grid cell's own pair, the enumeration order it was replayed in, and
/// the complete deterministic report of that replay. The rank is the
/// position in the ranked table (1 = the best net), assigned once at
/// ranking time; <see cref="Index"/> keeps the enumeration order and is the
/// ranking's deterministic tie-break.
/// </summary>
/// <param name="Rank">The 1-based position in the ranked table.</param>
/// <param name="Index">The 0-based enumeration order of the grid cell.</param>
/// <param name="FirstValue">The first axis' knob value of the cell.</param>
/// <param name="SecondValue">The second axis' knob value of the cell.</param>
/// <param name="Report">The completed run's report (the row's own request carries the cell's knobs).</param>
public sealed record BacktestSweepRow(
    int Rank,
    int Index,
    decimal FirstValue,
    decimal SecondValue,
    BacktestReport Report)
{
    /// <summary>
    /// The row's net outcome in the quote currency: the final mark-to-market
    /// equity minus the starting balance. The ranking's primary metric —
    /// it folds realized PnL, commissions and the open position's
    /// unrealized mark, so two parameterizations of the same strategy over
    /// the same window compare honestly.
    /// </summary>
    public decimal Net => Report.Statistics.FinalEquity - Report.Request.InitialQuoteAmount;
}
