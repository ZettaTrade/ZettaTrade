namespace ZettaTrade.App.ViewModels;

using System.Collections.ObjectModel;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.App.Services;
using ZettaTrade.Appearance.Extensions;
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Chart panel state: the available symbols list, the selected symbol and the
/// loaded bars. Application-lifetime singleton: the pipelines are built in the
/// constructor — WhenActivated never fires for VMs bound as nested-panel
/// DataContexts (the documented workspace fact), so there is no activation path.
/// </summary>
/// <remarks>
/// The chart owns the workspace symbol context: <see cref="SymbolSelections"/>
/// publishes every confirmed selection (the workspace combo change and the
/// symbols preselect both raise <see cref="SelectedSymbol"/>) for watchers such
/// as the order book panel. <see cref="BarsUpdates"/> stays raw and testable;
/// <see cref="Bars"/> is assigned after the UI-thread hop. The exchange
/// context is inherited from the active-exchange facade: both the symbol
/// bootstrap and the bars machine re-run on every adapter change, so the
/// workspace follows the toolbar selector blind. The time frame is the
/// third load-key ingredient and persists as <c>UI.ChartTimeFrame</c>: the
/// constructor restore (a startup-walk <see cref="InitializeAsync"/> call
/// applies the stored choice before any window binds) re-loads the chart
/// through the same machine a combo flip drives, and user changes persist.
/// The clock format is a pure consumer of the settings service: the startup
/// walk applies the stored <c>UI.ChartUse12HourClock</c> and every settings
/// change re-applies it live (the settings window is its only writer).
/// The workspace symbol persists as <c>Workspace.SymbolId</c>: every
/// bootstrap reads the preference first and the preselection prefers it
/// (persisted → preserved-current → first entry → empty), every user
/// selection writes it back, and a bootstrap application never rewrites —
/// it either re-applies the just-read preference (no duplicate write) or
/// records the logged fall-through (the persisted value is a preference,
/// never a silently substituted default).
/// The per-symbol marks persist per workspace exchange as the
/// <c>Workspace.FavoriteSymbols</c> / <c>Workspace.HiddenSymbols</c> JSON
/// maps (see <see cref="Services.SymbolMarkMap"/>): the bootstrap reads
/// both on every exchange switch and builds the rows favorites-first with
/// the hidden pairs filtered, the settings window's pairs tab writes both
/// maps back (the favorites and the hidden ones), and a hidden symbol never
/// takes the chart — when a save hides the shown pair the chart switches to
/// the first visible one (persisted, so a restart does not re-open it
/// either).
/// The ViewModel lives as long as the application (singleton), so the
/// subscriptions mirror it and need no explicit disposal.
/// </remarks>
public sealed class ChartViewModel : ReactiveObject, IAsyncInitializable
{
    #region Constants

    /// <summary>Upper bound of the symbols dropdown in the demo view.</summary>
    private const int MaxVisibleSymbols = 50;

    /// <summary>The nominal month length for MN1 history starts.</summary>
    private const int NominalMonthDays = 30;

    /// <summary>The nominal week length for W1 history starts.</summary>
    private const int NominalWeekDays = 7;

    /// <summary>Initial number of candles loaded for the chart.</summary>
    private const int InitialBarCount = 200;

    #endregion

    #region Properties

    private readonly ISettingsService _settings;
    private readonly ILogger<ChartViewModel> _logger;
    private readonly ActiveExchangeService _activeExchanges;
    private readonly SymbolListProvider _symbolList;

    /// <summary>
    /// Whether the constructor restore is writing its own value into
    /// <see cref="SelectedTimeFrame"/>: the persistence chain suppresses
    /// exactly that assignment (a restore is not a user edit — repersisting
    /// the just-read value would be a duplicate write with no information).
    /// </summary>
    private bool _isRestoringTimeFrame;

    /// <summary>
    /// Whether the symbols bootstrap is applying its own selection: the
    /// symbol persistence chain suppresses exactly those assignments. A
    /// bootstrap either re-applies the workspace preference it just read
    /// (repersisting it would be a duplicate write with no information) or
    /// applies the documented fall-through (writing the substitute would
    /// overwrite the preference it merely deferred).
    /// </summary>
    private bool _isApplyingBootstrapSelection;

    private ObservableCollection<Bar> _bars = new();

    /// <summary>Collection of bars to display in the chart.</summary>
    public ObservableCollection<Bar> Bars
    {
        get => _bars;
        private set => this.RaiseAndSetIfChanged(ref _bars, value);
    }

    private Symbol? _selectedSymbol;

    /// <summary>
    /// Currently selected trading symbol. Every user change persists as
    /// <c>Workspace.SymbolId</c> (bootstrap applications never rewrite it —
    /// see the class remarks), and the confirmed selections flow through
    /// <see cref="SymbolSelections"/> for the whole workspace.
    /// </summary>
    public Symbol? SelectedSymbol
    {
        get => _selectedSymbol;
        set => this.RaiseAndSetIfChanged(ref _selectedSymbol, value);
    }

    private TimeFrame _selectedTimeFrame = TimeFrame.H1;

    /// <summary>
    /// The time frame the chart loads bars for. Defaults to H1 — today's
    /// hardcoded behavior — until <see cref="InitializeAsync"/> applies the
    /// persisted choice; every combo change re-loads the chart AND persists
    /// the choice for the next cold start.
    /// </summary>
    public TimeFrame SelectedTimeFrame
    {
        get => _selectedTimeFrame;
        set => this.RaiseAndSetIfChanged(ref _selectedTimeFrame, value);
    }

    private bool _use12HourClock;

    /// <summary>
    /// Whether the chart time axis prints the 12-hour clock ("11:45 PM");
    /// false — the default — prints the 24-hour one. The settings service is
    /// the single source of truth: the startup walk seeds the persisted
    /// value and every settings change re-applies it live, so the settings
    /// window needs no direct wiring into the chart pipeline.
    /// </summary>
    public bool Use12HourClock
    {
        get => _use12HourClock;
        private set => this.RaiseAndSetIfChanged(ref _use12HourClock, value);
    }

    /// <summary>Every time frame the picker offers, in enum order (M1…MN1).</summary>
    public IReadOnlyList<TimeFrame> TimeFrames { get; } =
    [
        TimeFrame.M1,
        TimeFrame.M5,
        TimeFrame.M15,
        TimeFrame.M30,
        TimeFrame.H1,
        TimeFrame.H4,
        TimeFrame.D1,
        TimeFrame.W1,
        TimeFrame.MN1,
    ];

    private readonly ObservableCollection<SymbolRow> _availableSymbols = new();

    /// <summary>
    /// Rows of the symbols combo: the exchange's list with the persisted
    /// marks applied (favorites first, hidden pairs filtered out). Rebuilt
    /// on every marks, selection and exchange change — in place: the
    /// collection identity never changes (a swap makes the combo's selection
    /// machinery reset and re-point, and the deferred re-point then writes a
    /// stale row back through the two-way binding, rebasing the selection).
    /// </summary>
    public ObservableCollection<SymbolRow> AvailableSymbols => _availableSymbols;

    /// <summary>
    /// Whether a rows rebuild (or the bootstrap's rows application) is in
    /// progress: the two-way combo binding's write-backs arriving in here
    /// (clicks' own echoes, selection-machinery re-points) carry no
    /// information — the rebuild re-commits the correct row itself, and the
    /// write-back must not substitute its stale symbol for the selection.
    /// </summary>
    private bool _isUpdatingSymbolRows;

    /// <summary>
    /// Replace the rows in place with the rebuilt list: positional diffing
    /// keeps unchanged rows untouched (records compare by value, so a
    /// rebuild that changed nothing raises no events at all — the combo's
    /// selection stays exactly as the interaction committed it), removed
    /// rows leave the tail, added rows land on top of it.
    /// </summary>
    private void UpdateRowsInPlace(IReadOnlyList<SymbolRow> rows)
    {
        for (var index = 0; index < rows.Count; index++)
        {
            if (index < _availableSymbols.Count &&
                Equals(_availableSymbols[index], rows[index]))
            {
                continue;
            }

            if (index < _availableSymbols.Count)
            {
                _availableSymbols[index] = rows[index];
            }
            else
            {
                _availableSymbols.Add(rows[index]);
            }
        }

        while (_availableSymbols.Count > rows.Count)
        {
            _availableSymbols.RemoveAt(_availableSymbols.Count - 1);
        }
    }

    private SymbolRow? _selectedSymbolRow;

    /// <summary>
    /// The combo's selected ROW: the two-way shim between the combo and
    /// <see cref="SelectedSymbol"/> (the row list cannot bind SelectedItem
    /// to a Symbol directly, and SelectedValueBinding broke item container
    /// generation on Avalonia 12). Reading returns the row matching the
    /// canonical selection (kept current by every rows rebuild); writing a
    /// row forwards its symbol into <see cref="SelectedSymbol"/> — except
    /// while a rebuild is in flight (<see cref="_isUpdatingSymbolRows"/>):
    /// the rebuild re-commits the correct row itself, so a write-back
    /// arriving in here (the click's own echo, the selection machinery
    /// re-pointing on collection changes) must not substitute its symbol
    /// for the selection — that write-back is what rebased the selection
    /// to the list head before the rebuilds stopped replacing the rows
    /// collection.
    /// </summary>
    public SymbolRow? SelectedSymbolRow
    {
        get => _selectedSymbolRow;
        set
        {
            if (_isUpdatingSymbolRows)
            {
                if (!EqualityComparer<SymbolRow?>.Default.Equals(_selectedSymbolRow, value))
                {
                    _selectedSymbolRow = value;
                    this.RaisePropertyChanged(nameof(SelectedSymbolRow));
                }

                return;
            }

            if (EqualityComparer<SymbolRow?>.Default.Equals(_selectedSymbolRow, value))
            {
                return;
            }

            _selectedSymbolRow = value;
            this.RaisePropertyChanged(nameof(SelectedSymbolRow));
            if (value is not null)
            {
                SelectedSymbol = value.Symbol;
            }
        }
    }

    /// <summary>
    /// Every confirmed symbol selection: the workspace combo and the symbols
    /// preselect both flow through here. Raw — not UI-marshaled.
    /// </summary>
    public IObservable<Symbol> SymbolSelections { get; }

    /// <summary>
    /// Every workspace time-frame change: the current value arrives at
    /// subscribe time, so a late follower starts from the standing choice.
    /// Raw — not UI-marshaled (the chat context consumes it the same way the
    /// chart's own pipelines do).
    /// </summary>
    public IObservable<TimeFrame> TimeFrameSelections { get; }

    /// <summary>
    /// Bars loaded for the latest (symbol, time frame, exchange) triple; a
    /// newer triple cancels the previous load (Rx Switch). The machine is
    /// hot and shared: exactly one load pipeline runs no matter how many
    /// consumers subscribe, and a late watcher immediately gets the latest
    /// bars. Raw — not UI-marshaled; user-interface consumers marshal
    /// themselves.
    /// </summary>
    public IObservable<IReadOnlyList<Bar>> BarsUpdates { get; }

    /// <summary>
    /// The prepend slices produced by older-history paging: every emission
    /// is the strictly-ascending, deduped slice that arrived older than the
    /// series' current head (see <see cref="BarSeriesMerge.PrependOlder"/>).
    /// Raw — the UI-thread subscriber inserts it at the head of
    /// <see cref="Bars"/>; a test consumer reads the semantics off the raw
    /// stream. Empty and null-guard results never emit (the request path
    /// resolves them into exhaustion or a logged rejection).
    /// </summary>
    public IObservable<IReadOnlyList<Bar>> OlderBarsUpdates { get; }

    /// <summary>
    /// The indicator overlay lines computed for the current series (see
    /// <see cref="ChartIndicatorPipeline.Compute"/>). Raw - not UI-marshaled;
    /// the property assignment is the marshalled hop, this stream lets tests
    /// and future consumers read the semantics off the raw publish path.
    /// </summary>
    public IObservable<IReadOnlyList<ChartIndicatorLine>> IndicatorLinesUpdates { get; }

    private IReadOnlyList<ChartIndicatorLine> _indicatorLines = [];

    /// <summary>
    /// The indicator overlays the chart draws: one line per enabled settings
    /// row, values aligned with <see cref="Bars"/> by index. The compute runs
    /// on the caller's thread and only this assignment marshals (the same
    /// discipline as <see cref="Bars"/>), so the draw pass can slice it with
    /// the visible window without any cross-thread grid.
    /// </summary>
    public IReadOnlyList<ChartIndicatorLine> IndicatorLines
    {
        get => _indicatorLines;
        private set => this.RaiseAndSetIfChanged(ref _indicatorLines, value);
    }

    /// <summary>
    /// One delivered full load, with the (symbol, time frame, adapter) triple
    /// that produced it: the private payload the delivered subscriber uses to
    /// pin <see cref="_loadedTarget"/> for the paging path.
    /// </summary>
    private sealed record ChartBarsPage(
        Symbol Symbol,
        TimeFrame TimeFrame,
        IMarketDataAdapter Adapter,
        IReadOnlyList<Bar> Bars);

    #endregion

    #region Older History State

    /// <summary>
    /// The (symbol, time frame, exchange) pair the LAST delivered full load
    /// produced: paging rides exactly it. A page racing a rebase (the
    /// delivered bundle replaced the series underneath the pending fetch) is
    /// dropped — the rebase owns the series, not the stale page.
    /// </summary>
    private (Symbol Symbol, TimeFrame TimeFrame, string ExchangeId)? _loadedTarget;

    /// <summary>The adapter the last load delivered through; paging fetches through it (recorded with <see cref="_loadedTarget"/>).</summary>
    private IMarketDataAdapter? _loadedAdapter;

    /// <summary>Whether an older-history fetch is in flight (the request path serializes on it; a request racing the release is dropped and the next pan crossing re-raises it).</summary>
    private bool _olderFetchInFlight;

    /// <summary>Prepend slices queued on the UI thread but not inserted yet: a new request waits for the series' head to move (a duplicate fetch would dedupe to zero and fake exhaustion).</summary>
    private int _olderAppendsPending;

    /// <summary>Whether deeper history proved absent at the current anchor (the empty prepend result); a rebase clears it so the fresh series pages again.</summary>
    private bool _olderHistoryExhausted;

    /// <summary>Cancels the in-flight older-history fetch when a rebase lands (the loading pipeline owns the series; a stale page never merges into a fresh one).</summary>
    private CancellationTokenSource? _olderFetchEpoch;

    /// <summary>The paging requests' sink (hot; see <see cref="OlderBarsUpdates"/>).</summary>
    private readonly Subject<IReadOnlyList<Bar>> _olderPages = new();

    /// <summary>
    /// The full-load pipeline carrying each delivered load with its triple
    /// (see <see cref="ChartBarsPage"/>): <see cref="BarsUpdates"/> maps the
    /// raw bars off it, the delivered subscriber reads the page.
    /// </summary>
    private IObservable<ChartBarsPage> _barsPages = default!;

    #endregion

    #region Indicator Overlay State

    /// <summary>
    /// The indicator overlay publishes here; the marshalled subscription
    /// assigns <see cref="IndicatorLines"/>. The compute itself is pure -
    /// the publish runs wherever its callers land and only the assignment
    /// crosses onto the UI thread.
    /// </summary>
    private readonly Subject<IReadOnlyList<ChartIndicatorLine>> _indicatorPublishes = new();

    /// <summary>The catalog the indicator settings rows resolve through (the plugin seam).</summary>
    private readonly IIndicatorCatalog _indicatorCatalog;

    /// <summary>
    /// The persisted indicator configuration: seeded from the settings
    /// service, kept current by the changes stream, recomputed against on
    /// every bars delivery and prepend. The compute is cheap (two 200-bar
    /// averages), so the config rides by value and never needs a cache
    /// invalidation dance.
    /// </summary>
    private string _persistedIndicatorConfig = UiSettingsKeys.DefaultChartIndicators;

    #endregion

    #region Symbol Marks State

    /// <summary>The last bootstrap's full symbol list (before the marks are applied): every rows rebuild re-reads it.</summary>
    private IReadOnlyList<Symbol> _exchangeSymbols = [];

    /// <summary>The persisted favorites map (all exchanges): seeded at bootstrap, kept current by the changes stream and the toggles.</summary>
    private IReadOnlyDictionary<string, IReadOnlySet<string>> _favoriteMarks =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

    /// <summary>The persisted hidden map (all exchanges): the same lifecycle as <see cref="_favoriteMarks"/>.</summary>
    private IReadOnlyDictionary<string, IReadOnlySet<string>> _hiddenMarks =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

    /// <summary>
    /// The exchange the cached lists apply to: the marks resolve per workspace exchange.
    /// </summary>
    private string _marksExchangeId = string.Empty;

    /// <summary>
    /// Monotonic counter of the symbol bootstraps STARTED so far (BUG-016):
    /// every adapter change begins its own fetch and claims the next epoch;
    /// a delivery applies only while its epoch is still the current one.
    /// Two rapid exchange switches deliver out of order — the older fetch's
    /// delivery lands AFTER the newer switch started its fetch and drops,
    /// so an old exchange's list and selection never clobber the new one's.
    /// The same epoch scheme the older-history machinery runs against a
    /// rebase, stated on a counter over a cancellation token: dropping on
    /// delivery (not cancelling the fetch) keeps the provider cache warm
    /// for whichever exchange owns the workspace next.
    /// </summary>
    private int _symbolBootstrapEpoch;

    #endregion

    #region Constructors

    /// <summary>
    /// Create a new ChartViewModel instance.
    /// </summary>
    /// <param name="settings">The app settings service; the time frame
    /// restore reads <see cref="UiSettingsKeys.ChartTimeFrame"/> and every
    /// user change persists there, the clock formats read and live-apply
    /// <see cref="UiSettingsKeys.ChartUse12HourClock"/>, the workspace
    /// symbol reads and persists
    /// <see cref="UiSettingsKeys.WorkspaceSymbolId"/>, and the symbol marks
    /// read <see cref="UiSettingsKeys.WorkspaceFavoriteSymbols"/> /
    /// <see cref="UiSettingsKeys.WorkspaceHiddenSymbols"/> at every
    /// bootstrap while the toolbar toggles persist the toggled map.</param>
    /// <param name="indicatorCatalog">The plugin seam the indicator overlay
    /// rows resolve through; the enabled rows compute against the current
    /// series on every delivery, prepend and settings change.</param>
    public ChartViewModel(
        ActiveExchangeService activeExchanges,
        SymbolListProvider symbolList,
        ISettingsService settings,
        IIndicatorCatalog indicatorCatalog,
        ILogger<ChartViewModel> logger)
    {
        _activeExchanges = activeExchanges;
        _symbolList = symbolList;
        _settings = settings;
        _indicatorCatalog = indicatorCatalog;
        _logger = logger;

        SymbolSelections = this.WhenAnyValue(x => x.SelectedSymbol)
            .Where(symbol => symbol is not null)
            .Select(symbol => symbol!);

        TimeFrameSelections = this.WhenAnyValue(x => x.SelectedTimeFrame);

        // Bars re-load on every symbol, time frame or active-exchange change:
        // the triple pins the exact load target (exchange + timeframe), so
        // each load serves precisely its triggering change, and the Switch
        // drops the in-flight load of a superseded triple (atomic by
        // construction) — a rapid time frame flip leaves exactly one load.
        // Every delivered load rides its triple as a private page: the
        // public BarsUpdates stays the raw bars, the delivered subscriber
        // reads the page to pin the paging target.
        _barsPages = SymbolSelections
            .CombineLatest(
                this.WhenAnyValue(x => x.SelectedTimeFrame),
                activeExchanges.AdapterChanges,
                (symbol, timeFrame, adapter) => (Symbol: symbol, TimeFrame: timeFrame, Adapter: adapter))
            .Select(triple =>
                LoadBars(triple.Symbol, triple.TimeFrame, triple.Adapter)
                    .Select(bars => new ChartBarsPage(triple.Symbol, triple.TimeFrame, triple.Adapter, bars)))
            .Switch()
            .Replay(1)
            .RefCount();

        BarsUpdates = _barsPages.Select(static page => page.Bars);
        OlderBarsUpdates = _olderPages;
        IndicatorLinesUpdates = _indicatorPublishes;

        // The ViewModel lives as long as the application (singleton), so this
        // subscription mirrors it and needs no explicit disposal. The marshal
        // chain keeps <see cref="Bars"/> on the Avalonia UI thread.
        _barsPages.ObserveOnUiThread()
            .Subscribe(
                page =>
                {
                    // A full load is a rebase: the series belongs to the new
                    // triple. The paging state resets with it — the in-flight
                    // stale fetch is cancelled, exhaustion clears, and the
                    // new series pages from its own head.
                    _olderFetchEpoch?.Cancel();
                    _olderFetchEpoch = null;
                    _olderAppendsPending = 0;
                    _olderHistoryExhausted = false;
                    _loadedTarget = (page.Symbol, page.TimeFrame, page.Adapter.Id);
                    _loadedAdapter = page.Adapter;

                    Bars = new ObservableCollection<Bar>(page.Bars);
                    _logger.LogInformation("Chart bars delivered: {Count} candles", page.Bars.Count);

                    // The overlays re-compute against the fresh series: the
                    // delivered list IS the compute input (a 200-bar copy of
                    // the collection's content, marshal-free).
                    PublishIndicatorLines(page.Bars);
                },
                ex => _logger.LogError(ex, "The chart bars stream stopped unexpectedly"));

        // The prepend slices insert at the series' head on the UI thread;
        // the request path serialized them (one in flight, one queued at
        // most) and counts them pending until this insertion runs.
        _olderPages.ObserveOnUiThread()
            .Subscribe(
                prepend =>
                {
                    _olderAppendsPending--;
                    for (var i = prepend.Count - 1; i >= 0; i--)
                    {
                        Bars.Insert(0, prepend[i]);
                    }

                    _logger.LogInformation(
                        "Chart bars appended: {Count} older candles for {Symbol} {TimeFrame}",
                        prepend.Count,
                        _loadedTarget?.Symbol.Normalized,
                        _loadedTarget?.TimeFrame);

                    // The prepends GROW the series: the overlay values must
                    // re-align with the grown series (the compute runs over
                    // the whole collection right here on the UI thread).
                    PublishIndicatorLines(Bars);
                },
                ex => _logger.LogError(ex, "The chart older-bars stream stopped unexpectedly"));

        // The time frame persists: every USER change writes the enum name
        // into the UI settings section (the debounced settings service calls
        // the file I/O). The subscribe-time bootstrap emission is skipped,
        // and the restore's own assignment is suppressed by
        // <see cref="_isRestoringTimeFrame"/> — reads are never re-written.
        this.WhenAnyValue(x => x.SelectedTimeFrame)
            .Skip(1)
            .Where(_ => !_isRestoringTimeFrame)
            .SelectMany(timeFrame => Observable.FromAsync(() => _settings.SetAsync(
                UiSettingsKeys.Section,
                UiSettingsKeys.ChartTimeFrame,
                timeFrame.ToString())))
            .Subscribe(
                _ => { },
                ex => _logger.LogError(ex, "Failed to persist the chart time frame"));

        // The workspace symbol persists: every USER selection writes the
        // normalized name into the Workspace section (the order book and the
        // order dialog follow the same stream). The subscribe-time
        // current-value echo is skipped and the bootstrap pipeline's own
        // assignments are suppressed by <see cref="_isApplyingBootstrapSelection"/>:
        // a bootstrap either re-applies the just-read preference (a duplicate
        // write with no information) or applies the logged fall-through
        // (persisting the substitute would destroy the preference it
        // deferred). The two-way combo binding is the only writer that gets
        // through; a cleared selection never erases the preference.
        this.WhenAnyValue(x => x.SelectedSymbol)
            .Skip(1)
            .Where(_ => !_isApplyingBootstrapSelection)
            .Where(symbol => symbol is not null)
            .Select(symbol => symbol!)
            .SelectMany(symbol => Observable.FromAsync(() => _settings.SetAsync(
                UiSettingsKeys.WorkspaceSection,
                UiSettingsKeys.WorkspaceSymbolId,
                symbol.Normalized)))
            .Subscribe(
                _ => { },
                ex => _logger.LogError(ex, "Failed to persist the workspace symbol"));

        // The clock format follows the settings service live: the settings
        // window writes UI.ChartUse12HourClock and this property re-applies
        // it without a restart. Non-bool values are dropped — the value the
        // startup walk applied stays in charge until a real change arrives
        // (the settings change stream publishes synchronously, so a plain
        // subscription is UI-thread safe, same contract as the bars hop).
        _settings.Changes
            .Where(change => change.Section == UiSettingsKeys.Section &&
                             change.Key == UiSettingsKeys.ChartUse12HourClock)
            .Select(change => change.NewValue)
            .OfType<bool>()
            .Subscribe(
                use12HourClock => Use12HourClock = use12HourClock,
                ex => _logger.LogError(ex, "The chart clock format stream stopped unexpectedly"));

        // The indicator overlay follows the settings service live: the
        // settings window writes UI.ChartIndicators and every row re-applies
        // without a restart. One async read seeds the persisted config
        // (the default SMA row stands until it lands) and the changes stream
        // keeps it current; non-string values are dropped the same way the
        // clock drops them.
        Observable.FromAsync(() => _settings.GetAsync<string>(
                UiSettingsKeys.Section,
                UiSettingsKeys.ChartIndicators,
                UiSettingsKeys.DefaultChartIndicators))
            .Subscribe(
                ApplyIndicatorConfig,
                ex => _logger.LogError(ex, "Failed to read the chart indicator config"));

        _settings.Changes
            .Where(change => change.Section == UiSettingsKeys.Section &&
                             change.Key == UiSettingsKeys.ChartIndicators)
            .Select(change => change.NewValue)
            .OfType<string>()
            .Subscribe(
                ApplyIndicatorConfig,
                ex => _logger.LogError(ex, "The chart indicator config stream stopped unexpectedly"));

        // The computed overlays ride the same marshal chain the Bars do: the
        // publishes land on whatever thread ran the compute (delivered and
        // prepend subscribers are already marshalled, the settings paths run
        // synchronously), only the property assignment hops.
        _indicatorPublishes.ObserveOnUiThread()
            .Subscribe(
                lines => IndicatorLines = lines,
                ex => _logger.LogError(ex, "The chart indicator lines stream stopped unexpectedly"));


        // The symbol marks follow the settings service live (the same pure
        // -consumer contract the clock format keeps): a change to either
        // marks key re-parses the map, re-applies it against the current
        // exchange's list and rebuilds the rows. Non-string payloads drop —
        // the applied marks stay in charge.
        _settings.Changes
            .Where(change => change.Section == UiSettingsKeys.WorkspaceSection &&
                             (change.Key == UiSettingsKeys.WorkspaceFavoriteSymbols ||
                              change.Key == UiSettingsKeys.WorkspaceHiddenSymbols))
            .Subscribe(
                change => ApplyMarksChange(change),
                ex => _logger.LogError(ex, "The workspace symbol marks stream stopped unexpectedly"));

        // The rows re-decide on every selection change: the favorite state
        // and the combo shim follow the selection.
        this.WhenAnyValue(x => x.SelectedSymbol).Subscribe(
            _ => RebuildSymbolRows(),
            ex => _logger.LogError(ex, "The chart symbol rows stream stopped unexpectedly"));

        // The symbol list belongs to the exchange: the bootstrap re-runs on
        // every adapter change (the behavioral stream also performs the first
        // load right at construction) and keeps the previous selection alive
        // while the new exchange's list still carries it. The triggering
        // adapter names the exchange in the fall-through warnings.
        activeExchanges
            .AdapterChanges
            .Subscribe(
                adapter => BootstrapSymbols(adapter),
                ex => _logger.LogError(ex, "The chart symbols stream stopped unexpectedly"));
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Apply the persisted chart time frame before any consumer exists: the
    /// startup walk calls this AFTER the active-exchange facade restored its
    /// choice and BEFORE the main window binds, so the first bars load (and
    /// the combo) already carry the stored value — no H1 flash. A restore
    /// runs the same raise a combo flip drives (the load machine re-keys),
    /// but the persistence chain suppresses the assignment: a restore is a
    /// read, not an edit.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // The clock format restores FIRST and in EVERY path. The pre-fix
        // restore sat at the tail of the time-frame restore below, so the
        // early returns (no persisted time frame, an unparsable one, and —
        // the common case — a persisted time frame equal to the default
        // H1) skipped it and every restart silently reverted the user's
        // 12-hour clock (BUG-015). It is an independent pure consumer of
        // its setting; the time-frame restore does not stage it.
        Use12HourClock = await _settings.GetAsync<bool>(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartUse12HourClock,
            UiSettingsKeys.DefaultUse12HourClock);

        _logger.LogInformation(
            "Restored persisted chart clock format: '{Clock}'",
            Use12HourClock ? "12-hour" : "24-hour");

        var persisted = await _settings.GetAsync<string>(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartTimeFrame);

        if (string.IsNullOrWhiteSpace(persisted))
        {
            _logger.LogInformation(
                "No persisted chart time frame; starting on '{Value}'",
                SelectedTimeFrame);
            return;
        }

        // An unrecognized stored value keeps the default and logs — the
        // trading-safety rule: never substitute defaults silently.
        if (ParseStoredTimeFrame(persisted) is not { } restored)
        {
            _logger.LogWarning(
                "Persisted chart time frame '{PersistedValue}' is not a supported time frame; keeping '{Value}'",
                persisted,
                SelectedTimeFrame);
            return;
        }

        if (restored == SelectedTimeFrame)
        {
            return;
        }

        _isRestoringTimeFrame = true;
        try
        {
            SelectedTimeFrame = restored;
        }
        finally
        {
            _isRestoringTimeFrame = false;
        }

        _logger.LogInformation("Restored persisted chart time frame '{Value}'", restored);
    }

    /// <summary>
    /// The persisted-key parse, in its pure form (pinned by tests): the
    /// stored value must name a DEFINED time frame (case-insensitive,
    /// trimmed). Absent, blank, numeric or unrecognized values produce
    /// null — the caller keeps the default and logs.
    /// </summary>
    internal static TimeFrame? ParseStoredTimeFrame(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        var trimmed = stored.Trim();
        if (!Enum.TryParse(trimmed, ignoreCase: true, out TimeFrame parsed) ||
            !Enum.IsDefined(typeof(TimeFrame), parsed))
        {
            return null;
        }

        return parsed;
    }

    #endregion Public Methods

    #region Indicator Overlay

    /// <summary>
    /// Re-key the overlays to a settings value (the seed read and every
    /// changes-stream emission route here): the config carries by value and
    /// the current series recomputes against it right away.
    /// </summary>
    private void ApplyIndicatorConfig(string indicatorSettingsJson)
    {
        _persistedIndicatorConfig = indicatorSettingsJson;
        PublishIndicatorLines(Bars);
    }

    /// <summary>
    /// Compute the overlays for the given series and publish them. The
    /// compute is pure and marshal-free; the publication lands wherever the
    /// caller runs (delivered/prepend subscribers are already marshalled,
    /// the settings paths run synchronously) and the property assignment is
    /// the single UI-thread hop.
    /// </summary>
    private void PublishIndicatorLines(IReadOnlyList<Bar> bars)
    {
        _indicatorPublishes.OnNext(ChartIndicatorPipeline.Compute(
            bars,
            _persistedIndicatorConfig,
            _indicatorCatalog,
            _logger));
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// Fetch the symbol list through the shared
    /// <see cref="SymbolListProvider"/> (one <c>exchangeInfo</c> fetch per
    /// application run per exchange serves the chart combo and the order
    /// dialog autocomplete alike; the provider is warmed at startup and
    /// flushed on every exchange switch, so this bootstrap usually awaits a
    /// fetch or a ready cache). The FULL parsed list returns — the marks
    /// sort runs before the combo cap (a favorite deep in the exchange's
    /// order still pins to the head). Failures belong to the subscribe side
    /// (logged there, the combo stays empty).
    /// </summary>
    private async Task<IReadOnlyList<Symbol>> LoadSymbolsAsync(CancellationToken ct)
    {
        var names = await _symbolList.GetSymbolsAsync(ct);
        return names.Select(Symbol.Parse).ToList();
    }

    /// <summary>
    /// Load the symbol list for the current exchange and preselect. The
    /// persisted workspace preference is read first and the selection
    /// decision is computed raw (pinned by tests through
    /// <see cref="Preselect"/>); the marshaled subscribe side only applies
    /// the assignments under the bootstrap gate, a failure stays logged and
    /// leaves the combo empty.
    /// </summary>
    private void BootstrapSymbols(IMarketDataAdapter trigger)
    {
        var exchangeName = trigger.Name;

        // The marks resolve per exchange synchronously: a toggle that races
        // the fetch still persists into the right exchange's map.
        _marksExchangeId = trigger.Id;

        // BUG-016: claim this bootstrap's epoch and snapshot the selection
        // the fetch STARTS from — both sides of the delivery decision. A
        // newer emission that starts after this fetch bumps the epoch and
        // makes every pending delivery of this one stale.
        var epoch = Interlocked.Increment(ref _symbolBootstrapEpoch);
        var selectionAtStart = SelectedSymbol;

        Observable
            .FromAsync(token => LoadBootstrapAsync(trigger.Id, selectionAtStart, token))
            .ObserveOnUiThread()
            .Subscribe(
                bootstrap => ApplySymbolBootstrap(bootstrap, epoch, exchangeName),
                ex => _logger.LogError(ex, "Failed to load symbols"));
    }

    /// <summary>
    /// Apply one symbols bootstrap on the UI thread: the persisted warnings
    /// log, the marks re-cache (so the toggles serialize the other exchanges'
    /// entries untouched) and the exchange's list lands with the decided
    /// selection. The assignment runs under the bootstrap gate — the
    /// persistence chain skips exactly it, the two-way combo adds user edits
    /// everywhere else.
    /// </summary>
    private void ApplySymbolBootstrap(SymbolBootstrap bootstrap, int epoch, string exchangeName)
    {
        // BUG-016: only the CURRENT epoch delivers. A fetch outlived by a
        // newer switch carries a stale epoch — the newer bootstrap already
        // claimed its own — and drops: no older exchange's payload, marks
        // re-cache or preselection ever lands after the newer one.
        if (Volatile.Read(ref _symbolBootstrapEpoch) != epoch)
        {
            _logger.LogInformation(
                "Symbols for {Exchange} delivered late; a newer bootstrap started already and the delivery is dropped",
                exchangeName);
            return;
        }

        LogPersistedSymbolOutcome(bootstrap, exchangeName);

        _favoriteMarks = bootstrap.FavoriteMarks;
        _hiddenMarks = bootstrap.HiddenMarks;
        _isApplyingBootstrapSelection = true;
        try
        {
            _exchangeSymbols = bootstrap.Symbols;
            RebuildSymbolRows();
            SelectedSymbol = bootstrap.Selection;
        }
        finally
        {
            _isApplyingBootstrapSelection = false;
        }

        _logger.LogInformation(
            "Symbols loaded: {Count}; selected {Symbol}",
            bootstrap.Symbols.Count,
            bootstrap.Selection);
    }

    /// <summary>
    /// The marshal-invariant facts seam. The shared test-process dispatcher
    /// pins to a foreign owner (the posted apply is never pumped there), so
    /// a fact lands the load-and-apply through the same machinery the VM
    /// ships, synchronously and without the marshal hop:
    /// <list type="bullet">
    /// <item><see cref="StartBootstrapForFactsAsync"/> — the real start
    /// (epoch bump, selection snapshot, load, applying with its own
    /// epoch), for "a newer bootstrap wins" facts.</item>
    /// <item><see cref="ApplyBootstrapForFactsAsync(IMarketDataAdapter, int)"/>
    /// — a DELIVERY with a caller-declared epoch, including a stale one
    /// (the reversed-completions repro real switches produce).</item>
    /// <item><see cref="LoadBootstrapForFactsAsync"/> — the LOAD half
    /// alone (no epoch, no apply): the facts that need the raw decision —
    /// the persisted-deferral and preselection — while state stays
    /// untouched.</item>
    /// </list>
    /// Not part of the runtime UI contract — the ViewModel ships the
    /// assignment through
    /// <see cref="ZettaTrade.Appearance.Extensions.ObservableExtensions.ObserveOnUiThread"/>
    /// like every other assignment.
    /// </summary>
    internal async Task<int> StartBootstrapForFactsAsync(IMarketDataAdapter adapter)
    {
        var epoch = Interlocked.Increment(ref _symbolBootstrapEpoch);
        var selectionAtStart = SelectedSymbol;

        var bootstrap = await LoadBootstrapAsync(adapter.Id, selectionAtStart, CancellationToken.None);
        ApplySymbolBootstrap(bootstrap, epoch, adapter.Name);

        return epoch;
    }

    /// <summary>
    /// Deliver one bootstrap as the CALLER-declared epoch (the reversed
    /// completions repro: a fact fabricates the stale epoch a real switch
    /// produces — its bump already happened at the newer start). The load
    /// re-runs against the CURRENT exchange and the epoch guard decides.
    /// </summary>
    internal async Task ApplyBootstrapForFactsAsync(IMarketDataAdapter adapter, int epoch)
    {
        var bootstrap = await LoadBootstrapForFactsAsync(adapter);
        ApplySymbolBootstrap(bootstrap, epoch, adapter.Name);
    }

    /// <summary>
    /// The LOAD half of the facts seam: a real bootstrap load against the
    /// given adapter, nothing applied and no epoch consumed. The returned
    /// record carries the persisted-deferral and the preselection the
    /// delivery WOULD carry.
    /// </summary>
    internal async Task<SymbolBootstrap> LoadBootstrapForFactsAsync(IMarketDataAdapter adapter) =>
        await LoadBootstrapAsync(adapter.Id, SelectedSymbol, CancellationToken.None);

    /// <summary>
    /// The persisted-symbol warnings for one bootstrap outcome: a stored
    /// value that parses to nothing is an unsupported preference (logged,
    /// never silently substituted — the trading-safety rule), a parsed
    /// preference the exchange does not list falls through to the decided
    /// selection, and a listed but hidden preference yields the chart to
    /// the first visible pair (the settings tab's reveal is its way back).
    /// </summary>
    private void LogPersistedSymbolOutcome(
        SymbolBootstrap bootstrap,
        string exchangeName)
    {
        if (bootstrap.StoredSymbol is { } stored && bootstrap.PersistedSymbol is null)
        {
            _logger.LogWarning(
                "Persisted workspace symbol '{Stored}' is not a valid symbol; starting on '{Symbol}'",
                stored,
                bootstrap.Selection);
        }
        else if (bootstrap.PersistedDeferred)
        {
            // A deferred preference is not a fall-through: the user picked
            // while the fetch was in flight. Persist the fact quietly — the
            // not-listed warnings below would lie about the reason.
            _logger.LogInformation(
                "Persisted workspace symbol '{Persisted}' was deferred: a symbol was picked while the fetch was in flight",
                bootstrap.PersistedSymbol);
        }
        else if (bootstrap.PersistedSymbol is { } wanted && !Equals(wanted, bootstrap.Selection))
        {
            // The same mutual exclusivity rule the rows keep: a favorite
            // beats a stored hidden entry, so only unstarred names count as
            // hidden here.
            var exchangeFavorites =
                bootstrap.FavoriteMarks.GetValueOrDefault(bootstrap.ExchangeId) ?? new HashSet<string>(StringComparer.Ordinal);
            var hiddenNames =
                (bootstrap.HiddenMarks.GetValueOrDefault(bootstrap.ExchangeId) ?? new HashSet<string>(StringComparer.Ordinal))
                .Where(name => !exchangeFavorites.Contains(name))
                .ToHashSet(StringComparer.Ordinal);
            if (hiddenNames.Contains(wanted.Normalized))
            {
                _logger.LogWarning(
                    "Persisted workspace symbol '{Symbol}' is hidden on {Exchange}; starting on '{Selected}'",
                    wanted,
                    exchangeName,
                    bootstrap.Selection);
            }
            else
            {
                _logger.LogWarning(
                    "Persisted workspace symbol '{Symbol}' is not listed on {Exchange}; starting on '{Selected}'",
                    wanted,
                    exchangeName,
                    bootstrap.Selection);
            }
        }
    }

    /// <summary>
    /// Everything one symbols bootstrap decided: the full symbol list, the
    /// persisted marks maps (re-cached by the subscribe side so the toggles
    /// serialize the other exchanges' entries untouched), the exchange the
    /// marks apply to, the persisted preference and its raw stored form
    /// (non-null exactly when it failed to parse — the unsupported-fall
    /// -through warning source), whether the parsed preference was deferred
    /// by a mid-flight pick (BUG-016 — a deferral is not a fall-through, it
    /// must not warn) and the preselected symbol.
    /// </summary>
    internal sealed record SymbolBootstrap(
        IReadOnlyList<Symbol> Symbols,
        IReadOnlyDictionary<string, IReadOnlySet<string>> FavoriteMarks,
        IReadOnlyDictionary<string, IReadOnlySet<string>> HiddenMarks,
        string ExchangeId,
        string? StoredSymbol,
        Symbol? PersistedSymbol,
        bool PersistedDeferred,
        Symbol? Selection);

    /// <summary>
    /// One bootstrap load: the persisted workspace preference and the
    /// persisted marks maps first, then the symbol fetch (the cache-backed
    /// provider answers from memory after the startup warm-up), then the
    /// Preselect decision composed with the current selection. The rows the
    /// subscribe side lists are rebuilt on the spot (the single rows writer
    /// — the in-place updater — see <see cref="RebuildSymbolRows"/>).
    /// <paramref name="selectionAtStart"/> is the selection the fetch
    /// started from: a pick made WHILE the fetch was in flight supersedes
    /// the read-before-fetch persisted preference (BUG-016 — the stale
    /// snapshot must never overwrite an explicit user decision; the
    /// deferred preference stays stored and wins on the next unpicked
    /// switch).
    /// </summary>
    private async Task<SymbolBootstrap> LoadBootstrapAsync(
        string exchangeId,
        Symbol? selectionAtStart,
        CancellationToken ct)
    {
        var stored = await _settings.GetAsync<string>(
            UiSettingsKeys.WorkspaceSection,
            UiSettingsKeys.WorkspaceSymbolId);

        var favoritesJson = await _settings.GetAsync<string>(
            UiSettingsKeys.WorkspaceSection,
            UiSettingsKeys.WorkspaceFavoriteSymbols);

        var hiddenJson = await _settings.GetAsync<string>(
            UiSettingsKeys.WorkspaceSection,
            UiSettingsKeys.WorkspaceHiddenSymbols);

        var favoriteMarks = SymbolMarkMap.Parse(favoritesJson);
        var hiddenMarks = SymbolMarkMap.Parse(hiddenJson);

        var persistedTrimmed = stored?.Trim();
        var persisted = TryParseStoredSymbol(persistedTrimmed);

        var symbols = await LoadSymbolsAsync(ct);

        // BUG-016: the user picked while the fetch was in flight — the
        // read-before-fetch preference is deferred, it must not weigh into
        // the delivery (an explicit user decision beats a stale snapshot).
        var pickedDuringFetch = !Equals(SelectedSymbol, selectionAtStart);

        // The marks are mutually exclusive by rule: a favorite beats a stored
        // hidden entry (the legacy-collision decision), so only the unstarred
        // names weigh into the preselection decision.
        var exchangeFavorites =
            favoriteMarks.GetValueOrDefault(exchangeId) ?? new HashSet<string>(StringComparer.Ordinal);
        var hiddenNames =
            (hiddenMarks.GetValueOrDefault(exchangeId) ?? new HashSet<string>(StringComparer.Ordinal))
            .Where(name => !exchangeFavorites.Contains(name))
            .ToHashSet(StringComparer.Ordinal);
        var selection = Preselect(
            SelectedSymbol,
            pickedDuringFetch ? null : persisted,
            symbols,
            hiddenNames);

        if (pickedDuringFetch && persisted is not null)
        {
            _logger.LogInformation(
                "A symbol was picked while the {Exchange} symbols fetch was in flight; the persisted {Persisted} is deferred and stays stored for the next switch",
                exchangeId,
                persisted);
        }

        return new SymbolBootstrap(
            Symbols: symbols,
            FavoriteMarks: favoriteMarks,
            HiddenMarks: hiddenMarks,
            ExchangeId: exchangeId,
            StoredSymbol: persisted is null ? persistedTrimmed : null,
            PersistedSymbol: persisted,
            PersistedDeferred: pickedDuringFetch && persisted is not null,
            Selection: selection);
    }

    /// <summary>
    /// The preselection decision, in its pure form (pinned directly by
    /// tests), priority in the order the board fixed: the persisted workspace
    /// preference wins while the list carries it and it is not hidden, the
    /// current selection survives while the new list carries it and it is
    /// not hidden, the first visible entry owns the workspace default, an
    /// empty (or fully hidden) list clears the selection with no substitute.
    /// A satisfied choice is delivered as the LIST's own instance, so the
    /// combo's SelectedItem always points at an item of its own source.
    /// Hidden pairs never take the chart: the settings tab's reveals are
    /// their only way back.
    /// </summary>
    internal static Symbol? Preselect(
        Symbol? currentSelection,
        Symbol? persistedSymbol,
        IReadOnlyList<Symbol> symbols,
        IReadOnlySet<string>? hiddenNames = null)
    {
        hiddenNames ??= new HashSet<string>(StringComparer.Ordinal);

        if (persistedSymbol is { } preferred && !hiddenNames.Contains(preferred.Normalized))
        {
            var listed = symbols.FirstOrDefault(symbol => symbol == preferred);
            if (listed is not null)
            {
                return listed;
            }
        }

        if (currentSelection is { } kept && !hiddenNames.Contains(kept.Normalized))
        {
            var stillListed = symbols.FirstOrDefault(symbol => symbol == kept);
            if (stillListed is not null)
            {
                return stillListed;
            }
        }

        return symbols.FirstOrDefault(symbol => !hiddenNames.Contains(symbol.Normalized));
    }

    /// <summary>
    /// The rows a rebuild or a bootstrap lists, in its pure form (pinned by
    /// tests): favorites first (the exchange's own order inside the group),
    /// then the unmarked symbols, the hidden pairs filtered out. The marks
    /// are mutually exclusive by rule: a found favorite beats a stored
    /// hidden entry (legacy collision — the star wins, the settings tab's
    /// moves repair the document on the next save). A symbol that vanished
    /// from the list leaves no ghost; the capped page rides the rebuild.
    /// </summary>
    /// <param name="symbols">The exchange's full symbol list (exchange
    /// order).</param>
    /// <param name="favoriteNames">The exchange's favorite names
    /// (normalized).</param>
    /// <param name="hiddenNames">The exchange's hidden names
    /// (normalized).</param>
    /// <param name="maxRows">The combo capacity cap.</param>
    /// <returns>The rows in combo order.</returns>
    internal static IReadOnlyList<SymbolRow> BuildSymbolRows(
        IReadOnlyList<Symbol> symbols,
        IReadOnlySet<string> favoriteNames,
        IReadOnlySet<string> hiddenNames,
        int maxRows)
    {
        List<SymbolRow> favorites = [];
        List<SymbolRow> others = [];
        foreach (var symbol in symbols)
        {
            var isFavorite = favoriteNames.Contains(symbol.Normalized);
            if (!isFavorite && hiddenNames.Contains(symbol.Normalized))
            {
                continue;
            }

            (isFavorite ? favorites : others).Add(new SymbolRow(symbol, isFavorite));
        }

        favorites.AddRange(others);
        return favorites.Take(maxRows).ToList();
    }

    /// <summary>
    /// Rebuild the combo from the cached exchange list and marks. A rebuild
    /// never moves the selection on its own — except when the current
    /// selection is not among the visible rows (the settings save that hid
    /// the shown pair, or a vanished symbol): the chart switches to the
    /// first visible pair and the selection stream persists the switch, so
    /// a restart never re-opens a hidden chart either.
    /// </summary>
    private void RebuildSymbolRows()
    {
        _isUpdatingSymbolRows = true;
        try
        {
            IReadOnlySet<string> favorites =
                _favoriteMarks.GetValueOrDefault(_marksExchangeId) ?? new HashSet<string>(StringComparer.Ordinal);

            // The marks are mutually exclusive by rule: a favorite beats a
            // stored hidden entry (the legacy-collision decision), so only
            // the unstarred names count as hidden here.
            IReadOnlySet<string> hidden =
                (_hiddenMarks.GetValueOrDefault(_marksExchangeId) ?? new HashSet<string>(StringComparer.Ordinal))
                .Where(name => !favorites.Contains(name))
                .ToHashSet(StringComparer.Ordinal);
            IReadOnlyList<SymbolRow> rows = _exchangeSymbols.Count == 0
                ? []
                : BuildSymbolRows(
                    _exchangeSymbols,
                    favorites,
                    hidden,
                    MaxVisibleSymbols);
            UpdateRowsInPlace(rows);

            var selectedName = SelectedSymbol?.Normalized;
            if (selectedName is not null &&
                _exchangeSymbols.Count > 0 &&
                rows.All(row => row.Symbol.Normalized != selectedName))
            {
                // The shown pair is hidden (or vanished): the first visible
                // row takes the chart. The setter's stream persists the
                // switch; the assignments below keep the shim exact on the
                // rare no-change edge (the rows already updated). The list
                // -not-yet-loaded state keeps the user's last choice (there
                // is nothing to rebase to; the bootstrap decides next).
                var rebased = rows.FirstOrDefault();
                SelectedSymbol = rebased?.Symbol;
                SelectedSymbolRow = rebased;
                return;
            }

            // The combo shim follows the canonical selection: the row matching
            // it (the rebuilt rows' own instance). Records compare by value,
            // so an unchanged rebuild keeps the shim silent and the combo's
            // selection exactly as the interaction committed it.
            SelectedSymbolRow = selectedName is null
                ? null
                : rows.FirstOrDefault(row => row.Symbol.Normalized == selectedName);
        }
        finally
        {
            _isUpdatingSymbolRows = false;
        }
    }

    /// <summary>
    /// Apply one settings change of either marks key (the same pure-consumer
    /// contract the clock format keeps): the new document parses, lands in
    /// the per-exchange cache and the rows rebuild. Unusable values parse to
    /// an empty map (the fail-safe) — the rows just unmark.
    /// </summary>
    private void ApplyMarksChange(SettingsChange change)
    {
        if (change.NewValue is not string json)
        {
            _logger.LogWarning(
                "The '{Key}' setting change carried a non-string payload; the marks keep their applied state",
                change.Key);
            return;
        }

        var parsed = SymbolMarkMap.Parse(json);
        if (change.Key == UiSettingsKeys.WorkspaceFavoriteSymbols)
        {
            _favoriteMarks = parsed;
        }
        else
        {
            _hiddenMarks = parsed;
        }

        RebuildSymbolRows();
        _logger.LogInformation("The workspace '{Key}' map was re-applied", change.Key);
    }

    /// <summary>
    /// The persisted-symbol parse, in its pure form (pinned by tests): the
    /// stored value must be a non-blank normalized symbol string; a valid
    /// parse yields the Symbol (upper-cased, normalized), a blank or
    /// malformed one yields null — the caller logs the fall-through instead
    /// of substituting silently. This never throws.
    /// </summary>
    internal static Symbol? TryParseStoredSymbol(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        try
        {
            return Symbol.Parse(stored.Trim());
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// History window start for one time frame: the nominal bar count walked
    /// back at the frame's own duration, so EVERY frame asks for the same bar
    /// depth (the exchange-standard ~200 bars: W1 walks back 200 weeks, M1 a
    /// fresh 200 minutes) instead of one fixed calendar window that starved
    /// the coarse frames (one weekly bar in 7 days) and dated the fine ones.
    /// Monthly and weekly frames use nominal calendar lengths; the exchange
    /// keeps whatever the listing has delivered since — a frame younger than
    /// the requested depth never fills the window (the listing fact).
    /// </summary>
    internal static DateTimeOffset HistoryStart(
        DateTimeOffset now,
        TimeFrame timeFrame,
        int barCount) =>
        timeFrame switch
        {
            TimeFrame.MN1 => now.AddDays(-barCount * NominalMonthDays),
            TimeFrame.W1 => now.AddDays(-barCount * NominalWeekDays),
            TimeFrame.D1 => now.AddDays(-barCount),
            TimeFrame.H4 => now.AddHours(-barCount * 4),
            TimeFrame.H1 => now.AddHours(-barCount),
            TimeFrame.M30 => now.AddMinutes(-barCount * 30),
            TimeFrame.M15 => now.AddMinutes(-barCount * 15),
            TimeFrame.M5 => now.AddMinutes(-barCount * 5),
            TimeFrame.M1 => now.AddMinutes(-barCount),
            _ => throw new ArgumentOutOfRangeException(
                nameof(timeFrame),
                timeFrame,
                "Unknown time frame has no bar duration.")
        };

    /// <summary>
    /// Bars load for one selection on the exchange that triggered it, for
    /// the exact time frame of the triggering change. A failure ends this
    /// selection's data (logged, never a default value) but must not
    /// terminate the switching machine for later selections.
    /// </summary>
    private IObservable<IReadOnlyList<Bar>> LoadBars(
        Symbol symbol,
        TimeFrame timeFrame,
        IMarketDataAdapter adapter)
    {
        _logger.LogInformation(
            "Chart bars load requested for {Symbol} {TimeFrame} on {Exchange}",
            symbol,
            timeFrame,
            adapter.Name);

        // From = the frame's own bar depth walked back from now: every time
        // frame loads its full history depth. The limit is one over the bar
        // count because the request window [start, now] contains count+1
        // open times when start lands on a bar boundary (the current,
        // still-forming candle included), and the exchange answers a
        // saturated window with its OLDEST bars — a saturated limit would
        // cut exactly the fresh edge.
        return Observable
            .FromAsync(token => adapter.GetBarsAsync(
                symbol,
                timeFrame,
                HistoryStart(DateTimeOffset.UtcNow, timeFrame, InitialBarCount),
                limit: InitialBarCount + 1,
                ct: token))
            // A cancelled load is the normal outcome of Switch dropping the
            // previous selection or exchange: keep it silent (real failures
            // log below).
            .Catch<IReadOnlyList<Bar>, OperationCanceledException>(_ =>
                Observable.Empty<IReadOnlyList<Bar>>())
            .Catch<IReadOnlyList<Bar>, Exception>(ex =>
            {
                _logger.LogError(ex, "Failed to load chart data for {Symbol}", symbol);
                return Observable.Empty<IReadOnlyList<Bar>>();
            });
    }

    #endregion

    #region Older History Paging

    /// <summary>
    /// Fetch and prepend the PREVIOUS window for the loaded series (the
    /// chart pan crossed the left edge of the loaded history — see the
    /// chart's fetch threshold). The request rides the LAST delivered load's
    /// triple exactly: an anchor before any delivery, a duplicate racing
    /// release, or an exhausted history drops quietly, and a page racing a
    /// rebase is dropped with the view untouched — the rebase (symbol, time
    /// frame or exchange change) owns the series.
    /// </summary>
    /// <param name="ct">The caller's cancellation: an abandoned request
    /// (shutdown) stops its fetch without throwing.</param>
    public async Task RequestOlderBarsAsync(CancellationToken ct = default)
    {
        if (_olderFetchInFlight)
        {
            return;
        }

        if (_olderHistoryExhausted || _olderAppendsPending > 0)
        {
            return;
        }

        if (Bars.Count == 0 || _loadedTarget is not { } target || _loadedAdapter is null)
        {
            return;
        }

        _olderFetchInFlight = true;
        _olderFetchEpoch = new CancellationTokenSource();
        try
        {
            // The anchor and the series snapshot are the same object: a
            // rebase replaces the collection between them, and the fetch's
            // result dedupes against exactly the series it was captured on.
            var series = Bars;
            var anchor = series[0].Time;

            var page = await _loadedAdapter.GetBarsAsync(
                target.Symbol,
                target.TimeFrame,
                HistoryStart(anchor, target.TimeFrame, InitialBarCount),
                to: anchor,
                limit: InitialBarCount + 1,
                ct: CancellationTokenSource.CreateLinkedTokenSource(ct, _olderFetchEpoch.Token).Token);

            // The rebase check: a delivered full load replaced the collection
            // (and cancelled this epoch) while the page was in flight — the
            // page answers a series that no longer exists.
            if (!ReferenceEquals(Bars, series))
            {
                _logger.LogInformation(
                    "Chart older-bars page dropped: the series was rebased during the fetch ({Symbol} {TimeFrame} on {Exchange})",
                    target.Symbol.Normalized,
                    target.TimeFrame,
                    target.ExchangeId);
                return;
            }

            var prepend = BarSeriesMerge.PrependOlder(series, page);
            if (prepend is null)
            {
                _logger.LogError(
                    "Chart older-bars page rejected: bars are not strictly ascending ({Symbol} {TimeFrame} on {Exchange})",
                    target.Symbol.Normalized,
                    target.TimeFrame,
                    target.ExchangeId);
                return;
            }

            if (prepend.Count == 0)
            {
                // Nothing older arrived at this anchor: the loaded series
                // already reaches the adapter's first candle for this triple.
                // Deeper panning stays on the loaded bars until a rebase.
                _olderHistoryExhausted = true;
                _logger.LogInformation(
                    "Chart older history exhausted for {Symbol} {TimeFrame} on {Exchange}",
                    target.Symbol.Normalized,
                    target.TimeFrame,
                    target.ExchangeId);
                return;
            }

            _olderAppendsPending++;
            _olderPages.OnNext(prepend);
        }
        catch (OperationCanceledException)
        {
            // A cancelled request is normal: the rebase dropped the page or
            // the caller (shutdown) abandoned it — the view stays as it was.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load older chart data for {Symbol}", _loadedTarget?.Symbol.Normalized);
        }
        finally
        {
            _olderFetchEpoch?.Dispose();
            _olderFetchEpoch = null;
            _olderFetchInFlight = false;
        }
    }

    #endregion
}
