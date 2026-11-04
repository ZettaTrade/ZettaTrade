namespace ZettaTrade.App.ViewModels;

using ZettaTrade.Core.ValueObjects;

/// <summary>
/// One row of the symbols combo: the symbol plus the marks the row renders.
/// The list is rebuilt wholesale on every marks change, so the row is an
/// immutable snapshot — no per-row change notifications.
/// </summary>
/// <param name="Symbol">The row's trading symbol.</param>
/// <param name="IsFavorite">Whether the row renders the star badge (the
/// favorites pin to the head of the list).</param>
/// <param name="HiddenFromList">Whether the row exists only to hold the
/// selection: the symbol is hidden and the reveal toggle is off, so the
/// dropdown skips the row (invisible container) while the selection — and
/// the live series it owns — stays untouched.</param>
public sealed record SymbolRow(Symbol Symbol, bool IsFavorite, bool HiddenFromList);
