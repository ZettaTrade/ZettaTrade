namespace ZettaTrade.App;

/// <summary>
/// Settings keys of the user-managed watchlists section (T-196). Single
/// source of truth for App-level watchlist settings access: the whole
/// document rides ONE key — the lists are a single user document, not a
/// scattered set of per-list entries, so the export/import, the factory
/// reset and the decode canon all treat it as one versioned payload (the
/// alerts' <c>Alerts / Definitions</c> precedent, with the catalog service
/// in front of it).
/// </summary>
public static class WatchlistSettingsKeys
{
    /// <summary>Settings section holding the user-managed watchlists.</summary>
    public const string Section = "Watchlists";

    /// <summary>Settings key of the versioned watchlist document (JSON text via the codec).</summary>
    public const string DocumentKey = "Document";

    /// <summary>
    /// The stored name of the starter list (the roster every fresh install
    /// shows): the document's anchor name the catalog guards against
    /// deletion, while the panel prints its localized display name.
    /// </summary>
    public const string StarterListName = "Majors";
}
