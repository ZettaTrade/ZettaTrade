namespace ZettaTrade.App.ViewModels;

using ZettaTrade.Core.ValueObjects;

/// <summary>
/// One row of the symbol palette: the ranked instrument with the per-
/// exchange marks the row renders (the star for a favorite, the recency
/// marker for a pair applied earlier this session). The ranked list is
/// rebuilt in place on every publish, so the row is an immutable
/// snapshot — no per-row change notifications.
/// </summary>
/// <param name="Symbol">The row's trading symbol.</param>
/// <param name="ExchangeId">The serving exchange's id.</param>
/// <param name="ExchangeName">The serving exchange's display name (the row's right label).</param>
/// <param name="IsFavorite">Whether the user starred this pair at this exchange — a read-only mirror of the favorites store.</param>
/// <param name="IsRecent">Whether the row was applied earlier this session (the in-memory recents list).</param>
/// <param name="GroupHead">The pool the row opens on the blank-query page (T-161), or null for a continuation row and every query-typed row. The header prints above the head row; it never occupies a selection index.</param>
public sealed record SymbolPaletteRow(
    Symbol Symbol,
    string ExchangeId,
    string ExchangeName,
    bool IsFavorite,
    bool IsRecent,
    PaletteGroupKind? GroupHead = null)
{
    /// <summary>Whether the row opens the recents pool (the section header prints above it).</summary>
    public bool IsRecentsHead => GroupHead == PaletteGroupKind.Recents;

    /// <summary>Whether the row opens the favorites pool.</summary>
    public bool IsFavoritesHead => GroupHead == PaletteGroupKind.Favorites;

    /// <summary>Whether the row opens the other-instruments pool.</summary>
    public bool IsOtherHead => GroupHead == PaletteGroupKind.Other;
}
