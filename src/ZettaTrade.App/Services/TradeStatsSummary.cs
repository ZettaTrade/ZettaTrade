namespace ZettaTrade.App.Services;

using ZettaTrade.Charting;

/// <summary>
/// The current totals of one trade-statistics scan: the outcome counts, the
/// money aggregates over the realized round trips and the equity curve that
/// derives from the same fill-pair math — one honest snapshot per scan page.
/// <para>
/// Denominators: the win rate is wins / (wins + losses) over the decided
/// trades (breakeven closes count as trades, they never decide), the profit
/// factor is gross profit over the loss magnitude. Per-null semantics:
/// stats that cannot be computed from nothing stay null — the win rate
/// before any decided trade, the profit factor without a single loss (the
/// ratio is unbounded, the view renders it honestly), and the average
/// winner/loser while the respective outcome never occurred — never
/// fabricated zeros.
/// </para>
/// <para>
/// The risk-relative reads (T-251) ride the same booked-trade series. The
/// Sharpe and Sortino ratios are per-trade and unannualized: the equity
/// curve carries no annual clock, so the mean realized PnL divides by the
/// sample standard deviation (Sharpe) and by the target-0 downside
/// deviation (Sortino, the square root of the mean squared negative
/// return). An unbounded ratio (a single return or an identical return
/// series, no downside at all) reads null — the view renders it by the
/// mean's sign, never a fabricated zero. The R-multiple aggregates speak
/// the same distribution over the trades that carry a stop context only;
/// the R-free trades never dilute the average.
/// </para>
/// </summary>
public sealed record TradeStatsSummary(
    int Trades,
    int Wins,
    int Losses,
    decimal MaxDrawdown,
    decimal RealizedTotal,
    decimal Volume,
    decimal? AverageWinner,
    decimal? AverageLoser,
    decimal? WinRate,
    decimal? ProfitFactor,
    IReadOnlyList<EquityCurvePoint> Equity,
    decimal? Sharpe = null,
    decimal? Sortino = null,
    decimal? AverageRMultiple = null,
    decimal? BestRMultiple = null,
    decimal? WorstRMultiple = null);
