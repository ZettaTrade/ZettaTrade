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
    IReadOnlyList<EquityCurvePoint> Equity);
