namespace ZettaTrade.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Livery.Extensions;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.App.Services;
using ZettaTrade.Core.ValueObjects;
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// The watchlist panel state (T-120, reworked in T-196): the rows'
/// collection over the feed's snapshots plus the list surface over the
/// catalog's document. The feed owns the row set and the cadence; the
/// ViewModel only diffs snapshots into stable row identities — a
/// collection event fires for the rows that join or retire, a moved row
/// moves once, and each row's fields update in place at the feed's pulse
/// (twice a second at most, whatever the wire says). The list surface
/// mirrors the catalog's document: the list combo, the inline name editor
/// and the row actions route into catalog mutations and re-publish only
/// when the catalog accepted them.
/// </summary>
public sealed class WatchlistViewModel : ReactiveObject
{
    #region Private Fields

    /// <summary>The localized display name of the starter list option (the stored name never shows for it).</summary>
    private const string DefaultListNameKey = "Watch.DefaultListName";

    /// <summary>The no-selection index the selection shims rest at.</summary>
    private const int NoSelection = -1;

    /// <summary>The panel's rows, in the feed's snapshot order.</summary>
    private readonly ObservableCollection<WatchlistRowViewModel> _rows = new();

    /// <summary>The row lookup the diff keeps (UI-thread-only access).</summary>
    private readonly Dictionary<string, WatchlistRowViewModel> _rowByText =
        new(StringComparer.Ordinal);

    /// <summary>The list options the combo holds (in-place positional diff — the collection identity never changes).</summary>
    private readonly ObservableCollection<WatchlistListOption> _lists = new();

    /// <summary>The catalog document the list surface mirrors (null until the first publish).</summary>
    private WatchlistDocument? _document;

    /// <summary>The active list's exact stored name — the row mutations' target (null until the first publish).</summary>
    private string? _activeListName;

    /// <summary>The row count the last publish raised (the count's raise fires on the change only).</summary>
    private int _publishedRowCount;

    /// <summary>The selected row's symbol text: the selection rides the identity, not the index (a reorder re-points the index).</summary>
    private string? _selectedSymbol;

    private int _selectedRowIndex = NoSelection;

    private int _selectedListIndex = NoSelection;

    /// <summary>The list name the last publish applied: the combo's echo-guard (a write of the applied name's index is the echo, never an apply).</summary>
    private string? _appliedListName;

    private bool _isListEditorOpen;

    private bool _isListEditorRename;

    private string _listEditorText = string.Empty;

    /// <summary>The rename target (null while the editor creates a list).</summary>
    private string? _editingListName;

    private bool _canDeleteList;

    /// <summary>The catalog every mutation routes into (the publishes come back through its stream only).</summary>
    private readonly WatchlistCatalogService _catalog;

    /// <summary>The localization the starter option's display name composes through.</summary>
    private readonly ILocalizationManager _localization;

    /// <summary>The panel's own logging lane.</summary>
    private readonly ILogger<WatchlistViewModel> _logger;

    #endregion Private Fields

    #region Properties

    /// <summary>The panel's rows, in the feed's snapshot order.</summary>
    public ObservableCollection<WatchlistRowViewModel> Rows => _rows;

    /// <summary>The list options the combo holds, in the document's stored order.</summary>
    public ObservableCollection<WatchlistListOption> Lists => _lists;

    /// <summary>How many rows the panel carries (the row commands' exact bound, never a clamped guess).</summary>
    public int RowCount => _rows.Count;

    /// <summary>Whether the panel carries any rows at all (the empty hint's flip).</summary>
    public bool HasRows => _rows.Count > 0;

    /// <summary>
    /// Every ask to add a symbol rides this interaction: the window opens
    /// the palette in watchlist mode and completes the ride with the picked
    /// symbol (the compare pick's route, pointed at the catalog).
    /// </summary>
    public Interaction<RxVoid, RxVoid> AddSymbolRequested { get; }

    /// <summary>
    /// The active list's delete availability: it exists and is not the
    /// starter (the document's anchor). Re-evaluated on every catalog
    /// publish, so a rename away from the starter name re-arms it even when
    /// the active index itself did not move.
    /// </summary>
    public bool CanDeleteList
    {
        get => _canDeleteList;
        private set => this.RaiseAndSetIfChanged(ref _canDeleteList, value);
    }

    #endregion Properties

    #region Row Selection

    /// <summary>
    /// The selected row's index (two-way: the list's own selection lands
    /// here too). The selection rides the row's symbol text — a snapshot
    /// that retires or reorders the row re-points this index from the
    /// remembered text, so a list edit can never leave the toolbar acting
    /// on the wrong row.
    /// </summary>
    public int SelectedRowIndex
    {
        get => _selectedRowIndex;
        set
        {
            var changed = _selectedRowIndex != value;
            this.RaiseAndSetIfChanged(ref _selectedRowIndex, value);
            if (!changed)
            {
                return;
            }

            _selectedSymbol = value >= 0 && value < _rows.Count ? _rows[value].SymbolText : null;
        }
    }

    #endregion Row Selection

    #region List Selection

    /// <summary>
    /// The active list's index shim (two-way over the combo, the screener
    /// preset's precedent): the catalog publish writes the field and raises
    /// — a write of the applied name's index is that publish's echo and
    /// never re-issues the switch; a write of another name's index switches
    /// the active list. A failed switch stays re-issuable: the echo-guard
    /// only marks the names the publishes actually applied.
    /// </summary>
    public int SelectedListIndex
    {
        get => _selectedListIndex;
        set
        {
            if (_selectedListIndex == value)
            {
                return;
            }

            _selectedListIndex = value;
            this.RaisePropertyChanged(nameof(SelectedListIndex));

            if (value < 0 || value >= _lists.Count)
            {
                // The control's transient reset (the items swap) — the
                // publish re-points immediately after; never an apply.
                return;
            }

            var name = _lists[value].Name;
            if (string.Equals(name, _appliedListName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _ = ApplyListSwitchAsync(name);
        }
    }

    #endregion List Selection

    #region List Editor

    /// <summary>Whether the inline name editor stands open (the toolbar swaps to it).</summary>
    public bool IsListEditorOpen
    {
        get => _isListEditorOpen;
        private set => this.RaiseAndSetIfChanged(ref _isListEditorOpen, value);
    }

    /// <summary>Whether the open editor renames the active list (false: it creates a new one).</summary>
    public bool IsListEditorRename
    {
        get => _isListEditorRename;
        private set => this.RaiseAndSetIfChanged(ref _isListEditorRename, value);
    }

    /// <summary>The editor's live name text (two-way over the text box).</summary>
    public string ListEditorText
    {
        get => _listEditorText;
        set => this.RaiseAndSetIfChanged(ref _listEditorText, value);
    }

    #endregion List Editor

    #region Commands

    /// <summary>Open the editor to create a list (the create sets the new list active on apply).</summary>
    public ReactiveCommand<RxVoid, RxVoid> OpenNewListCommand { get; }

    /// <summary>Open the editor over the active list's name.</summary>
    public ReactiveCommand<RxVoid, RxVoid> OpenRenameListCommand { get; }

    /// <summary>Apply the editor's name (create or rename) and close it.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ConfirmListEditorCommand { get; }

    /// <summary>Close the editor without applying anything.</summary>
    public ReactiveCommand<RxVoid, RxVoid> CancelListEditorCommand { get; }

    /// <summary>Delete the active list (the starter is never deletable — the anchor of the default state).</summary>
    public ReactiveCommand<RxVoid, RxVoid> DeleteListCommand { get; }

    /// <summary>Open the palette's watchlist pick (the picked symbol joins the active list).</summary>
    public ReactiveCommand<RxVoid, RxVoid> AddSymbolCommand { get; }

    /// <summary>Remove the selected row from the active list.</summary>
    public ReactiveCommand<RxVoid, RxVoid> RemoveRowCommand { get; }

    /// <summary>Move the selected row one position up inside the active list.</summary>
    public ReactiveCommand<RxVoid, RxVoid> MoveRowUpCommand { get; }

    /// <summary>Move the selected row one position down inside the active list.</summary>
    public ReactiveCommand<RxVoid, RxVoid> MoveRowDownCommand { get; }

    #endregion Commands

    #region Constructor

    /// <summary>
    /// Create the panel over the feed and the catalog: the quotes flow
    /// through the UI thread so the collection writes stay on it, and the
    /// catalog's document stream re-publishes the list surface (the VM
    /// lives as long as the application, so these subscriptions mirror
    /// it). Writers never ride their own echo — the mutations route into
    /// the catalog and the publishes come back through
    /// <see cref="WatchlistCatalogService.Changes"/> only.
    /// </summary>
    public WatchlistViewModel(
        WatchlistFeedService feed,
        WatchlistCatalogService catalog,
        ILocalizationManager localization,
        ILogger<WatchlistViewModel> logger)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        AddSymbolRequested = new Interaction<RxVoid, RxVoid>();

        feed.Quotes
            .ObserveOnUiThread()
            .Subscribe(
                ApplyQuotes,
                ex => _logger.LogError(ex, "The watchlist quote stream stopped unexpectedly"));

        catalog.Changes
            .StartWith(catalog.Current)
            .ObserveOnUiThread()
            .Subscribe(
                ApplyDocument,
                ex => _logger.LogError(ex, "The watchlist document stream stopped unexpectedly"));

        // The starter option's display name re-inks with the language (the
        // stored names never localize — only the starter token does).
        _localization.LanguageChanged
            .ObserveOnUiThread()
            .Subscribe(
                _ => RepublishListOptions(),
                ex => _logger.LogError(ex, "The watchlist language stream stopped unexpectedly"));

        var rowBounds = this.WhenAnyValue(
            x => x.SelectedRowIndex,
            x => x.RowCount,
            (index, count) => index >= 0 && index < count);

        OpenNewListCommand = ReactiveCommand.Create<RxVoid, RxVoid>(
            _ =>
            {
                OpenNewListEditor();
                return RxVoid.Default;
            },
            this.WhenAnyValue(x => x.IsListEditorOpen, open => !open));
        OpenRenameListCommand = ReactiveCommand.Create<RxVoid, RxVoid>(
            _ =>
            {
                OpenRenameListEditor();
                return RxVoid.Default;
            },
            this.WhenAnyValue(
                x => x.SelectedListIndex,
                x => x.IsListEditorOpen,
                (index, open) => !open && index >= 0));
        ConfirmListEditorCommand = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(
            (_, ct) => ConfirmListEditorAsync(ct),
            this.WhenAnyValue(
                x => x.IsListEditorOpen,
                x => x.ListEditorText,
                (open, text) => open && IsEditorTextApplicable(text)));
        CancelListEditorCommand = ReactiveCommand.Create<RxVoid, RxVoid>(
            _ =>
            {
                CancelListEditor();
                return RxVoid.Default;
            },
            this.WhenAnyValue(x => x.IsListEditorOpen));
        DeleteListCommand = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(
            (_, ct) => DeleteActiveListAsync(ct),
            this.WhenAnyValue(
                x => x.CanDeleteList,
                x => x.IsListEditorOpen,
                (canDelete, open) => canDelete && !open));

        AddSymbolCommand = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(
            (_, ct) => OpenSymbolPickerAsync(ct));
        RemoveRowCommand = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(
            (_, ct) => RemoveSelectedRowAsync(ct),
            rowBounds);
        MoveRowUpCommand = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(
            (_, ct) => MoveSelectedRowAsync(-1, ct),
            this.WhenAnyValue(
                x => x.SelectedRowIndex,
                x => x.RowCount,
                (index, count) => index > 0 && index < count));
        MoveRowDownCommand = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(
            (_, ct) => MoveSelectedRowAsync(1, ct),
            this.WhenAnyValue(
                x => x.SelectedRowIndex,
                x => x.RowCount,
                (index, count) => index >= 0 && index < count - 1));
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>
    /// Apply one snapshot: join the missing rows, update each row's fields
    /// in place, retire the rows the feed dropped, then the order pass —
    /// the snapshot order IS the active list's stored order, and a moved
    /// row moves once (stable identities ride every move). The selection
    /// fix-up closes the pass: the remembered symbol text re-points the
    /// index, or clears it when the feed retired the row.
    /// </summary>
    public void ApplyQuotes(IReadOnlyList<WatchlistQuote> snapshot)
    {
        var alive = new HashSet<string>(StringComparer.Ordinal);

        foreach (var quote in snapshot)
        {
            alive.Add(quote.SymbolText);

            if (!_rowByText.TryGetValue(quote.SymbolText, out var row))
            {
                row = new WatchlistRowViewModel(quote.SymbolText);
                _rowByText.Add(quote.SymbolText, row);
                _rows.Add(row);
            }

            row.ApplyQuote(quote);
        }

        for (var index = _rows.Count - 1; index >= 0; index--)
        {
            var row = _rows[index];
            if (alive.Contains(row.SymbolText))
            {
                continue;
            }

            _rows.RemoveAt(index);
            _rowByText.Remove(row.SymbolText);
        }

        for (var position = 0; position < snapshot.Count; position++)
        {
            var row = _rowByText[snapshot[position].SymbolText];
            var current = _rows.IndexOf(row);
            if (current != position)
            {
                _rows.Move(current, position);
            }
        }

        if (_rows.Count != _publishedRowCount)
        {
            _publishedRowCount = _rows.Count;
            this.RaisePropertyChanged(nameof(RowCount));
            this.RaisePropertyChanged(nameof(HasRows));
        }

        if (_selectedSymbol is { } selected && _rowByText.TryGetValue(selected, out var selectedRow))
        {
            var index = _rows.IndexOf(selectedRow);
            if (index != _selectedRowIndex)
            {
                _selectedRowIndex = index;
                this.RaisePropertyChanged(nameof(SelectedRowIndex));
            }
        }
        else if (_selectedRowIndex != NoSelection)
        {
            _selectedSymbol = null;
            _selectedRowIndex = NoSelection;
            this.RaisePropertyChanged(nameof(SelectedRowIndex));
        }
    }

    /// <summary>
    /// Add the palette's picked symbol to the active list (the watchlist
    /// pick's landing): the catalog mutation carries it, the publish
    /// re-publishes the rows. A failure logs — the pick itself already
    /// closed the palette.
    /// </summary>
    public async Task ApplyPickedAsync(Symbol symbol)
    {
        if (_activeListName is not { } list)
        {
            return;
        }

        try
        {
            await _catalog.AddSymbolAsync(list, symbol.Normalized);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The watchlist cannot add '{Symbol}' to '{List}'", symbol.Normalized, list);
        }
    }

    #endregion Public Methods

    #region Document Publish

    /// <summary>
    /// Apply one catalog publish: the held document re-points the list
    /// surface (the in-place positional diff keeps the combo's collection
    /// identity — the palette's rank-publish discipline), the shim re-points
    /// to the active list's index, and the row mutations' target follows.
    /// The delete gate re-arms off the active list's own identity, so a
    /// rename away from the starter name opens it even when the index did
    /// not move.
    /// </summary>
    private void ApplyDocument(WatchlistDocument document)
    {
        _document = document;
        RepublishListOptions();

        CanDeleteList = _activeListName is { } active &&
            !string.Equals(active, WatchlistSettingsKeys.StarterListName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Rebuild the list options over the held document: the starter token's
    /// display name is the localized default (the stored names show as
    /// stored), the active index lands in the shim with its echo-guard
    /// name, and the row mutations' target follows the active list.
    /// </summary>
    private void RepublishListOptions()
    {
        if (_document is not { } document)
        {
            return;
        }

        var activeIndex = NoSelection;
        string? activeName = null;
        for (var index = 0; index < document.Lists.Count; index++)
        {
            var list = document.Lists[index];
            if (string.Equals(list.Name, document.ActiveList, StringComparison.OrdinalIgnoreCase))
            {
                activeIndex = index;
                activeName = list.Name;
            }

            var isStarter = string.Equals(
                list.Name,
                WatchlistSettingsKeys.StarterListName,
                StringComparison.OrdinalIgnoreCase);
            var option = new WatchlistListOption(
                list.Name,
                isStarter ? _localization.GetString(DefaultListNameKey) : list.Name,
                isStarter);

            // The in-place positional diff: equal options stay untouched,
            // changed ones replace in place — the combo's collection
            // identity never changes (the combo trap).
            if (index < _lists.Count)
            {
                if (!_lists[index].Equals(option))
                {
                    _lists[index] = option;
                }
            }
            else
            {
                _lists.Add(option);
            }
        }

        while (_lists.Count > document.Lists.Count)
        {
            _lists.RemoveAt(_lists.Count - 1);
        }

        _activeListName = activeName;
        _appliedListName = activeName;
        if (_selectedListIndex != activeIndex)
        {
            _selectedListIndex = activeIndex;
            this.RaisePropertyChanged(nameof(SelectedListIndex));
        }
    }

    #endregion Document Publish

    #region List Editor

    /// <summary>Whether the editor's text can apply right now: trimmed non-empty and unique (the rename target excluded).</summary>
    private bool IsEditorTextApplicable(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || _document is not { } document)
        {
            return false;
        }

        foreach (var list in document.Lists)
        {
            var matches = string.Equals(list.Name, trimmed, StringComparison.OrdinalIgnoreCase);
            var isTarget = _editingListName is { } target &&
                string.Equals(list.Name, target, StringComparison.OrdinalIgnoreCase);
            if (matches && !isTarget)
            {
                return false;
            }
        }

        return true;
    }

    private void OpenNewListEditor()
    {
        _editingListName = null;
        IsListEditorRename = false;
        IsListEditorOpen = true;
        ListEditorText = string.Empty;
    }

    private void OpenRenameListEditor()
    {
        if (_selectedListIndex < 0 || _selectedListIndex >= _lists.Count)
        {
            return;
        }

        var option = _lists[_selectedListIndex];
        _editingListName = option.Name;
        IsListEditorRename = true;
        IsListEditorOpen = true;
        ListEditorText = option.Name;
    }

    private async Task<RxVoid> ConfirmListEditorAsync(CancellationToken ct)
    {
        var text = ListEditorText.Trim();
        if (text.Length == 0)
        {
            return RxVoid.Default;
        }

        IsListEditorOpen = false;
        try
        {
            if (_editingListName is { } target)
            {
                await _catalog.RenameListAsync(target, text, ct);
            }
            else
            {
                await _catalog.CreateListAsync(text, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The watchlist cannot apply the list name '{Name}'", text);
        }

        return RxVoid.Default;
    }

    private void CancelListEditor()
    {
        IsListEditorOpen = false;
        ListEditorText = string.Empty;
    }

    #endregion List Editor

    #region Row Actions

    private async Task<RxVoid> OpenSymbolPickerAsync(CancellationToken ct)
    {
        try
        {
            await AddSymbolRequested.Handle(RxVoid.Default);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The watchlist's symbol picker ride failed");
        }

        return RxVoid.Default;
    }

    private async Task<RxVoid> RemoveSelectedRowAsync(CancellationToken ct)
    {
        if (SelectedRowIndex is not (>= 0) ||
            SelectedRowIndex >= _rows.Count ||
            _activeListName is not { } list)
        {
            return RxVoid.Default;
        }

        var symbol = _rows[SelectedRowIndex].SymbolText;
        try
        {
            await _catalog.RemoveSymbolAsync(list, symbol, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The watchlist cannot remove '{Symbol}' from '{List}'", symbol, list);
        }

        return RxVoid.Default;
    }

    private async Task<RxVoid> MoveSelectedRowAsync(int step, CancellationToken ct)
    {
        if (SelectedRowIndex is not (>= 0) ||
            SelectedRowIndex >= _rows.Count ||
            _activeListName is not { } list)
        {
            return RxVoid.Default;
        }

        var symbol = _rows[SelectedRowIndex].SymbolText;
        try
        {
            await _catalog.MoveSymbolAsync(list, symbol, step, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The watchlist cannot move '{Symbol}' in '{List}'", symbol, list);
        }

        return RxVoid.Default;
    }

    private async Task ApplyListSwitchAsync(string name)
    {
        try
        {
            await _catalog.SetActiveAsync(name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The watchlist cannot switch to the list '{List}'", name);
        }
    }

    private async Task<RxVoid> DeleteActiveListAsync(CancellationToken ct)
    {
        if (_activeListName is not { } list)
        {
            return RxVoid.Default;
        }

        try
        {
            await _catalog.DeleteListAsync(list, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The watchlist cannot delete the list '{List}'", list);
        }

        return RxVoid.Default;
    }

    #endregion Row Actions
}

/// <summary>
/// One list option the panel's combo holds (T-196): the stored name (the
/// mutations' identity) and the display name (the starter token shows the
/// localized default, every other list its own stored name).
/// </summary>
/// <param name="Name">The list's stored name.</param>
/// <param name="DisplayName">The name the combo prints.</param>
/// <param name="IsStarter">Whether this is the starter list (never deletable).</param>
public sealed record WatchlistListOption(string Name, string DisplayName, bool IsStarter);
