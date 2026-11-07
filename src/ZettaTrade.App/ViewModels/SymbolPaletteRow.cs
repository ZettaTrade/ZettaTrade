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
public sealed record SymbolPaletteRow(
    Symbol Symbol,
    string ExchangeId,
    string ExchangeName,
    bool IsFavorite,
    bool IsRecent);
