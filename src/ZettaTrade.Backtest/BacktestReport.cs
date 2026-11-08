namespace ZettaTrade.Backtest;

using ZettaTrade.Core.Entities;

/// <summary>
/// One point of the replay's mark-to-market equity curve: the moment (the
/// closed candle's open time) and the account's total equity at that
/// moment, in the quote currency — realized cash plus the open position
/// marked at that candle's close. One point per closed candle, ascending,
/// deterministic.
/// </summary>
/// <param name="Time">The open time of the closed candle the point marks.</param>
/// <param name="Value">The mark-to-market equity in the quote currency.</param>
public sealed record BacktestEquityPoint(DateTimeOffset Time, decimal Value);

/// <summary>
/// The aggregate outcome of one backtest run. Mirrors the trade-statistics
/// denominators (T-083's account): the win rate is wins over the decided
/// trades, the profit factor is gross profit over the loss magnitude, and
/// a ratio that nothing decides yet stays null — never a fabricated zero.
/// <para>
/// <see cref="RealizedTotal"/> is the gross fill-pair realized PnL of the
/// closed trades (the average-price method, commissions excluded — they
/// ride <see cref="CommissionTotal"/> and every fill's own stamp). The
/// drawdown folds the mark-to-market equity curve.
/// </para>
/// </summary>
/// <param name="Fills">The number of simulated fills.</param>
/// <param name="Trades">The number of closed round trips (breakeven ones count as trades).</param>
/// <param name="Wins">The round trips that closed at a profit.</param>
/// <param name="Losses">The round trips that closed at a loss.</param>
/// <param name="RealizedTotal">Gross realized PnL of the closed trades (commissions excluded).</param>
/// <param name="CommissionTotal">The sum of stamped commissions (the paper schedule's honest unknown leaves it zero).</param>
/// <param name="Volume">The notional traded (price × quantity per fill).</param>
/// <param name="MaxDrawdown">The largest peak-to-trough fall of the equity curve.</param>
/// <param name="PeakEquity">The highest mark-to-market equity reached.</param>
/// <param name="FinalEquity">The last mark-to-market equity of the replay.</param>
/// <param name="Unrealized">The open position's unrealized PnL at the final mark (zero when flat).</param>
/// <param name="AverageWinner">The mean winner's realized PnL, null while no trade won.</param>
/// <param name="AverageLoser">The mean loser's realized PnL, null while no trade lost.</param>
/// <param name="WinRate">Wins over the decided trades, null while nothing decided.</param>
/// <param name="ProfitFactor">Gross profit over the loss magnitude, null while nothing lost.</param>
public sealed record BacktestStatistics(
    int Fills,
    int Trades,
    int Wins,
    int Losses,
    decimal RealizedTotal,
    decimal CommissionTotal,
    decimal Volume,
    decimal MaxDrawdown,
    decimal PeakEquity,
    decimal FinalEquity,
    decimal Unrealized,
    decimal? AverageWinner,
    decimal? AverageLoser,
    decimal? WinRate,
    decimal? ProfitFactor);

/// <summary>
/// One refused stance transition of the replay: the moment and the reason.
/// The paper ledger refuses a fill the portfolio cannot carry outright and
/// leaves nothing behind (the T-051-corrected no-phantom-fill path); the
/// backtest mirrors that and surfaces the refusal as a fact of the report,
/// never as a substituted default.
/// </summary>
/// <param name="Time">The open time of the closed candle the refusal belongs to.</param>
/// <param name="Reason">The refusal reason, in the ledger's own words.</param>
public sealed record BacktestRefusal(DateTimeOffset Time, string Reason);

/// <summary>
/// The deterministic report of one backtest run (T-113): the request it
/// served, the per-candle mark-to-market equity curve, the simulated fills
/// in Core's journal shape (exactly how the paper ledger records them) and
/// the refused transitions, plus the aggregate statistics.
/// <para>
/// <see cref="ToCanonicalText"/> serializes the whole report byte-stably:
/// fixed key order, invariant culture, round-trip timestamps — the same
/// seed over the same cache yields byte-identical text, which is the
/// determinism acceptance of the task.
/// </para>
/// </summary>
public sealed record BacktestReport
{
    #region Constructors

    /// <summary>Create the full report of one completed run.</summary>
    public BacktestReport(
        BacktestRunRequest request,
        IReadOnlyList<BacktestEquityPoint> equity,
        IReadOnlyList<ZettaTrade.Core.Entities.Order> fills,
        IReadOnlyList<BacktestRefusal> refusals,
        BacktestStatistics statistics)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Equity = equity ?? throw new ArgumentNullException(nameof(equity));
        Fills = fills ?? throw new ArgumentNullException(nameof(fills));
        Refusals = refusals ?? throw new ArgumentNullException(nameof(refusals));
        Statistics = statistics;
    }

    #endregion Constructors

    #region Properties

    /// <summary>The request the run served (re-serialized into the canonical text).</summary>
    public BacktestRunRequest Request { get; }

    /// <summary>The mark-to-market equity curve, one point per closed candle, ascending.</summary>
    public IReadOnlyList<BacktestEquityPoint> Equity { get; }

    /// <summary>The simulated fills in the journal's <see cref="Order"/> shape
    /// (the paper ledger's record spelling, timestamps derived from the
    /// candles — the replay has no wall clock).</summary>
    public IReadOnlyList<ZettaTrade.Core.Entities.Order> Fills { get; }

    /// <summary>The refused stance transitions, in replay order.</summary>
    public IReadOnlyList<BacktestRefusal> Refusals { get; }

    /// <summary>The aggregate statistics of the run.</summary>
    public BacktestStatistics Statistics { get; }

    #endregion Properties

    #region Canonical Serialization

    /// <summary>
    /// The canonical byte-stable serialization of the report: the request
    /// echo, then every fill, refusal, curve point and the statistics, in
    /// replay order, invariant everywhere. Two runs of the same request
    /// over the same cache produce byte-identical text.
    /// </summary>
    public string ToCanonicalText()
    {
        // Report Cosmetics: the text is a canonical golden, not prose —
        // every number is an invariant decimal, every timestamp "o", every
        // line its own fact in a fixed order.
        var text = new System.Text.StringBuilder();
        var request = Request;

        text.Append("BacktestReport v1").AppendLine();
        text.Append("StrategyId=").Append(request.StrategyId).AppendLine();
        text.Append("AdapterId=").Append(request.AdapterId).AppendLine();
        text.Append("Symbol=").Append(request.Symbol.Normalized).AppendLine();
        text.Append("Interval=").Append(request.Interval.ToString()).AppendLine();
        text.Append("Bars=").Append(request.Bars.ToString(System.Globalization.CultureInfo.InvariantCulture)).AppendLine();
        text.Append("Seed=").Append(request.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture)).AppendLine();
        text.Append("InitialQuote=").Append(Format(request.InitialQuoteAmount)).AppendLine();
        text.Append("LongNotionalFraction=").Append(Format(request.LongNotionalFraction)).AppendLine();
        text.Append("SlippageValue=").Append(Format(request.SlippageValue)).AppendLine();

        for (var i = 0; i < Fills.Count; i++)
        {
            var fill = Fills[i];
            text.Append("Fill[").Append(i.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(']');
            text.Append(" time=").Append(fill.CreatedAt.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            text.Append(" id=").Append(fill.Id);
            text.Append(" side=").Append(fill.Side.ToString());
            text.Append(" type=").Append(fill.Type.ToString());
            text.Append(" qty=").Append(Format(fill.Quantity.Value));
            text.Append(" price=").Append(Format(fill.Price.Value));
            text.Append(" status=").Append(fill.Status.ToString());
            if (fill.Commission is { } commission)
            {
                text.Append(" commission=").Append(Format(commission));
            }
            else
            {
                text.Append(" commission=none");
            }

            text.AppendLine();
        }

        for (var i = 0; i < Refusals.Count; i++)
        {
            var refusal = Refusals[i];
            text.Append("Refusal[").Append(i.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(']');
            text.Append(" time=").Append(refusal.Time.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            text.Append(" reason=").Append(refusal.Reason);
            text.AppendLine();
        }

        for (var i = 0; i < Equity.Count; i++)
        {
            var point = Equity[i];
            text.Append("Equity[").Append(i.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(']');
            text.Append(" time=").Append(point.Time.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            text.Append(" value=").Append(Format(point.Value));
            text.AppendLine();
        }

        var stats = Statistics;
        text.Append("Statistics")
            .Append(" fills=").Append(stats.Fills.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append(" trades=").Append(stats.Trades.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append(" wins=").Append(stats.Wins.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append(" losses=").Append(stats.Losses.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append(" realizedTotal=").Append(Format(stats.RealizedTotal))
            .Append(" commissionTotal=").Append(Format(stats.CommissionTotal))
            .Append(" volume=").Append(Format(stats.Volume))
            .Append(" maxDrawdown=").Append(Format(stats.MaxDrawdown))
            .Append(" peakEquity=").Append(Format(stats.PeakEquity))
            .Append(" finalEquity=").Append(Format(stats.FinalEquity))
            .Append(" unrealized=").Append(Format(stats.Unrealized))
            .Append(" averageWinner=").Append(Format(stats.AverageWinner))
            .Append(" averageLoser=").Append(Format(stats.AverageLoser))
            .Append(" winRate=").Append(Format(stats.WinRate))
            .Append(" profitFactor=").Append(Format(stats.ProfitFactor))
            .AppendLine();

        return text.ToString();
    }

    /// <summary>Format one decimal invariantly (round-trip stable per run).</summary>
    private static string Format(decimal value) =>
        value.ToString("G29", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Format one optional decimal invariantly ("none" stands for the absence).</summary>
    private static string Format(decimal? value) =>
        value is { } present ? Format(present) : "none";

    #endregion Canonical Serialization
}
