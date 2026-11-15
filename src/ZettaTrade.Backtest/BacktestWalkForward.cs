namespace ZettaTrade.Backtest;

using System.Text;

/// <summary>
/// How a walk-forward ended: with every fold replayed (in-sample optimized,
/// out-of-sample verified), or stopped cooperatively at a fold boundary. A
/// cancelled walk-forward carries every fold whose out-of-sample replay had
/// COMPLETED — each fold is atomic (a fold's row exists only when its whole
/// optimize-then-verify pass finished) — and nothing half-run ever exists.
/// </summary>
public enum BacktestWalkForwardOutcome
{
    /// <summary>Every fold replayed and the rows folded.</summary>
    Completed = 0,

    /// <summary>The stop landed at a fold boundary; the completed folds stay, the rest never started.</summary>
    Cancelled = 1,
}

/// <summary>
/// One fold's carve of the window (T-254): the segment's offset (its first
/// bar, oldest first) and its two halves — the in-sample head the grid
/// optimizes on and the out-of-sample tail the chosen pair replays on. The
/// halves split the segment 50/50 (the odd bar rides the out-of-sample
/// half), each half replaying through the exact <see cref="BacktestRunRequest"/>
/// bounds (each at least <see cref="BacktestRunRequest.MinBars"/> bars).
/// </summary>
/// <param name="Offset">The segment's first bar, oldest first (0-based over the loaded window).</param>
/// <param name="InSampleBars">The in-sample head's bar count.</param>
/// <param name="OutOfSampleBars">The out-of-sample tail's bar count.</param>
public sealed record BacktestWalkForwardSplit(
    int Offset,
    int InSampleBars,
    int OutOfSampleBars);

/// <summary>
/// The deterministic input of one walk-forward study (T-254): a fully valid
/// run request as the template, the two-knob grid (the same axes a T-212
/// sweep enumerates) and the fold count. The loaded window carves into
/// <see cref="Folds"/> consecutive equal segments (the honest even carve,
/// the remainder bars unused); each segment optimizes every grid
/// parameterization over its in-sample half and replays the best IS net
/// (ties by enumeration order) over its out-of-sample half.
/// </summary>
public sealed record BacktestWalkForwardRequest
{
    #region Constants

    /// <summary>The fold count a study carries when the caller pins none.</summary>
    public const int DefaultFolds = 4;

    /// <summary>The smallest fold count a study accepts: a single fold has no
    /// unseen segment to speak of — the walk-forward needs at least two.</summary>
    public const int MinFolds = 2;

    /// <summary>The largest fold count a study accepts: the sanity ceiling
    /// keeps the study's replay count bounded and its runtime predictable.</summary>
    public const int MaxFolds = 64;

    #endregion Constants

    #region Required Identity

    /// <summary>The fully valid run request every fold's replays start from (the unswept knobs come from here).</summary>
    public required BacktestRunRequest Template { get; init; }

    /// <summary>The grid's first axis (the enumeration's slower dimension).</summary>
    public required BacktestSweepAxis FirstAxis { get; init; }

    /// <summary>The grid's second axis (the enumeration's faster dimension).</summary>
    public required BacktestSweepAxis SecondAxis { get; init; }

    /// <summary>How many consecutive segments the window carves into.</summary>
    public int Folds { get; init; } = DefaultFolds;

    #endregion Required Identity

    #region Validation

    /// <summary>
    /// Refuse an invalid study before any replay starts: the template and
    /// both axes validate on their own, the two axes must name different
    /// knobs (a duplicate-knob grid is a diagonal, not a grid), and the fold
    /// count must sit inside its bounds. Nothing here is ever defaulted,
    /// corrected or clamped silently — the standing rule.
    /// </summary>
    /// <exception cref="ArgumentNullException">The template or an axis is missing.</exception>
    /// <exception cref="ArgumentException">A required identity is blank, or the two axes name the same knob.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A run bound, an axis bound or the fold bound is violated.</exception>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Template);
        Template.Validate();
        FirstAxis.Validate();
        SecondAxis.Validate();

        if (FirstAxis.Parameter == SecondAxis.Parameter)
        {
            throw new ArgumentException(
                "The two walk-forward axes must name different parameters; a duplicate-knob grid is refused, never collapsed.");
        }

        if (Folds < MinFolds || Folds > MaxFolds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Folds),
                Folds,
                $"The fold count must be between {MinFolds} and {MaxFolds}.");
        }
    }

    #endregion Validation

    #region Fold Carve

    /// <summary>
    /// Carve a window of the given bar count into the folds' consecutive
    /// segments: each segment spans
    /// <c>windowBars / <see cref="Folds"/></c> bars (integer division, the
    /// remainder bars stay unused — the honest deterministic carve), split
    /// 50/50 into its in-sample head and out-of-sample tail. A window that
    /// cannot host every fold's two honest halves is refused with its own
    /// named counts — never silently carved into unplayably short replays.
    /// </summary>
    /// <param name="windowBars">The loaded window's bar count (positive).</param>
    /// <exception cref="ArgumentOutOfRangeException">The window is empty, or a fold's segment cannot host <see cref="BacktestRunRequest.MinBars"/> in-sample and out-of-sample bars each.</exception>
    public IReadOnlyList<BacktestWalkForwardSplit> FoldPlan(int windowBars)
    {
        if (windowBars <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(windowBars),
                windowBars,
                "The loaded window is empty; the walk-forward cannot carve it.");
        }

        var segment = windowBars / Folds;
        var minimumSegment = 2 * BacktestRunRequest.MinBars;
        if (segment < minimumSegment)
        {
            throw new ArgumentOutOfRangeException(
                nameof(windowBars),
                windowBars,
                $"The loaded window ({windowBars} bars) cannot host {Folds} fold(s): each fold needs at least {minimumSegment} bars (two {BacktestRunRequest.MinBars}-bar halves); replay a longer window or fewer folds.");
        }

        var splits = new List<BacktestWalkForwardSplit>(Folds);
        for (var fold = 0; fold < Folds; fold++)
        {
            var offset = fold * segment;
            var inSampleBars = segment / 2;
            splits.Add(new BacktestWalkForwardSplit(offset, inSampleBars, segment - inSampleBars));
        }

        return splits;
    }

    #endregion Fold Carve
}

/// <summary>
/// One fold's outcome in a walk-forward's result table (T-254): the fold's
/// own carve, the pair the in-sample optimization chose (the best IS net,
/// ties broken by the enumeration order) and that pair's honest
/// out-of-sample replay — the walk-forward's whole point is the OOS number,
/// the IS number was the choice's input.
/// </summary>
/// <param name="Fold">The 1-based fold (chronological over the window).</param>
/// <param name="InSampleBars">The fold's in-sample head's bar count.</param>
/// <param name="OutOfSampleBars">The fold's out-of-sample tail's bar count.</param>
/// <param name="FirstValue">The first axis' knob value the fold chose.</param>
/// <param name="SecondValue">The second axis' knob value the fold chose.</param>
/// <param name="InSampleNet">The chosen pair's net over the in-sample half (the best of the grid).</param>
/// <param name="OutOfSampleNet">The chosen pair's net over the out-of-sample half.</param>
/// <param name="OutOfSampleMaxDrawdown">The chosen pair's replay's largest drawdown over the out-of-sample half.</param>
public sealed record BacktestWalkForwardRow(
    int Fold,
    int InSampleBars,
    int OutOfSampleBars,
    decimal FirstValue,
    decimal SecondValue,
    decimal InSampleNet,
    decimal OutOfSampleNet,
    decimal OutOfSampleMaxDrawdown);

/// <summary>
/// The result of one walk-forward study (T-254): the outcome, the study's
/// own request (the template, both axes, the folds) and the chronological
/// fold rows. The same study over the same cache yields the same rows —
/// every replay is the harness's own deterministic replay.
/// </summary>
/// <param name="Outcome">How the study ended.</param>
/// <param name="Request">The study's request (the template plus both axes and the folds).</param>
/// <param name="Rows">The fold rows, chronological.</param>
public sealed record BacktestWalkForwardResult(
    BacktestWalkForwardOutcome Outcome,
    BacktestWalkForwardRequest Request,
    IReadOnlyList<BacktestWalkForwardRow> Rows)
{
    #region Facts

    /// <summary>True when every fold replayed.</summary>
    public bool IsCompleted => Outcome == BacktestWalkForwardOutcome.Completed;

    /// <summary>The folds' in-sample nets summed (the optimization's own bookkeeping).</summary>
    public decimal TotalInSampleNet => Rows.Sum(row => row.InSampleNet);

    /// <summary>The folds' out-of-sample nets summed — the study's honest headline number.</summary>
    public decimal TotalOutOfSampleNet => Rows.Sum(row => row.OutOfSampleNet);

    /// <summary>How many folds earned a positive out-of-sample net (the honest hit rate of the re-optimization).</summary>
    public int WinningOutOfSampleFolds => Rows.Count(row => row.OutOfSampleNet > 0m);

    #endregion Facts

    #region Canonical Serialization

    /// <summary>
    /// The canonical byte-stable serialization of the study: the template
    /// echo, both axes, the folds, the outcome and every fold's carve and
    /// numbers, in chronological order, invariant everywhere. The same study
    /// over the same cache produces byte-identical text.
    /// </summary>
    public string ToCanonicalText()
    {
        var text = new StringBuilder();
        var template = Request.Template;

        text.Append("BacktestWalkForwardReport v1").AppendLine();
        text.Append("StrategyId=").Append(template.StrategyId).AppendLine();
        text.Append("AdapterId=").Append(template.AdapterId).AppendLine();
        text.Append("Symbol=").Append(template.Symbol.Normalized).AppendLine();
        text.Append("Interval=").Append(template.Interval.ToString()).AppendLine();
        text.Append("Bars=").Append(template.Bars.ToString(System.Globalization.CultureInfo.InvariantCulture)).AppendLine();
        text.Append("Seed=").Append(template.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture)).AppendLine();
        text.Append("InitialQuote=").Append(Format(template.InitialQuoteAmount)).AppendLine();
        AppendAxis(text, 0, Request.FirstAxis);
        AppendAxis(text, 1, Request.SecondAxis);
        text.Append("Folds=").Append(Request.Folds.ToString(System.Globalization.CultureInfo.InvariantCulture)).AppendLine();
        text.Append("Outcome=").Append(Outcome.ToString()).AppendLine();

        for (var i = 0; i < Rows.Count; i++)
        {
            var row = Rows[i];
            text.Append("Fold[").Append(row.Fold.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(']');
            text.Append(" isBars=").Append(row.InSampleBars.ToString(System.Globalization.CultureInfo.InvariantCulture));
            text.Append(" oosBars=").Append(row.OutOfSampleBars.ToString(System.Globalization.CultureInfo.InvariantCulture));
            text.Append(" first=").Append(Format(row.FirstValue));
            text.Append(" second=").Append(Format(row.SecondValue));
            text.Append(" isNet=").Append(Format(row.InSampleNet));
            text.Append(" oosNet=").Append(Format(row.OutOfSampleNet));
            text.Append(" oosDrawdown=").Append(Format(row.OutOfSampleMaxDrawdown));
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

    #endregion Canonical Serialization
}
