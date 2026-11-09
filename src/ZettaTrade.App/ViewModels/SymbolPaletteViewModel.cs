namespace ZettaTrade.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Livery.Extensions;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>The half-day-agnostic status byte ReactiveUI.Primitives names RxVoid.</summary>
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// The global symbol palette: one Ctrl+K surface that fuzzy-matches
/// instruments across ALL registered adapters, applies the workspace
/// symbol context (a cross-exchange apply rides the SAME workspace
/// symbol preference the chart's setter maintains — a same-exchange
/// apply writes <c>ChartViewModel.SelectedSymbol</c> directly) and
/// greets every open with the session's recents and the user's
/// favorites — the palette never introduces a settings key of its own.
/// </summary>
/// <remarks>
/// Coordination only: the instrument state lives in
/// <see cref="SymbolPaletteSource"/>, the marks in the settings store and
/// every ranged action is a few lines around the ranked rows. The open is
/// instant: <see cref="OpenCoreAsync"/> ranks the current snapshot
/// synchronously — the query throttle only serves the keystrokes typed
/// AFTER the open — and both the focus-in and the focus-out ride
/// interactions, so the window keeps its focus discipline without the
/// ViewModel touching controls. The ViewModel lives as long as the
/// application (a workspace singleton), so its subscriptions mirror that
/// lifetime and need no explicit disposal, exactly like every workspace
/// panel's ViewModel.
/// </remarks>
public sealed class SymbolPaletteViewModel : ReactiveObject
{
    #region Constants

    /// <summary>How long a query settles before its ranking re-publishes (a keystroke pause is never ranked mid-flight).</summary>
    internal const int QueryThrottleMilliseconds = 40;

    /// <summary>How many rows one ranking pages at the top bound.</summary>
    internal const int MaxResults = 24;

    /// <summary>How many applied pairs the session's recents list keeps (newest first).</summary>
    internal const int MaxRecents = 8;

    /// <summary>How far one page-step jumps the keyboard selection.</summary>
    internal const int SelectionPage = 10;

    /// <summary>The selection standing nowhere.</summary>
    internal const int NoSelection = -1;

    #endregion Constants

    #region Properties

    private readonly ActiveExchangeService _activeExchanges;
    private readonly SymbolPaletteSource _source;
    private readonly ISettingsService _settings;
    private readonly ChartViewModel _chart;
    private readonly ILogger<SymbolPaletteViewModel> _logger;

    /// <summary>
    /// The session's recents: every applied pair lands at the head
    /// (deduped, bounded). In-memory ON PURPOSE — persistence is the
    /// favorites territory the workspace pairs tab owns; the palette
    /// stays recording-only.
    /// </summary>
    private readonly List<SymbolPaletteRow> _recents = new();

    /// <summary>
    /// The per-exchange favorites, freshly mirrored from the store: read
    /// at every open and kept current by the settings change stream.
    /// Read-only — the palette never writes marks (a star is made from
    /// the chart's toolbar, not from here).
    /// </summary>
    private IReadOnlyDictionary<string, IReadOnlySet<string>> _favoriteMarks =
        SymbolMarkMap.Parse(null);

    /// <summary>The snapshot the ranking reads; replaced before every publish it serves.</summary>
    private PaletteSnapshot _snapshot = new([], Settled: false);

    /// <summary>Every ask to focus the query rides this interaction (the window owns the actual focus).</summary>
    public Interaction<RxVoid, RxVoid> FocusQueryRequested { get; }

    /// <summary>Every close returns the pre-open focus through this interaction.</summary>
    public Interaction<RxVoid, RxVoid> FocusReturnRequested { get; }

    /// <summary>The Ctrl+K shortcut: a closed palette opens, an open one closes.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ToggleCommand { get; }

    /// <summary>The Escape short way out: close and hand the focus back.</summary>
    public ReactiveCommand<RxVoid, RxVoid> CloseCommand { get; }

    /// <summary>
    /// Enter's apply: a cross-exchange row switches the active exchange
    /// first, then the workspace symbol context is written.
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid> ApplySelectionCommand { get; }

    /// <summary>One row up.</summary>
    public ReactiveCommand<RxVoid, RxVoid> MoveSelectionUpCommand { get; }

    /// <summary>One row down.</summary>
    public ReactiveCommand<RxVoid, RxVoid> MoveSelectionDownCommand { get; }

    /// <summary>One page up.</summary>
    public ReactiveCommand<RxVoid, RxVoid> MoveSelectionPageUpCommand { get; }

    /// <summary>One page down.</summary>
    public ReactiveCommand<RxVoid, RxVoid> MoveSelectionPageDownCommand { get; }

    private bool _isOpen;

    /// <summary>Whether the palette overlay is up; the window binds its whole host to this.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        private set => this.RaiseAndSetIfChanged(ref _isOpen, value);
    }

    private string _query = string.Empty;

    /// <summary>
    /// The live query, bound two-way to the palette's text box. Every
    /// open resets it: a stale search never filters the next open.
    /// </summary>
    public string Query
    {
        get => _query;
        set => this.RaiseAndSetIfChanged(ref _query, value);
    }

    /// <summary>
    /// The ranked rows, updated in place on every publish — the
    /// collection identity never changes (a swap would reset the list's
    /// selection machinery, and the deferred re-point would write a
    /// stale row back through the two-way binding, the combo's known
    /// trap).
    /// </summary>
    public ObservableCollection<SymbolPaletteRow> Rows { get; } = new();

    private int _selectedIndex = NoSelection;

    /// <summary>
    /// The keyboard selection (two-way: the list's own selection lands
    /// here too, so Enter applies what a click chose). Every publish
    /// re-points it to the head — a changed list cannot keep a stale
    /// highlight.
    /// </summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedIndex, value);
    }

    /// <summary>Whether the ranked rows carry anything to show.</summary>
    public bool HasRows => Rows.Count > 0;

    /// <summary>
    /// Whether the user's query found none over a non-empty source (the
    /// palette mirrors the adapters' offerings, not their silence).
    /// </summary>
    public bool HasNoMatches => !HasRows && _snapshot.Settled && _snapshot.Instruments.Count > 0;

    /// <summary>
    /// Whether the source settled on an empty aggregate: every registered
    /// adapter served nothing (loading lives elsewhere).
    /// </summary>
    public bool HasNoInstruments => !HasRows && _snapshot.Settled && _snapshot.Instruments.Count == 0;

    /// <summary>
    /// Whether the source has not completed its first fetch yet: nothing
    /// settled, so the palette shows the honest loading state.
    /// </summary>
    public bool IsSourceWaiting =>
        !HasRows && !_snapshot.Settled && _snapshot.Instruments.Count == 0;

    private bool _isPickingCompare;

    /// <summary>
    /// Whether the palette rides the chart's compare pick (T-124): the
    /// rows rank the CURRENT exchange only (the compare fetch reads the
    /// standing adapter — a foreign-exchange row could never serve a
    /// fetchable series), Enter completes the pending pick with the
    /// highlighted row's symbol, and any close completes it with null.
    /// </summary>
    public bool IsPickingCompare
    {
        get => _isPickingCompare;
        private set => this.RaiseAndSetIfChanged(ref _isPickingCompare, value);
    }

    /// <summary>The pending compare pick's resolution sink (see <see cref="BeginComparePickAsync"/>); null while no pick stands.</summary>
    private TaskCompletionSource<Symbol?>? _comparePickTcs;

    /// <summary>
    /// Begin the chart's compare pick (T-124): the palette opens in
    /// compare mode and the returned task completes with the picked
    /// symbol, null on a close without a pick. A second pick while one
    /// stands resolves the first with null — at most one pending pick,
    /// the newer wins.
    /// </summary>
    public async Task<Symbol?> BeginComparePickAsync(CancellationToken ct = default)
    {
        _comparePickTcs?.TrySetResult(null);
        _comparePickTcs = new TaskCompletionSource<Symbol?>(TaskCreationOptions.RunContinuationsAsynchronously);
        IsPickingCompare = true;

        await OpenCoreAsync(ct);

        return await _comparePickTcs.Task;
    }

    #endregion Properties

    #region Constructor

    /// <summary>
    /// Create the palette: the snapshot, marks and query pipelines wire
    /// here, and the commands wire as one-shot tasks over the exact state
    /// the palette stands in.
    /// </summary>
    /// <param name="activeExchanges">The exchange facade the apply seam
    /// reads (the active id) and switches through (the row's exchange
    /// first, the symbol write second — the T-017 ordering).</param>
    /// <param name="source">The instrument snapshot state (all adapters).</param>
    /// <param name="settings">The settings store the favorites marks ride
    /// — read by the palette, never written.</param>
    /// <param name="chart">The chart ViewModel whose
    /// <c>SelectedSymbol</c> IS the workspace symbol context.</param>
    /// <param name="logger">The palette's own logger.</param>
    public SymbolPaletteViewModel(
        ActiveExchangeService activeExchanges,
        SymbolPaletteSource source,
        ISettingsService settings,
        ChartViewModel chart,
        ILogger<SymbolPaletteViewModel> logger)
    {
        _activeExchanges = activeExchanges ?? throw new ArgumentNullException(nameof(activeExchanges));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _chart = chart ?? throw new ArgumentNullException(nameof(chart));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        FocusQueryRequested = new Interaction<RxVoid, RxVoid>();
        FocusReturnRequested = new Interaction<RxVoid, RxVoid>();

        // Every re-published snapshot re-ranks the OPEN palette (a source
        // refresh lands while the user types); a closed palette re-ranks
        // at its next open. The marshal keeps the collection writes on
        // the user-interface thread like every combo-driving write.
        _source.Snapshots
            .ObserveOnUiThread()
            .Subscribe(
                snapshot =>
                {
                    _snapshot = snapshot;
                    if (IsOpen)
                    {
                        PublishCurrentRank();
                    }
                },
                ex => _logger.LogError(ex, "The palette snapshot stream stopped unexpectedly"));

        // The favorites: one seeded read fills the mirror, the change
        // stream keeps it current (the same seed + changes discipline the
        // settings window's pairs tab rides). Never a write.
        Observable.FromAsync(() => _settings.GetAsync<string>(
                UiSettingsKeys.WorkspaceSection,
                UiSettingsKeys.WorkspaceFavoriteSymbols))
            .Subscribe(
                StoreFavoriteMarks,
                ex => _logger.LogError(ex, "Failed to read the favorites map"));

        _settings.Changes
            .Where(change => change.Section == UiSettingsKeys.WorkspaceSection &&
                             change.Key == UiSettingsKeys.WorkspaceFavoriteSymbols)
            .Select(change => change.NewValue)
            .OfType<string>()
            .Subscribe(
                StoreFavoriteMarks,
                ex => _logger.LogError(ex, "The favorites marks stream stopped unexpectedly"));

        // The keystrokes settle for QueryThrottleMilliseconds before
        // their ranking publishes; the marshal lands every ranked write
        // on the user-interface thread. The instant open ranks
        // synchronously BEFORE this pipeline ever fires, so the throttle
        // only owns the keystrokes typed after the open.
        this.WhenAnyValue(x => x.Query)
            .Throttle(TimeSpan.FromMilliseconds(QueryThrottleMilliseconds))
            .ObserveOnUiThread()
            .Subscribe(
                _ => PublishCurrentRank(),
                ex => _logger.LogError(ex, "The palette ranking stream stopped unexpectedly"));

        ToggleCommand = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>((_, ct) => ToggleAsync(ct));
        CloseCommand = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(
            (_, ct) => CloseCoreAsync(ct),
            this.WhenAnyValue(x => x.IsOpen));
        ApplySelectionCommand = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(
            (_, ct) => ApplySelectedAsync(ct),
            this.WhenAnyValue(
                x => x.IsOpen,
                x => x.SelectedIndex,
                (open, index) => open && HasRowAt(index)));
        MoveSelectionUpCommand = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(
            (_, ct) => MoveSelectionAsync(ct, -1),
            this.WhenAnyValue(
                x => x.IsOpen,
                x => x.SelectedIndex,
                (open, index) => open && index > 0));
        MoveSelectionDownCommand = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(
            (_, ct) => MoveSelectionAsync(ct, 1),
            this.WhenAnyValue(
                x => x.IsOpen,
                x => x.SelectedIndex,
                (open, index) => open && HasRowAfter(index)));
        MoveSelectionPageUpCommand = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(
            (_, ct) => MoveSelectionAsync(ct, -SelectionPage),
            this.WhenAnyValue(
                x => x.IsOpen,
                x => x.SelectedIndex,
                (open, index) => open && index > 0));
        MoveSelectionPageDownCommand = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(
            (_, ct) => MoveSelectionAsync(ct, SelectionPage),
            this.WhenAnyValue(
                x => x.IsOpen,
                x => x.SelectedIndex,
                (open, index) => open && HasRowAfter(index)));
    }

    #endregion Constructor

    #region Open and Close

    /// <summary>
    /// Toggle the palette: Ctrl+K from anywhere — a closed palette opens,
    /// an open one closes.
    /// </summary>
    private async Task<RxVoid> ToggleAsync(CancellationToken ct) =>
        IsOpen ? await CloseCoreAsync(ct) : await OpenCoreAsync(ct);

    /// <summary>
    /// Open the palette instantly: the flag flips, the query resets and
    /// the current snapshot re-ranks right on the caller thread (the
    /// user-interface path a keystroke rides), then the window is asked
    /// to focus the query box. Nothing here waits for a fetch — the
    /// source's own stream re-ranks whenever a refresh lands later.
    /// </summary>
    private async Task<RxVoid> OpenCoreAsync(CancellationToken ct)
    {
        if (IsOpen)
        {
            return RxVoid.Default;
        }

        IsOpen = true;

        // The marks re-read at every open: the palette writes nothing,
        // but it must mirror the stars standing right now (the store is
        // in-memory; the changes stream keeps it current meanwhile).
        var storedFavorites = await _settings.GetAsync<string>(
            UiSettingsKeys.WorkspaceSection,
            UiSettingsKeys.WorkspaceFavoriteSymbols);
        _favoriteMarks = SymbolMarkMap.Parse(storedFavorites);

        // A fresh open starts from a clean query; RaiseAndSetIfChanged
        // fires only when the reset actually changes something, so the
        // throttled re-publish, if any, just re-lands the identical
        // ranking.
        Query = string.Empty;
        PublishCurrentRank();

        await FocusQueryRequested.Handle(RxVoid.Default);
        return RxVoid.Default;
    }

    /// <summary>
    /// Close the palette and return the focus: the window decides what
    /// "return the focus" means (its stashed pre-open element), so a
    /// palette opened from the chart drops the user back on the chart.
    /// A close also resolves the pending compare pick (T-124) with null —
    /// the ride never leaves a dangling task.
    /// </summary>
    private async Task<RxVoid> CloseCoreAsync(CancellationToken ct)
    {
        if (!IsOpen)
        {
            return RxVoid.Default;
        }

        IsOpen = false;
        EndComparePick(null);
        await FocusReturnRequested.Handle(RxVoid.Default);
        return RxVoid.Default;
    }

    /// <summary>
    /// Resolve the pending compare pick (T-124): the apply answers with
    /// the picked symbol, every close answers with null, and both clear
    /// the ride mode — no pick survives its own resolution.
    /// </summary>
    private void EndComparePick(Symbol? picked)
    {
        IsPickingCompare = false;
        _comparePickTcs?.TrySetResult(picked);
        _comparePickTcs = null;
    }

    /// <summary>
    /// Apply the highlighted row: a same-exchange row writes the workspace
    /// symbol context directly; a cross-exchange row FIRST carries the
    /// applied pair into the workspace symbol preference and THEN switches
    /// the active exchange — the pending symbol bootstrap the switch kicks
    /// preselects the applied pair (the same preference the chart's own
    /// setter maintains), the delivered selection raises while the new
    /// exchange is already the current one, and its persist is suppressed
    /// (rows are not touched synchronously, so the rebuild's rebase never
    /// replaces the applied choice with the previous exchange's head).
    /// </summary>
    private async Task<RxVoid> ApplySelectedAsync(CancellationToken ct)
    {
        if (!IsOpen || !HasRowAt(SelectedIndex))
        {
            return RxVoid.Default;
        }

        var row = Rows[SelectedIndex];

        // The compare pick rides FIRST (T-124): the pending pick resolves
        // with the row's own symbol and the palette closes — no workspace
        // write, no exchange switch, no recents (the pick is chart-layer
        // state, never a workspace preference).
        if (_isPickingCompare)
        {
            EndComparePick(row.Symbol);
            await CloseCoreAsync(ct);
            return RxVoid.Default;
        }

        if (string.Equals(
                _activeExchanges.Current.Id,
                row.ExchangeId,
                StringComparison.OrdinalIgnoreCase))
        {
            _chart.SelectedSymbol = row.Symbol;
            RecordRecent(row);
            await CloseCoreAsync(ct);
            return RxVoid.Default;
        }

        try
        {
            // The applied pair rides the SAME workspace symbol preference the
            // chart's setter maintains (no palette key of its own): it is
            // written BEFORE the switch so the pending bootstrap reads it as
            // the preference and preselects the pair once it lands.
            await _settings.SetAsync(
                UiSettingsKeys.WorkspaceSection,
                UiSettingsKeys.WorkspaceSymbolId,
                row.Symbol.Normalized);
            await _activeExchanges.SwitchToAsync(row.ExchangeId, ct);
        }
        catch (ArgumentException ex)
        {
            // The snapshot's exchange left the registry between the rank
            // and the apply: a stale row cannot silently redirect the
            // workspace — the palette just closes, and the error stands
            // in the log. The preference keeps the applied pair for the
            // moment the adapter returns.
            _logger.LogError(
                ex,
                "The palette cannot apply a symbol whose exchange is no longer registered");
            await CloseCoreAsync(ct);
            return RxVoid.Default;
        }

        RecordRecent(row);
        await CloseCoreAsync(ct);
        return RxVoid.Default;
    }

    /// <summary>
    /// One keyboard selection move (minus/plus one for the arrows, a
    /// page for the page keys), clamped inside the rows. The near-edge
    /// correctness lives in the commands' canExecute guards; the clamp
    /// only guards the race where a ranking landed between the guard's
    /// evaluation and the call.
    /// </summary>
    private async Task<RxVoid> MoveSelectionAsync(CancellationToken ct, int step)
    {
        if (IsOpen && Rows.Count > 0)
        {
            var target = Math.Clamp(SelectedIndex + step, 0, Rows.Count - 1);
            if (target != SelectedIndex)
            {
                SelectedIndex = target;
            }
        }

        return RxVoid.Default;
    }

    #endregion Open and Close

    #region Ranking

    /// <summary>
    /// Rank the current state against the current query and publish the
    /// rows in place. A typed query rides the fuzzy matcher; a blank one
    /// pages the recents first, then the stars, then the natural
    /// (registration) order — every page capped at
    /// <see cref="MaxResults"/>.
    /// </summary>
    private void PublishCurrentRank()
    {
        var ranked = RankCurrent();

        // The in-place positional diff: the collection identity never
        // changes (the combo trap — and the two-way selection binding
        // re-points through a swapped collection the exact same way).
        // Equal rows stay untouched, so a re-publish of an identical
        // ranking never churns the visuals.
        for (var position = 0; position < ranked.Count; position++)
        {
            if (position < Rows.Count)
            {
                Rows[position] = ranked[position];
            }
            else
            {
                Rows.Add(ranked[position]);
            }
        }

        while (Rows.Count > ranked.Count)
        {
            Rows.RemoveAt(Rows.Count - 1);
        }

        // A changed list resets the keyboard selection to the head — a
        // stale highlight over renamed rows would apply the wrong pair.
        SelectedIndex = HasRows ? 0 : NoSelection;

        this.RaisePropertyChanged(nameof(HasRows));
        this.RaisePropertyChanged(nameof(HasNoMatches));
        this.RaisePropertyChanged(nameof(HasNoInstruments));
        this.RaisePropertyChanged(nameof(IsSourceWaiting));
    }

    /// <summary>The current state's ranked page (pure over the snapshot, the query, the recents and the marks).</summary>
    private IReadOnlyList<SymbolPaletteRow> RankCurrent()
    {
        // The compare mode (T-124) ranks the CURRENT exchange only: the
        // compare fetch reads the workspace's standing adapter, so a
        // foreign-exchange row could never serve a fetchable series. The
        // standing mode keeps the cross-exchange palette whole.
        var instruments = _isPickingCompare
            ? _snapshot.Instruments
                .Where(instrument => string.Equals(
                    instrument.ExchangeId,
                    _activeExchanges.Current.Id,
                    StringComparison.OrdinalIgnoreCase))
                .ToList()
            : _snapshot.Instruments;

        var query = Query;
        if (string.IsNullOrWhiteSpace(query))
        {
            return PageEmptyQuery(instruments);
        }

        return FuzzySymbolMatcher
            .Match(instruments, query, MaxResults)
            .Select(instrument => MarkRow(instrument, isRecent: false))
            .ToList();
    }

    /// <summary>
    /// The blank query's page: the session's recents first (newest at the
    /// head — only the pairs still served by the current snapshot; a
    /// dropped exchange's pair cannot apply), then the favorites in
    /// registration order, then the rest of the instruments in their
    /// natural order. Every page capped at <see cref="MaxResults"/>.
    /// </summary>
    private IReadOnlyList<SymbolPaletteRow> PageEmptyQuery(
        IReadOnlyList<PaletteInstrument> instruments)
    {
        var seen = new HashSet<(string ExchangeId, string Normalized)>();
        var page = new List<SymbolPaletteRow>(Math.Min(MaxResults, instruments.Count));

        void TryAdd(PaletteInstrument instrument, bool isRecent)
        {
            if (page.Count >= MaxResults)
            {
                return;
            }

            var key = (instrument.ExchangeId, instrument.Symbol.Normalized);
            if (!seen.Add(key))
            {
                return;
            }

            page.Add(MarkRow(instrument, isRecent));
        }

        foreach (var recent in _recents)
        {
            var instrument = instruments.FirstOrDefault(candidate =>
                string.Equals(candidate.ExchangeId, recent.ExchangeId, StringComparison.Ordinal) &&
                string.Equals(
                    candidate.Symbol.Normalized,
                    recent.Symbol.Normalized,
                    StringComparison.Ordinal));
            if (instrument is not null)
            {
                TryAdd(instrument, isRecent: true);
            }
        }

        foreach (var instrument in instruments)
        {
            if (_favoriteMarks.TryGetValue(instrument.ExchangeId, out var names) &&
                names.Contains(instrument.Symbol.Normalized))
            {
                TryAdd(instrument, isRecent: false);
            }
        }

        foreach (var instrument in instruments)
        {
            TryAdd(instrument, isRecent: false);
        }

        return page;
    }

    /// <summary>One instrument's row with the flags the current marks and recents say.</summary>
    private SymbolPaletteRow MarkRow(PaletteInstrument instrument, bool isRecent) =>
        new(
            instrument.Symbol,
            instrument.ExchangeId,
            instrument.ExchangeName,
            IsFavorite: _favoriteMarks.TryGetValue(instrument.ExchangeId, out var names) &&
                names.Contains(instrument.Symbol.Normalized),
            IsRecent: isRecent);

    /// <summary>Whether the position selects a row (the keyboard route and the apply command share this row predicate).</summary>
    public bool HasRowAt(int position) => position >= 0 && position < Rows.Count;

    /// <summary>Whether one more row stands after the position (the down/page-down side of the route's bounds).</summary>
    public bool HasRowAfter(int position) => position >= 0 && position < Rows.Count - 1;

    #endregion Ranking

    #region Recents and Marks

    /// <summary>
    /// Record an applied pair at the recents' head: deduped by exchange
    /// + symbol, bounded at <see cref="MaxRecents"/>, in-memory only
    /// (never persisted — the favorites store is another task's).
    /// </summary>
    private void RecordRecent(SymbolPaletteRow row)
    {
        _recents.RemoveAll(item =>
            string.Equals(item.ExchangeId, row.ExchangeId, StringComparison.Ordinal) &&
            string.Equals(item.Symbol.Normalized, row.Symbol.Normalized, StringComparison.Ordinal));
        _recents.Insert(0, new SymbolPaletteRow(
            row.Symbol,
            row.ExchangeId,
            row.ExchangeName,
            row.IsFavorite,
            IsRecent: true));
        while (_recents.Count > MaxRecents)
        {
            _recents.RemoveAt(_recents.Count - 1);
        }
    }

    /// <summary>
    /// Mirror a fresh favorites document: the palette stays read-only,
    /// and the mirror re-ranks an OPEN palette (a closed one re-reads at
    /// its next open anyway).
    /// </summary>
    private void StoreFavoriteMarks(string storedFavorites)
    {
        _favoriteMarks = SymbolMarkMap.Parse(storedFavorites);
        if (IsOpen)
        {
            PublishCurrentRank();
        }
    }

    #endregion Recents and Marks
}
