namespace ZettaTrade.App.ViewModels;

using System.Collections.ObjectModel;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.UI.Extensions;

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

    private ObservableCollection<Symbol> _availableSymbols = new();

    /// <summary>List of available trading symbols.</summary>
    public ObservableCollection<Symbol> AvailableSymbols
    {
        get => _availableSymbols;
        private set => this.RaiseAndSetIfChanged(ref _availableSymbols, value);
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

    #region Constructors

    /// <summary>
    /// Create a new ChartViewModel instance.
    /// </summary>
    /// <param name="settings">The app settings service; the time frame
    /// restore reads <see cref="UiSettingsKeys.ChartTimeFrame"/> and every
    /// user change persists there, the clock formats read and live-apply
    /// <see cref="UiSettingsKeys.ChartUse12HourClock"/>, and the workspace
    /// symbol reads and persists
    /// <see cref="UiSettingsKeys.WorkspaceSymbolId"/>.</param>
    public ChartViewModel(
        ActiveExchangeService activeExchanges,
        SymbolListProvider symbolList,
        ISettingsService settings,
        ILogger<ChartViewModel> logger)
    {
        _activeExchanges = activeExchanges;
        _symbolList = symbolList;
        _settings = settings;
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

        // The clock format is a pure consumer of the setting (nothing in the
        // chart pipeline writes it), so the restore is a plain assignment.
        Use12HourClock = await _settings.GetAsync<bool>(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartUse12HourClock,
            UiSettingsKeys.DefaultUse12HourClock);

        _logger.LogInformation(
            "Restored persisted chart clock format: '{Clock}'",
            Use12HourClock ? "12-hour" : "24-hour");
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

    #region Private Methods

    /// <summary>
    /// Fetch the symbol list through the shared
    /// <see cref="SymbolListProvider"/> (one <c>exchangeInfo</c> fetch per
    /// application run per exchange serves the chart combo and the order
    /// dialog autocomplete alike; the provider is warmed at startup and
    /// flushed on every exchange switch, so this bootstrap usually awaits a
    /// fetch or a ready cache), trimmed to the combo capacity. Failures
    /// belong to the subscribe side (logged there, the combo stays empty).
    /// </summary>
    private async Task<IReadOnlyList<Symbol>> LoadSymbolsAsync(CancellationToken ct)
    {
        var names = await _symbolList.GetSymbolsAsync(ct);
        return names.Select(Symbol.Parse).Take(MaxVisibleSymbols).ToList();
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
        Observable
            .FromAsync(token => LoadBootstrapAsync(token))
            .ObserveOnUiThread()
            .Subscribe(
                bootstrap =>
                {
                    LogPersistedSymbolOutcome(bootstrap, exchangeName);

                    // A bootstrap either re-applies the persisted preference
                    // it just read (no duplicate write — nothing changed) or
                    // applies the logged fall-through (writing the substitute
                    // would destroy the deferred preference). The gate makes
                    // the persistence chain skip exactly this assignment; the
                    // two-way combo adds user edits everywhere else.
                    _isApplyingBootstrapSelection = true;
                    try
                    {
                        AvailableSymbols = new ObservableCollection<Symbol>(bootstrap.Symbols);
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
                },
                ex => _logger.LogError(ex, "Failed to load symbols"));
    }

    /// <summary>
    /// The persisted-symbol warnings for one bootstrap outcome: a stored
    /// value that parses to nothing is an unsupported preference (logged,
    /// never silently substituted — the trading-safety rule), and a parsed
    /// preference the exchange does not list falls through to the decided
    /// selection.
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
        else if (bootstrap.PersistedSymbol is { } wanted && !Equals(wanted, bootstrap.Selection))
        {
            _logger.LogWarning(
                "Persisted workspace symbol '{Symbol}' is not listed on {Exchange}; starting on '{Selected}'",
                wanted,
                exchangeName,
                bootstrap.Selection);
        }
    }

    /// <summary>
    /// Everything one symbols bootstrap decided: the symbol page to list, the
    /// persisted preference and its raw stored form (non-null exactly when it
    /// failed to parse — the unsupported-fall-through warning source), and
    /// the preselected symbol.
    /// </summary>
    private sealed record SymbolBootstrap(
        IReadOnlyList<Symbol> Symbols,
        string? StoredSymbol,
        Symbol? PersistedSymbol,
        Symbol? Selection);

    /// <summary>
    /// One bootstrap load: the persisted workspace preference first, then the
    /// symbol fetch (the cache-backed provider answers from memory after the
    /// startup warm-up), then the Preselect decision composed with the
    /// current selection.
    /// </summary>
    private async Task<SymbolBootstrap> LoadBootstrapAsync(CancellationToken ct)
    {
        var stored = await _settings.GetAsync<string>(
            UiSettingsKeys.WorkspaceSection,
            UiSettingsKeys.WorkspaceSymbolId);

        var persistedTrimmed = stored?.Trim();
        var persisted = TryParseStoredSymbol(persistedTrimmed);
        var symbols = await LoadSymbolsAsync(ct);

        return new SymbolBootstrap(
            Symbols: symbols,
            StoredSymbol: persisted is null ? persistedTrimmed : null,
            PersistedSymbol: persisted,
            Selection: Preselect(SelectedSymbol, persisted, symbols));
    }

    /// <summary>
    /// The preselection decision, in its pure form (pinned directly by
    /// tests), priority in the order the board fixed: the persisted workspace
    /// preference wins while the list carries it, the current selection
    /// survives while the new list carries it, the first entry owns the
    /// workspace default, an empty list clears the selection with no
    /// substitute. A satisfied choice is delivered as the LIST's own
    /// instance, so the combo's SelectedItem always points at an item of its
    /// own source.
    /// </summary>
    internal static Symbol? Preselect(
        Symbol? currentSelection,
        Symbol? persistedSymbol,
        IReadOnlyList<Symbol> symbols)
    {
        if (persistedSymbol is { } preferred)
        {
            var listed = symbols.FirstOrDefault(symbol => symbol == preferred);
            if (listed is not null)
            {
                return listed;
            }
        }

        if (currentSelection is { } kept)
        {
            var stillListed = symbols.FirstOrDefault(symbol => symbol == kept);
            if (stillListed is not null)
            {
                return stillListed;
            }
        }

        return symbols.FirstOrDefault();
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
