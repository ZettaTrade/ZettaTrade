namespace ZettaTrade.App.ViewModels;

using System.Collections.ObjectModel;
using System.Reactive.Linq;
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

    private ObservableCollection<Bar> _bars = new();

    /// <summary>Collection of bars to display in the chart.</summary>
    public ObservableCollection<Bar> Bars
    {
        get => _bars;
        private set => this.RaiseAndSetIfChanged(ref _bars, value);
    }

    private Symbol? _selectedSymbol;

    /// <summary>Currently selected trading symbol.</summary>
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
    /// Bars loaded for the latest (symbol, time frame, exchange) triple; a
    /// newer triple cancels the previous load (Rx Switch). The machine is
    /// hot and shared: exactly one load pipeline runs no matter how many
    /// consumers subscribe, and a late watcher immediately gets the latest
    /// bars. Raw — not UI-marshaled; user-interface consumers marshal
    /// themselves.
    /// </summary>
    public IObservable<IReadOnlyList<Bar>> BarsUpdates { get; }

    #endregion

    #region Constructors

    /// <summary>
    /// Create a new ChartViewModel instance.
    /// </summary>
    /// <param name="settings">The app settings service; the time frame
    /// restore reads <see cref="UiSettingsKeys.ChartTimeFrame"/> and every
    /// user change persists there.</param>
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

        // Bars re-load on every symbol, time frame or active-exchange change:
        // the triple pins the exact load target (exchange + timeframe), so
        // each load serves precisely its triggering change, and the Switch
        // drops the in-flight load of a superseded triple (atomic by
        // construction) — a rapid time frame flip leaves exactly one load.
        BarsUpdates = SymbolSelections
            .CombineLatest(
                this.WhenAnyValue(x => x.SelectedTimeFrame),
                activeExchanges.AdapterChanges,
                (symbol, timeFrame, adapter) => (Symbol: symbol, TimeFrame: timeFrame, Adapter: adapter))
            .Select(triple => LoadBars(triple.Symbol, triple.TimeFrame, triple.Adapter))
            .Switch()
            .Replay(1)
            .RefCount();

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

        // The ViewModel lives as long as the application (singleton), so this
        // subscription mirrors it and needs no explicit disposal. The marshal
        // chain keeps <see cref="Bars"/> on the Avalonia UI thread.
        BarsUpdates.ObserveOnUiThread()
            .Subscribe(
                bars =>
                {
                    Bars = new ObservableCollection<Bar>(bars);
                    _logger.LogInformation("Chart bars delivered: {Count} candles", bars.Count);
                },
                ex => _logger.LogError(ex, "The chart bars stream stopped unexpectedly"));

        // The symbol list belongs to the exchange: the bootstrap re-runs on
        // every adapter change (the behavioral stream also performs the first
        // load right at construction) and keeps the previous selection alive
        // while the new exchange's list still carries it.
        activeExchanges
            .AdapterChanges
            .Subscribe(
                _ => BootstrapSymbols(),
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
    /// selection decision is computed raw (pinned by tests through
    /// <see cref="Preselect"/>); the marshaled subscribe side only applies
    /// the assignments, and a failure stays logged, leaving the combo empty.
    /// </summary>
    private void BootstrapSymbols()
    {
        Observable.FromAsync(LoadSymbolsAsync)
            .Select(symbols => (
                Symbols: symbols,
                Selection: Preselect(SelectedSymbol, symbols)))
            .ObserveOnUiThread()
            .Subscribe(
                page =>
                {
                    AvailableSymbols = new ObservableCollection<Symbol>(page.Symbols);
                    SelectedSymbol = page.Selection;
                    _logger.LogInformation(
                        "Symbols loaded: {Count}; selected {Symbol}",
                        AvailableSymbols.Count,
                        SelectedSymbol);
                },
                ex => _logger.LogError(ex, "Failed to load symbols"));
    }

    /// <summary>
    /// The preselection decision, in its pure form (pinned directly by
    /// tests): the current selection survives while the new list carries
    /// it, otherwise the first entry owns the workspace default; an empty
    /// list clears the selection with no substitute.
    /// </summary>
    internal static Symbol? Preselect(Symbol? currentSelection, IReadOnlyList<Symbol> symbols) =>
        currentSelection is { } kept && symbols.Contains(kept)
            ? kept
            : symbols.FirstOrDefault();

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
}
