namespace ZettaTrade.Backtest;

using System.Text;

/// <summary>
/// How a sweep ended: with the whole grid replayed and ranked, or stopped
/// cooperatively at a parameterization boundary. A cancelled sweep carries
/// every parameterization whose replay had COMPLETED before the boundary —
/// each a complete, byte-stable report — and nothing half-run ever exists
/// (the cancel never interrupts a replay mid-bars, the acceptance's
/// "between parameterizations" discipline).
/// </summary>
public enum BacktestSweepOutcome
{
    /// <summary>Every parameterization of the grid replayed and the table ranked.</summary>
    Completed = 0,

    /// <summary>The stop landed at a boundary; the completed parameterizations stay, the rest never started.</summary>
    Cancelled = 1,
}

/// <summary>
/// The result of one parameter sweep (T-212): the outcome, the sweep's own
/// request (the template plus both axes) and the ranked rows. The ranking is
/// deterministic: net descending, ties broken by the enumeration order — the
/// same grid over the same cache always yields the same table.
/// <para>
/// T-263: the rows carry metrics only (the cell's request + the replay's
/// statistics); the full report the report surface opens on is the ONE
/// <see cref="BestReport"/> — the rank-1 row's replay, re-run silently on
/// the sweep's OWN loaded window, so its equity curve is the sweep's own
/// fact (the same seed and the same bars fold the byte-identical report).
/// A cancelled sweep or an empty grid carries no best report: the
/// click-through re-replays a row through the single-run lane instead.
/// </para>
/// </summary>
/// <param name="Outcome">How the sweep ended.</param>
/// <param name="Request">The sweep's request (the template plus both axes).</param>
/// <param name="Rows">The ranked rows, best net first.</param>
/// <param name="BestReport">The completed sweep's rank-1 row's full report (null otherwise).</param>
public sealed record BacktestSweepResult(
    BacktestSweepOutcome Outcome,
    BacktestSweepRequest Request,
    IReadOnlyList<BacktestSweepRow> Rows,
    BacktestReport? BestReport = null)
{
    #region Facts

    /// <summary>True when the whole grid replayed.</summary>
    public bool IsCompleted => Outcome == BacktestSweepOutcome.Completed;

    #endregion Facts

    #region Canonical Serialization

    /// <summary>
    /// The canonical byte-stable serialization of the sweep: the template
    /// echo, both axes, the outcome and every ranked row's headline metrics,
    /// in ranked order, invariant everywhere. The same grid over the same
    /// cache produces byte-identical text — the sweep's determinism
    /// acceptance reads exactly this text against the previous one, and each
    /// row's report stays byte-stable on its own (the run's own discipline).
    /// </summary>
    public string ToCanonicalText()
    {
        var text = new StringBuilder();
        var template = Request.Template;

        text.Append("BacktestSweepReport v1").AppendLine();
        text.Append("StrategyId=").Append(template.StrategyId).AppendLine();
        text.Append("AdapterId=").Append(template.AdapterId).AppendLine();
        text.Append("Symbol=").Append(template.Symbol.Normalized).AppendLine();
        text.Append("Interval=").Append(template.Interval.ToString()).AppendLine();
        text.Append("Bars=").Append(template.Bars.ToString(System.Globalization.CultureInfo.InvariantCulture)).AppendLine();
        text.Append("Seed=").Append(template.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture)).AppendLine();
        text.Append("InitialQuote=").Append(Format(template.InitialQuoteAmount)).AppendLine();
        AppendAxis(text, 0, Request.FirstAxis);
        AppendAxis(text, 1, Request.SecondAxis);
        text.Append("Outcome=").Append(Outcome.ToString()).AppendLine();

        for (var i = 0; i < Rows.Count; i++)
        {
            var row = Rows[i];
            text.Append("Row[").Append(row.Rank.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(']');
            text.Append(" index=").Append(row.Index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            text.Append(" first=").Append(Format(row.FirstValue));
            text.Append(" second=").Append(Format(row.SecondValue));
            text.Append(" net=").Append(Format(row.Net));
            text.Append(" drawdown=").Append(Format(row.Statistics.MaxDrawdown));
            text.Append(" winRate=").Append(Format(row.Statistics.WinRate));
            text.Append(" profitFactor=").Append(Format(row.Statistics.ProfitFactor));
            text.AppendLine();
        }

        return text.ToString();
    }

    private static void AppendAxis(StringBuilder text, int ordinal, BacktestSweepAxis axis)
    {
        text.Append("Axis[").Append(ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(']');
        text.Append(" parameter=").Append(axis.Parameter.ToString());
        text.Append(" values=");
        for (var i = 0; i < axis.Values.Count; i++)
        {
            if (i > 0)
            {
                text.Append(',');
            }

            text.Append(Format(axis.Values[i]));
        }

        text.AppendLine();
    }

    /// <summary>Format one decimal invariantly (round-trip stable per run).</summary>
    private static string Format(decimal value) =>
        value.ToString("G29", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Format one optional decimal invariantly ("none" stands for the absence).</summary>
    private static string Format(decimal? value) =>
        value is { } present ? Format(present) : "none";

    #endregion Canonical Serialization
}
