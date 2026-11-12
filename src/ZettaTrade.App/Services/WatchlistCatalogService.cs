namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ZettaTrade.App;
using ZettaTrade.Core.Interfaces;
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// The user-managed watchlists' catalog (T-196): the in-memory owner of the
/// watchlist document over the <c>Watchlists / Document</c> settings key.
/// The stored payload is read once at startup (the alerts engine's seeded
/// read); every mutation then produces a fresh immutable document, writes
/// it through the codec and only then publishes it — the store write and
/// the <see cref="Changes"/> emission are ordered, so a subscriber never
/// sees a document the store has not accepted. The document is
/// all-or-nothing: an unreadable or foreign-version payload, an empty
/// roster or a missing key all leave the starter document standing (the
/// factory reset removes the key, and the starter returns).
/// <para>
/// The reader/writer split is the settings window's own: writers never
/// subscribe to the store's change stream for the key they own (their own
/// writes would echo back), and the consumers ride this service's
/// <see cref="Changes"/> instead. Mutations before the seed's completion
/// wait for it — the document the user edits is always the stored one.
/// </para>
/// </summary>
public sealed class WatchlistCatalogService : IDisposable
{
    #region Private Fields

    /// <summary>The starter document: the fresh-install state and every fallback's anchor (one instance, reused).</summary>
    private readonly WatchlistDocument _starter = WatchlistCodec.StarterDocument();

    /// <summary>The live document, swapped by one atomic reference write.</summary>
    private WatchlistDocument _current;

    /// <summary>The one shared "the document changed" stream the panel and the feed ride.</summary>
    private readonly Subject<WatchlistDocument> _changes = new();

    /// <summary>The seed's completion gate: mutations wait for the stored read (or its fallback).</summary>
    private readonly TaskCompletionSource<RxVoid> _seeded =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The settings store the document persists through (written, never echoed).</summary>
    private readonly ISettingsService _settings;

    /// <summary>The catalog's own logging lane.</summary>
    private readonly ILogger<WatchlistCatalogService> _logger;

    #endregion Private Fields

    #region Properties

    /// <summary>The live document: the starter until the stored one applies, then the newest mutation's result.</summary>
    public WatchlistDocument Current => Volatile.Read(ref _current);

    /// <summary>
    /// The document stream: one publish after the seed and after every
    /// applied mutation (a refused or no-op mutation publishes nothing).
    /// </summary>
    public IObservable<WatchlistDocument> Changes => _changes;

    #endregion Properties

    #region Constructor

    /// <summary>
    /// Create the catalog: the starter document stands immediately and the
    /// stored payload's seeded read applies over it (the favorites marks'
    /// read precedent). The gate opens on the read's completion whatever it
    /// decoded — a failed read leaves the starter standing and the
    /// mutations still usable.
    /// </summary>
    public WatchlistCatalogService(ISettingsService settings, ILogger<WatchlistCatalogService> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _current = _starter;

        Observable
            .FromAsync(ReadStoredAsync)
            .Subscribe(
                ApplyStored,
                ex =>
                {
                    _logger.LogError(ex, "The watchlist document read failed; the starter list stays");
                    _seeded.TrySetResult(RxVoid.Default);
                },
                () => _seeded.TrySetResult(RxVoid.Default));
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>
    /// Add one symbol text to the named list (appended at the end). An
    /// unknown list or a blank text is refused with a warning; a symbol the
    /// list already carries is a no-op — no write, no publish.
    /// </summary>
    public Task AddSymbolAsync(string listName, string symbolText, CancellationToken ct = default) =>
        MutateAsync(
            current =>
            {
                if (FindList(current, listName) is not { } index)
                {
                    _logger.LogWarning("Watchlist: add refused — the list '{List}' is unknown", listName);
                    return null;
                }

                var trimmed = symbolText.Trim();
                if (trimmed.Length == 0)
                {
                    _logger.LogWarning("Watchlist: add refused — the symbol text is blank");
                    return null;
                }

                var list = current.Lists[index];
                if (list.Symbols.Contains(trimmed, StringComparer.Ordinal))
                {
                    return null; // The no-op duplicate: the list already carries it.
                }

                return WithList(current, index, list with { Symbols = [.. list.Symbols, trimmed] });
            },
            ct);

    /// <summary>
    /// Remove one symbol text from the named list. An unknown list or a
    /// symbol the list does not carry is refused with a warning (no write,
    /// no publish).
    /// </summary>
    public Task RemoveSymbolAsync(string listName, string symbolText, CancellationToken ct = default) =>
        MutateAsync(
            current =>
            {
                if (FindList(current, listName) is not { } index)
                {
                    _logger.LogWarning("Watchlist: remove refused — the list '{List}' is unknown", listName);
                    return null;
                }

                var list = current.Lists[index];
                var symbols = new List<string>(list.Symbols);
                if (!symbols.Remove(symbolText.Trim()))
                {
                    _logger.LogWarning(
                        "Watchlist: remove refused — '{Symbol}' is not in the list '{List}'",
                        symbolText,
                        listName);
                    return null;
                }

                return WithList(current, index, list with { Symbols = symbols });
            },
            ct);

    /// <summary>
    /// Move one symbol text inside the named list by <paramref name="step"/>
    /// positions (+1 down, -1 up). An unknown list, an unknown symbol or a
    /// step that would leave the roster is a no-op — no write, no publish.
    /// </summary>
    public Task MoveSymbolAsync(string listName, string symbolText, int step, CancellationToken ct = default) =>
        MutateAsync(
            current =>
            {
                if (FindList(current, listName) is not { } index)
                {
                    _logger.LogWarning("Watchlist: move refused — the list '{List}' is unknown", listName);
                    return null;
                }

                var list = current.Lists[index];
                var symbols = new List<string>(list.Symbols);
                var at = symbols.IndexOf(symbolText.Trim());
                var target = at + step;
                if (at < 0 || target < 0 || target >= symbols.Count)
                {
                    return null; // The bounds are the command's canExecute's business; the clamp is never a guess.
                }

                var moved = symbols[at];
                symbols.RemoveAt(at);
                symbols.Insert(target, moved);
                return WithList(current, index, list with { Symbols = symbols });
            },
            ct);

    /// <summary>
    /// Create a new empty list and make it active. A blank name or a name
    /// any list already carries (case-insensitively) is refused with a
    /// warning.
    /// </summary>
    public Task CreateListAsync(string name, CancellationToken ct = default) =>
        MutateAsync(
            current =>
            {
                var trimmed = name.Trim();
                if (trimmed.Length == 0 || FindList(current, trimmed) is not null)
                {
                    _logger.LogWarning("Watchlist: create refused — the name '{Name}' is blank or already taken", name);
                    return null;
                }

                var lists = new List<WatchlistList>(current.Lists) { new(trimmed, []) };
                return current with { ActiveList = trimmed, Lists = lists };
            },
            ct);

    /// <summary>
    /// Rename one list. An unknown list, a blank name or a name another
    /// list already carries (case-insensitively, the rename target
    /// excluded) is refused with a warning; a rename of the active list
    /// re-points the active name with it.
    /// </summary>
    public Task RenameListAsync(string oldName, string newName, CancellationToken ct = default) =>
        MutateAsync(
            current =>
            {
                if (FindList(current, oldName) is not { } index)
                {
                    _logger.LogWarning("Watchlist: rename refused — the list '{List}' is unknown", oldName);
                    return null;
                }

                var trimmed = newName.Trim();
                if (trimmed.Length == 0)
                {
                    _logger.LogWarning("Watchlist: rename refused — the new name is blank");
                    return null;
                }

                var list = current.Lists[index];
                if (string.Equals(list.Name, trimmed, StringComparison.Ordinal))
                {
                    return null; // The name is already exactly this: nothing to write.
                }

                for (var other = 0; other < current.Lists.Count; other++)
                {
                    if (other != index &&
                        string.Equals(current.Lists[other].Name, trimmed, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning(
                            "Watchlist: rename refused — the name '{Name}' is already taken",
                            newName);
                        return null;
                    }
                }

                var updated = WithList(current, index, list with { Name = trimmed });
                if (string.Equals(current.ActiveList, oldName, StringComparison.OrdinalIgnoreCase))
                {
                    updated = updated with { ActiveList = trimmed };
                }

                return updated;
            },
            ct);

    /// <summary>
    /// Delete one list. The starter list is the document's anchor — deleting
    /// it is refused with a warning (the factory reset is its only way out).
    /// Deleting the active list re-points the active to the starter list
    /// when it stands, else to the first remaining list; deleting the last
    /// remaining list restores the starter document in full.
    /// </summary>
    public Task DeleteListAsync(string listName, CancellationToken ct = default) =>
        MutateAsync(
            current =>
            {
                if (FindList(current, listName) is not { } index)
                {
                    _logger.LogWarning("Watchlist: delete refused — the list '{List}' is unknown", listName);
                    return null;
                }

                var list = current.Lists[index];
                if (string.Equals(list.Name, WatchlistSettingsKeys.StarterListName, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "Watchlist: delete refused — the starter list '{List}' is the document's anchor",
                        listName);
                    return null;
                }

                var lists = new List<WatchlistList>(current.Lists);
                lists.RemoveAt(index);
                if (lists.Count == 0)
                {
                    return _starter; // The roster emptied: the starter state returns in full.
                }

                var active = current.ActiveList;
                if (string.Equals(active, list.Name, StringComparison.OrdinalIgnoreCase))
                {
                    active = lists.FirstOrDefault(candidate =>
                            string.Equals(candidate.Name, WatchlistSettingsKeys.StarterListName, StringComparison.OrdinalIgnoreCase))
                        ?.Name
                        ?? lists[0].Name;
                }

                return current with { ActiveList = active, Lists = lists };
            },
            ct);

    /// <summary>
    /// Make the named list active. An unknown list is refused with a
    /// warning; activating the already-active list is a no-op — no write,
    /// no publish.
    /// </summary>
    public Task SetActiveAsync(string listName, CancellationToken ct = default) =>
        MutateAsync(
            current =>
            {
                if (FindList(current, listName) is not { } index)
                {
                    _logger.LogWarning("Watchlist: activate refused — the list '{List}' is unknown", listName);
                    return null;
                }

                var name = current.Lists[index].Name;
                if (string.Equals(current.ActiveList, name, StringComparison.OrdinalIgnoreCase))
                {
                    return null; // Already active: the echo never re-writes the key.
                }

                return current with { ActiveList = name };
            },
            ct);

    #endregion Public Methods

    #region Private Methods

    /// <summary>
    /// The seeded read: the stored payload or null — a missing key and a
    /// failed read both arrive as null (the starter stays), never as a
    /// fabricated document.
    /// </summary>
    private async Task<string?> ReadStoredAsync()
    {
        try
        {
            return await _settings.GetAsync<string>(WatchlistSettingsKeys.Section, WatchlistSettingsKeys.DocumentKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The watchlist document read failed; the starter list stays");
            return null;
        }
    }

    /// <summary>
    /// Apply one stored payload: a blank one (fresh install, factory reset)
    /// keeps the starter; a decode failure keeps the starter; a decoded
    /// document is normalized (duplicate names keep their first entry, an
    /// empty roster restores the starter, a dangling active name falls back
    /// to the starter list or the first list) and becomes the live
    /// document. The store is never rewritten on load — the normalized
    /// shape lands with the next mutation.
    /// </summary>
    private void ApplyStored(string? payload)
    {
        var applied = _starter;
        if (!string.IsNullOrWhiteSpace(payload))
        {
            try
            {
                var decoded = WatchlistCodec.Decode(payload);
                if (decoded.SkippedCount > 0)
                {
                    _logger.LogWarning(
                        decoded.SkippedCount == 1
                            ? "Watchlist: 1 stored list entry could not be decoded exactly and was skipped"
                            : "Watchlist: {Count} stored list entries could not be decoded exactly and were skipped",
                        decoded.SkippedCount);
                }

                applied = Normalize(decoded.Document);
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Watchlist: the stored document is unreadable; the starter list stays");
            }
        }

        Volatile.Write(ref _current, applied);
        _changes.OnNext(applied);
    }

    /// <summary>
    /// The stored document's load-time normalization: duplicate list names
    /// keep the first entry (the later ones dropped with a warning), an
    /// emptied roster restores the starter, and the active name re-points
    /// to the matched list's exact stored spelling — or falls back to the
    /// starter list, else the first list, when the stored name dangles.
    /// </summary>
    private WatchlistDocument Normalize(WatchlistDocument candidate)
    {
        var lists = new List<WatchlistList>(candidate.Lists.Count);
        var dropped = 0;
        foreach (var list in candidate.Lists)
        {
            if (lists.Exists(existing => string.Equals(existing.Name, list.Name, StringComparison.OrdinalIgnoreCase)))
            {
                dropped++;
                continue;
            }

            lists.Add(list);
        }

        if (dropped > 0)
        {
            _logger.LogWarning(
                dropped == 1
                    ? "Watchlist: 1 stored list entry carried a duplicate name and was dropped"
                    : "Watchlist: {Count} stored list entries carried duplicate names and were dropped",
                dropped);
        }

        if (lists.Count == 0)
        {
            return _starter;
        }

        var matched = lists.Find(list => string.Equals(list.Name, candidate.ActiveList, StringComparison.OrdinalIgnoreCase))
            ?? lists.Find(list => string.Equals(list.Name, WatchlistSettingsKeys.StarterListName, StringComparison.OrdinalIgnoreCase))
            ?? lists[0];
        return candidate with { ActiveList = matched.Name, Lists = lists };
    }

    /// <summary>
    /// The one mutation seam: wait for the seed, plan the new document over
    /// the current one, and — only when the plan produced one — write the
    /// store first, then swap the live document and publish it. A refused
    /// or no-op plan (null) writes and publishes nothing.
    /// </summary>
    private async Task MutateAsync(
        Func<WatchlistDocument, WatchlistDocument?> plan,
        CancellationToken ct)
    {
        await _seeded.Task.WaitAsync(ct);
        var updated = plan(Volatile.Read(ref _current));
        if (updated is null)
        {
            return;
        }

        await _settings.SetAsync(
            WatchlistSettingsKeys.Section,
            WatchlistSettingsKeys.DocumentKey,
            WatchlistCodec.Encode(updated));
        Volatile.Write(ref _current, updated);
        _changes.OnNext(updated);
    }

    /// <summary>Find a list's position by case-insensitive name; unknown names yield null.</summary>
    private static int? FindList(WatchlistDocument document, string name)
    {
        for (var index = 0; index < document.Lists.Count; index++)
        {
            if (string.Equals(document.Lists[index].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return null;
    }

    /// <summary>The document with one list entry replaced (the rest carried as-is, order kept).</summary>
    private static WatchlistDocument WithList(WatchlistDocument document, int index, WatchlistList list)
    {
        var lists = new WatchlistList[document.Lists.Count];
        for (var position = 0; position < lists.Length; position++)
        {
            lists[position] = document.Lists[position];
        }

        lists[index] = list;
        return document with { Lists = lists };
    }

    #endregion Private Methods

    #region Dispose

    /// <summary>Stop the document stream (the app-lifetime singleton usually outlives the process).</summary>
    public void Dispose() => _changes.Dispose();

    #endregion Dispose
}
