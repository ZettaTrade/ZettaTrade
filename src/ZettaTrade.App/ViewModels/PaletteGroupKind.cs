namespace ZettaTrade.App.ViewModels;

/// <summary>
/// The blank-query palette's pools (T-161): the ranked page groups the
/// session's recents, the user's favorites and the remaining instruments,
/// and a pool's head row carries its kind so the row template prints the
/// section header above it. The headers are the rows' own visuals — they
/// never occupy a selection index, so the keyboard route (the T-095
/// contract) walks the same flat list it always has. A query-typed page is
/// one ranked pool — no heads there.
/// </summary>
public enum PaletteGroupKind
{
    /// <summary>The session's applied pairs (the in-memory recents list, newest first).</summary>
    Recents,

    /// <summary>The user's starred pairs (the favorites store's read-only mirror).</summary>
    Favorites,

    /// <summary>The remaining instruments in their natural (registration) order.</summary>
    Other,
}
