namespace ZettaTrade.App.Services;

using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The whole outcome of one bulk cancel attempt: the per-item reports in
/// read order, the attempt's terminal state, and the localized summary line
/// the panel renders. A refused confirmation resolves into the declined
/// state with no reports and no touched order.
/// </summary>
public sealed record BulkCancelResult
{
    /// <summary>The terminal state of the attempt.</summary>
    public required BulkCancelOutcome Outcome { get; init; }

    /// <summary>
    /// The symbol the pass was scoped to; null only when the workspace had no
    /// active symbol to scope by (the explicit no-symbol rejection).
    /// </summary>
    public Symbol? Symbol { get; init; }

    /// <summary>Per-item reports in the adapter's read order.</summary>
    public required IReadOnlyList<BulkCancelReport> Reports { get; init; }

    /// <summary>Localized summary line of a completed pass (the Done path).</summary>
    public string? ResultMessage { get; init; }

    /// <summary>Localized reason the pass did not start or did not finish (the Failed path).</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>True when the user refused the confirmation; the panel stays silent.</summary>
    public bool IsDeclined => Outcome == BulkCancelOutcome.Declined;

    /// <summary>A fully completed pass with its per-item reports and summary line.</summary>
    public static BulkCancelResult Done(
        Symbol symbol,
        IReadOnlyList<BulkCancelReport> reports,
        string resultMessage) => new()
        {
            Outcome = BulkCancelOutcome.Done,
            Symbol = symbol,
            Reports = reports,
            ResultMessage = resultMessage,
        };

    /// <summary>The user refused the confirmation: nothing touched, nothing reported.</summary>
    public static BulkCancelResult Declined(Symbol symbol) => new()
    {
        Outcome = BulkCancelOutcome.Declined,
        Symbol = symbol,
        Reports = [],
    };

    /// <summary>A failed attempt with its localized reason and whatever was reached.</summary>
    public static BulkCancelResult Fail(
        Symbol? symbol,
        IReadOnlyList<BulkCancelReport> reports,
        string errorMessage) => new()
        {
            Outcome = BulkCancelOutcome.Failed,
            Symbol = symbol,
            Reports = reports,
            ErrorMessage = errorMessage,
        };
}
