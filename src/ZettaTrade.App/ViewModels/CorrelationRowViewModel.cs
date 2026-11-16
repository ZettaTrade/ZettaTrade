namespace ZettaTrade.App.ViewModels;

/// <summary>
/// One display row of the correlation matrix window (T-252). The parent
/// view model precomputes the display strings at apply time — the row is
/// immutable display data, not a localization surface (the screener
/// row's own discipline).
/// </summary>
public sealed record CorrelationRowViewModel
{
    /// <summary>The row symbol's normalized text — also the row header's caption.</summary>
    public required string Symbol { get; init; }

    /// <summary>The row's cells, in the matrix's column order (one per symbol).</summary>
    public required IReadOnlyList<CorrelationCellViewModel> Cells { get; init; }
}

/// <summary>
/// One cell of the correlation matrix (T-252): the two-decimal signed
/// coefficient (the heatmap caption's explicit-sign form) or the honest
/// dash — a pair whose shared coverage cannot reach the minimum, whose
/// variance vanished, or the diagonal's own quiet anchor. A routable
/// cell (an off-diagonal verdict) presses into the compare view (T-124):
/// the pair's symbol that is not the chart's standing anchor joins the
/// compare layers — the decision rides the press (the chart's anchor is
/// live), so the cell carries both of the pair's texts and the view
/// model decides.
/// </summary>
/// <param name="Text">The display text: the signed coefficient or the dash.</param>
/// <param name="IsRoutable">True when the press routes (an off-diagonal cell carrying a coefficient).</param>
/// <param name="IsPositive">True for a strictly positive coefficient: the up tint's state class.</param>
/// <param name="IsNegative">True for a strictly negative coefficient: the down tint's state class.</param>
/// <param name="RowSymbol">The row symbol's normalized text (the pair's first member).</param>
/// <param name="ColumnSymbol">The column symbol's normalized text (the pair's second member).</param>
public sealed record CorrelationCellViewModel(
    string Text,
    bool IsRoutable,
    bool IsPositive,
    bool IsNegative,
    string RowSymbol,
    string ColumnSymbol)
{
    /// <summary>The honest dash: a coverage gap renders quiet, never a fabricated coefficient.</summary>
    public const string DashText = "\u2014";
}
