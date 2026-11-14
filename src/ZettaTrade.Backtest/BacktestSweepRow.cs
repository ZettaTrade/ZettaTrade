namespace ZettaTrade.Backtest;

/// <summary>
/// One parameterization's outcome in a sweep's ranked result table (T-212):
/// the grid cell's own pair, the enumeration order it was replayed in, the
/// cell's own request and the aggregate statistics of that replay. The rank
/// is the position in the ranked table (1 = the best net), assigned once at
/// ranking time; <see cref="Index"/> keeps the enumeration order and is the
/// ranking's deterministic tie-break.
/// <para>
/// T-263: the rows carry METRICS, never the full per-row reports — every
/// consumer outside the one selected row (the table's rendering, the
/// ranking itself, the sweep's canonical text) reads the statistics, so a
/// sweep of P parameterizations over N candles retains O(P) rows plus the
/// ONE best report the result carries, not P per-candle equity curves.
/// </para>
/// </summary>
/// <param name="Rank">The 1-based position in the ranked table.</param>
/// <param name="Index">The 0-based enumeration order of the grid cell.</param>
/// <param name="FirstValue">The first axis' knob value of the cell.</param>
/// <param name="SecondValue">The second axis' knob value of the cell.</param>
/// <param name="Request">The cell's own parameterized request (the replay's exact input, the knobs included).</param>
/// <param name="Statistics">The replay's aggregate statistics (the table's metrics source).</param>
public sealed record BacktestSweepRow(
    int Rank,
    int Index,
    decimal FirstValue,
    decimal SecondValue,
    BacktestRunRequest Request,
    BacktestStatistics Statistics)
{
    /// <summary>
    /// The row's net outcome in the quote currency: the final mark-to-market
    /// equity minus the starting balance. The ranking's primary metric —
    /// it folds realized PnL, commissions and the open position's
    /// unrealized mark, so two parameterizations of the same strategy over
    /// the same window compare honestly.
    /// </summary>
    public decimal Net => Statistics.FinalEquity - Request.InitialQuoteAmount;
}
