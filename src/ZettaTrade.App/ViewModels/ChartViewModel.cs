namespace ZettaTrade.App.ViewModels;

using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Livery.Contracts;
using Livery.Extensions;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.App.Services;
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Exchange;
using ZettaTrade.Infrastructure.Export;
using ZettaTrade.Infrastructure.Persistence;
using ZettaTrade.Plugins.Abstractions;
using ZettaTrade.Strategies;
using ZettaTrade.UI.Charting;
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// Chart panel state: the available symbols list, the selected symbol and the
/// loaded bars. Application-lifetime singleton: the pipelines are built in the
/// constructor - WhenActivated never fires for VMs bound as nested-panel
/// DataContexts (the documented workspace fact), so there is no activation path.
/// <para>
/// The multi-chart grid (T-201) constructs secondary instances for its
/// non-primary slots: they carry the same pipelines with their own
/// symbol/time-frame persistence keys (<see cref="UiSettingsKeys"/> - the
/// primary keeps the workspace's shared keys, a slot writes
/// <c>ChartSlot{N}*</c>), and they persist only those two choices - the
/// chart-face maps (types, scales, pane heights, the overlay toggles) stay
/// the primary's writes, so the slots' face picks live in-session.
/// </para>
/// <para>
/// The loaded series exports as a candles CSV: the export command rides
/// <see cref="ICsvExportService"/>, the destination comes through the
/// save-file interaction the main window answers, and a write fault
/// surfaces on the panel's own error line.
/// </para>
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
/// The chart rendering type (T-089) persists per workspace symbol in the
/// <c>UI.ChartSymbolTypes</c> JSON map
/// (see <see cref="Services.SymbolChartTypeMap"/>): one async read seeds the
/// map at construction and the changes stream keeps it current, every
/// selection change resolves the new symbol's stored choice (a symbol
/// without an entry keeps the standing type — a preference map substitutes
/// nothing), every selector change serializes the whole updated map under
/// the current symbol, and the Heikin-Ashi series publishes through the
/// same raw-then-marshal discipline the overlays keep.
/// The delivered series also runs the deep-history repair (T-086): every
/// delivered page (the cached one and the fetched one behind it) kicks a
/// bounded pass chain that probes the cache behind the window's head,
/// detects the calendar holes (<see cref="Services.CandleGapDetector"/>) and
/// fills the fillable ones through
/// <see cref="Services.CandleBackfillService"/> — the additions are
/// FETCHED bars only (contiguous cached bars never deepen the boot view),
/// the merge is strictly additive on the UI thread, oversized truncated
/// holes are left to the paging machinery, and a feed floor discovered on
/// the way surfaces as the localized <c>Chart.HistoryLimit</c> note (the
/// paging machinery composes the same note when its own anchor proves the
/// end of history).
/// The ViewModel lives as long as the application (singleton), so the
/// subscriptions mirror it and need no explicit disposal.
/// <para>
/// The strategy-signals surface (T-112): every full load publishes its
/// identity through the series relay (the strategy host's feed address)
/// and the markers overlay (<see cref="SignalMarkers"/>) re-inks whenever
/// a delivery, prepend, repair or a new signal lands — one marker per
/// displayed candle whose stance the host registered, anchored at the
/// candle's own close. The rail panel rides
/// <see cref="SignalRail"/>: its coalescing and click routes are the rail
/// ViewModel's own.
/// </para>
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

    /// <summary>The file name the save dialog pre-fills for the candles export.</summary>
    private const string CandlesCsvFileName = "candles.csv";

    /// <summary>The file name the save dialog pre-fills for the visible-range candles export (T-094).</summary>
    private const string VisibleCandlesCsvFileName = "candles-visible.csv";

    /// <summary>The resx key of the candles export fault line.</summary>
    private const string ExportFailedKey = "Chart.ExportFailed";

    /// <summary>The resx key of the deep-history end note (T-086).</summary>
    private const string HistoryLimitKey = "Chart.HistoryLimit";

    /// <summary>
    /// Extra open slots one compare fetch requests over the standing
    /// series' own span (T-124): the feed may pad the window around
    /// <c>from</c>, and the layer's aligned head should not clip the
    /// window because the request carried exactly the visible count.
    /// </summary>
    private const int CompareFetchBarPad = 2;

    /// <summary>The resx keys of the symbol-compare refusals (T-124): each reason stands alone on the error line.</summary>
    private const string CompareNoSeriesKey = "Chart.CompareNoSeries";
    private const string CompareSelfKey = "Chart.CompareSelf";
    private const string CompareDuplicateKey = "Chart.CompareDuplicate";
    private const string CompareCapKey = "Chart.CompareCap";
    private const string CompareFetchEmptyKey = "Chart.CompareFetchEmpty";
    private const string CompareFetchFailedKey = "Chart.CompareFetchFailed";

    /// <summary>How many cached bars older than the series' head one repair
    /// probe reads (the same clamp the batched cache reads keep).</summary>
    private const int RepairOlderProbeBars = 1_000;

    /// <summary>How many repair passes one delivered page may chain: bounded
    /// work per delivery — the rest belongs to the paging machinery.</summary>
    private const int MaxRepairPasses = 3;

    /// <summary>The UTC format the history-limit note prints its boundary in.</summary>
    private const string UtcTimestampFormat = "yyyy-MM-dd HH:mm 'UTC'";

    #endregion

    #region Properties

    private readonly ISettingsService _settings;
    private readonly ILogger<ChartViewModel> _logger;
    private readonly ActiveExchangeService _activeExchanges;
    private readonly SymbolListProvider _symbolList;
    private readonly ICsvExportService _export;
    private readonly ILocalizationManager _localization;

    /// <summary>
    /// The slot identity (T-201): null on the application-lifetime primary,
    /// otherwise the grid slot's number ("1".."3"). It derives this
    /// instance's persistence keys and gates the chart-face writes.
    /// </summary>
    private readonly string? _slotId;

    /// <summary>
    /// Whether the chart-face choices (the per-symbol type/scale/pane maps
    /// and the overlay toggles) persist from this instance. The primary
    /// writes them; a slot's picks stay in-session so interleaved
    /// whole-map writes cannot lose each other's entries (T-201).
    /// </summary>
    private readonly bool _persistsFaceChoices;

    /// <summary>
    /// The named chart templates' manager (T-202): the primary hosts it
    /// (the face captures, the error reports and the default seeding run
    /// through it); a slot passes none and keeps every face pick
    /// in-session — a slot never writes the shared template state.
    /// </summary>
    private readonly ChartTemplatesViewModel? _templates;

    /// <summary>
    /// The settings key this instance persists its selected symbol under:
    /// the workspace's shared key on the primary (the order book and the
    /// order dialog follow the same stream), a per-slot key on the
    /// multi-chart grid's slots (T-201).
    /// </summary>
    private string SymbolSettingsKey =>
        _slotId is null
            ? UiSettingsKeys.WorkspaceSymbolId
            : $"{UiSettingsKeys.ChartSlotPrefix}{_slotId}SymbolId";

    /// <summary>
    /// The settings key this instance persists its selected time frame
    /// under: the shared key on the primary, a per-slot key on the grid's
    /// slots (T-201).
    /// </summary>
    private string TimeFrameSettingsKey =>
        _slotId is null
            ? UiSettingsKeys.ChartTimeFrame
            : $"{UiSettingsKeys.ChartSlotPrefix}{_slotId}TimeFrame";

    /// <summary>
    /// The settings section the symbol key lives in: the Workspace section
    /// on the primary (the workspace symbol context), the UI section on the
    /// grid's slots (T-201).
    /// </summary>
    private string SymbolSettingsSection =>
        _slotId is null
            ? UiSettingsKeys.WorkspaceSection
            : UiSettingsKeys.Section;

    /// <summary>
    /// The local candle cache the bootstrap serves from and the loaded
    /// pages ride WRITE-THROUGH (T-085): every adapter-served bar set
    /// persists so the next start renders instantly from the cache.
    /// </summary>
    /// <summary>The local candle cache (T-085): the bootstrap page serves it first.</summary>
    private readonly ICandleCache _candleCache;

    /// <summary>The persisted chart drawings store (T-091): the standing page's annotations load and save whole.</summary>
    private readonly IChartDrawingStore _drawingStore;

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
    /// overwrite the preference it merely deferred). The prewarm's own
    /// apply (T-226) rides the same gate.
    /// </summary>
    private bool _isApplyingBootstrapSelection;

    /// <summary>
    /// The startup restore's completion (the walk's
    /// <see cref="InitializeAsync"/>): the prewarm's selection apply holds
    /// behind it, so the persisted symbol never loads against the
    /// constructor-time default time frame — the restore's own raise would
    /// immediately supersede that load with a wasted fetch pair. Facts that
    /// never call <see cref="InitializeAsync"/> ride the completed default
    /// and keep the prewarm's immediate shape.
    /// </summary>
    private Task _startupRestore = Task.CompletedTask;

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

    private bool _showVwap = UiSettingsKeys.DefaultChartShowVwap;

    /// <summary>
    /// Whether the chart draws the session-anchored VWAP overlay with its
    /// ±1σ bands (T-093). The settings service is the single source of
    /// truth: the startup walk seeds the persisted value, external changes
    /// re-apply live and the toolbar toggle persists its flips (the
    /// cache-leads real-difference discipline the AI-panel visibility
    /// pipeline keeps).
    /// </summary>
    public bool ShowVwap
    {
        get => _showVwap;
        private set => this.RaiseAndSetIfChanged(ref _showVwap, value);
    }

    private bool _showVolumeProfile = UiSettingsKeys.DefaultChartShowVolumeProfile;

    /// <summary>
    /// Whether the chart draws the right-margin volume-at-price histogram
    /// (T-093); the same settings lifecycle as <see cref="ShowVwap"/>.
    /// </summary>
    public bool ShowVolumeProfile
    {
        get => _showVolumeProfile;
        private set => this.RaiseAndSetIfChanged(ref _showVolumeProfile, value);
    }

    private ChartSeriesType _chartType = ChartSeriesType.Candlesticks;

    /// <summary>
    /// The price-chart rendering type (T-089): candlesticks (the standing
    /// default), line, area, OHLC bars or Heikin-Ashi. Every user change
    /// persists into the per-symbol map
    /// <see cref="UiSettingsKeys.ChartSymbolTypes"/> under the workspace
    /// symbol; an addressed restore (a symbol switch to a pair with a
    /// stored choice, or the startup map seed) applies under the
    /// <see cref="_isApplyingPersistedChartType"/> gate — a restore is a
    /// read, never a rewrite. A symbol without a stored entry keeps the
    /// standing type: a preference map substitutes nothing silently.
    /// </summary>
    public ChartSeriesType ChartType
    {
        get => _chartType;
        set
        {
            if (_chartType == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _chartType, value);

            // The selector shim follows the canonical type (the same shim
            // discipline the symbol combo keeps): the combo's index must
            // track any applied or restored type without its own writer.
            this.RaisePropertyChanged(nameof(SelectedChartTypeIndex));
        }
    }

    /// <summary>
    /// The type selector's two-way shim: the combo's selected index over the
    /// five static localized items (the <c>SelectedThemeIndex</c> settings
    /// precedent — static labels never live on ViewModel properties).
    /// Reading returns the canonical type as an index; writing an in-range
    /// index forwards it into <see cref="ChartType"/> (the source of
    /// truth), an out-of-range write is dropped.
    /// </summary>
    public int SelectedChartTypeIndex
    {
        get => (int)ChartType;
        set
        {
            if (value >= 0 && value <= (int)ChartSeriesType.HeikinAshi)
            {
                ChartType = (ChartSeriesType)value;
            }
        }
    }

    private PriceScaleMode _scaleMode = PriceScaleMode.Linear;

    /// <summary>
    /// The Y-axis scale mode (T-090): linear (the standing default),
    /// logarithmic or percent change over the window's first visible close.
    /// Every user change persists into the per-symbol map
    /// <see cref="UiSettingsKeys.ChartSymbolScales"/> under the workspace
    /// symbol; an addressed restore (a symbol switch to a pair with a stored
    /// mode, or the startup map seed) applies under the
    /// <see cref="_isApplyingPersistedScaleMode"/> gate — a restore is a
    /// read, never a rewrite. A symbol without a stored entry keeps the
    /// standing mode: a preference map substitutes nothing silently.
    /// </summary>
    public PriceScaleMode ScaleMode
    {
        get => _scaleMode;
        set
        {
            if (_scaleMode == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _scaleMode, value);

            // The selector shim follows the canonical mode (the same shim
            // discipline the chart-type combo keeps): the combo's index must
            // track any applied or restored mode without its own writer.
            this.RaisePropertyChanged(nameof(SelectedScaleModeIndex));
        }
    }

    /// <summary>
    /// The mode selector's two-way shim: the combo's selected index over the
    /// three static localized items (the <c>SelectedChartTypeIndex</c> shim
    /// precedent — static labels never live on ViewModel properties).
    /// Reading returns the canonical mode as an index; writing an in-range
    /// index forwards it into <see cref="ScaleMode"/> (the source of truth),
    /// an out-of-range write is dropped.
    /// </summary>
    public int SelectedScaleModeIndex
    {
        get => (int)ScaleMode;
        set
        {
            if (value >= 0 && value <= (int)PriceScaleMode.Percent)
            {
                ScaleMode = (PriceScaleMode)value;
            }
        }
    }

    private IReadOnlyList<Bar>? _heikinAshiBars;

    /// <summary>
    /// The Heikin-Ashi series the chart draws while <see cref="ChartType"/>
    /// is Heikin-Ashi (T-089): the client-side <see cref="ZettaTrade.Charting.HeikinAshi"/>
    /// transform over the loaded series, aligned with <see cref="Bars"/> by
    /// index. Null in every other type — the chart renders its own variant
    /// from the raw bars.
    /// </summary>
    public IReadOnlyList<Bar>? HeikinAshiBars
    {
        get => _heikinAshiBars;
        private set => this.RaiseAndSetIfChanged(ref _heikinAshiBars, value);
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
    /// The gap-repair additions the delivered series heals with (T-086):
    /// each emission is the fetched hole-filling slice one repair pass
    /// produced for the CURRENT delivery. Raw — the UI-thread subscriber
    /// merges it into <see cref="Bars"/> by open time (the displayed bar
    /// wins every equal open; the merge is strictly additive).
    /// </summary>
    public IObservable<IReadOnlyList<Bar>> RepairedBarsUpdates =>
        _repairedAdds.Select(static payload => payload.Adds);

    /// <summary>
    /// The indicator overlay lines computed for the current series (see
    /// <see cref="ChartIndicatorPipeline.Compute"/>). Raw - not UI-marshaled;
    /// the property assignment is the marshalled hop, this stream lets tests
    /// and future consumers read the semantics off the raw publish path.
    /// </summary>
    public IObservable<IReadOnlyList<ChartIndicatorLine>> IndicatorLinesUpdates { get; }

    /// <summary>
    /// The session VWAP overlay computed for the current series (null while
    /// the overlay is off — see <see cref="VwapOverlay"/>). Raw - not
    /// UI-marshaled; the property assignment is the marshalled hop, this
    /// stream lets tests read the semantics off the raw publish path.
    /// </summary>
    public IObservable<VwapOverlay?> VwapOverlayUpdates { get; }

    /// <summary>
    /// The Heikin-Ashi series computed for the current series (null while
    /// the type is not Heikin-Ashi — see <see cref="HeikinAshiBars"/>). Raw -
    /// not UI-marshaled; the property assignment is the marshalled hop, this
    /// stream lets tests read the semantics off the raw publish path.
    /// </summary>
    public IObservable<IReadOnlyList<Bar>?> HeikinAshiBarsUpdates { get; }

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

    private VwapOverlay? _vwapOverlay;

    /// <summary>
    /// The session-anchored VWAP overlay the chart draws (T-093): value
    /// series aligned with <see cref="Bars"/> by index, computed by the pure
    /// <see cref="VwapOverlay.Compute"/> exactly the way the indicator lines
    /// are; null while the overlay is switched off. The assignment is the
    /// single UI-thread hop of its publish, and the draw pass slices it
    /// with the visible window.
    /// </summary>
    public VwapOverlay? VwapOverlay
    {
        get => _vwapOverlay;
        private set => this.RaiseAndSetIfChanged(ref _vwapOverlay, value);
    }

    private LiquidationLevelsOverlay? _liquidationLevelsOverlay;

    /// <summary>
    /// The estimated liquidation-levels overlay the chart draws on the price
    /// pane (T-243): the local estimator's bands from the chart's own OI
    /// deltas and the venue's public bracket table; null while either input
    /// is absent (the honest absence — the chart draws nothing). The
    /// assignment is the single UI-thread hop of its publish.
    /// </summary>
    public LiquidationLevelsOverlay? LiquidationLevelsOverlay
    {
        get => _liquidationLevelsOverlay;
        private set => this.RaiseAndSetIfChanged(ref _liquidationLevelsOverlay, value);
    }

    private IReadOnlyList<SignalMarker> _signalMarkers = [];

    /// <summary>
    /// The strategy-signal markers the chart overlays at the candles'
    /// close levels (T-112): one entry per displayed bar that carries a
    /// signal, keyed by the bar's own open time and resolved against the
    /// CURRENT loaded series — a marker whose candle left the series
    /// disappears instead of shifting. The assignment is the single
    /// UI-thread hop of its publish; the draw pass anchors each marker on
    /// its own candle exactly the way candles ride their slots.
    /// </summary>
    public IReadOnlyList<SignalMarker> SignalMarkers
    {
        get => _signalMarkers;
        private set => this.RaiseAndSetIfChanged(ref _signalMarkers, value);
    }

    /// <summary>
    /// One delivered full load, with the (symbol, time frame, adapter) triple
    /// that produced it: the private payload the delivered subscriber uses to
    /// pin <see cref="_loadedTarget"/> for the paging path. The load also
    /// carries the triple's own persisted drawings layer (T-091) — the page
    /// is the one truth the drawings state mirrors.
    /// </summary>
    private sealed record ChartBarsPage(
        Symbol Symbol,
        TimeFrame TimeFrame,
        IMarketDataAdapter Adapter,
        IReadOnlyList<Bar> Bars,
        IReadOnlyList<ChartDrawing> Drawings);

    /// <summary>
    /// One full load's pair (T-091): the bars the same (symbol, time frame,
    /// adapter) load served and the key's persisted drawings layer, joined
    /// before either reaches the page so a page is always complete.
    /// </summary>
    private sealed record ChartSeriesLoad(
        IReadOnlyList<Bar> Bars,
        IReadOnlyList<ChartDrawing> Drawings);

    /// <summary>
    /// One funding-pane load's payload (T-240): the events the service
    /// resolved (null = the honest no-pane — no capability or a fetch that
    /// came back empty), the series identity the load kicked for and the
    /// kick's epoch (the stale-drop: a superseded load drops at dispatch).
    /// </summary>
    private sealed record FundingPaneLoad(
        Symbol Symbol,
        string ExchangeId,
        int Epoch,
        IReadOnlyList<FundingRatePoint>? Points);

    /// <summary>
    /// One OI-pane load's payload (T-241): the snapshots the service
    /// resolved (null = the honest no-pane — no capability or a fetch that
    /// came back empty), the series identity the load kicked for (the bar
    /// interval rides it — the bucket size keyed the fetch), and the kick's
    /// epoch (the stale-drop: a superseded load drops at dispatch).
    /// </summary>
    private sealed record OpenInterestPaneLoad(
        Symbol Symbol,
        string ExchangeId,
        TimeFrame Interval,
        int Epoch,
        IReadOnlyList<OpenInterestPoint>? Points);

    /// <summary>
    /// One liquidation-levels load's payload (T-243): the venue's public
    /// bracket table the service resolved (null = the honest no-table — no
    /// capability or a spot symbol), the series identity the load kicked for
    /// and the kick's epoch (the stale-drop: a superseded load drops at
    /// dispatch).
    /// </summary>
    private sealed record LiquidationLevelsLoad(
        Symbol Symbol,
        string ExchangeId,
        int Epoch,
        IReadOnlyList<LeverageBracket>? Brackets);

    private string? _errorMessage;

    /// <summary>Last candles-export error, for the error line under the chart header.</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (ErrorMessage == value)
            {
                return;
            }

            _errorMessage = value;
            this.RaisePropertyChanged(nameof(ErrorMessage));
            this.RaisePropertyChanged(nameof(HasErrorMessage));
        }
    }

    /// <summary>True while <see cref="ErrorMessage"/> holds text; drives the error line visibility.</summary>
    public bool HasErrorMessage => _errorMessage is not null;

    private string? _historyLimitNote;

    /// <summary>
    /// The localized note stating where the deep history ENDS for the loaded
    /// triple (T-086): the feed serves no candles older than the boundary
    /// the repair chain or the paging machinery discovered. Null while the
    /// limit is unknown — a rebase clears it with the fresh series.
    /// </summary>
    public string? HistoryLimitNote
    {
        get => _historyLimitNote;
        private set
        {
            if (HistoryLimitNote == value)
            {
                return;
            }

            _historyLimitNote = value;
            this.RaisePropertyChanged(nameof(HistoryLimitNote));
            this.RaisePropertyChanged(nameof(HasHistoryLimitNote));
        }
    }

    /// <summary>True while <see cref="HistoryLimitNote"/> holds text; drives the note line visibility.</summary>
    public bool HasHistoryLimitNote => _historyLimitNote is not null;

    /// <summary>Exports the loaded candle series as a CSV file.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ExportCandlesCommand { get; }

    /// <summary>Asks the view for the candles CSV destination (a save-file dialog); null cancels.</summary>
    public Interaction<string, string?> SaveCandlesCsvRequested { get; }

    /// <summary>
    /// Exports the current chart frame as a PNG snapshot (T-094): the view
    /// renders the frame through the chart control at a fixed scale with the
    /// symbol/timeframe watermark, to the user-picked path.
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid> ExportPngCommand { get; }

    /// <summary>
    /// Asks the view for the PNG destination AND the render itself (the
    /// chart control is view state): the request carries the suggested file
    /// name and the watermark, a null output is the user cancelling, a
    /// render or write fault propagates to the flow's catch.
    /// </summary>
    public Interaction<PngExportRequest, string?> ExportPngRequested { get; }

    /// <summary>Exports exactly the visible candle range as a CSV file (T-094).</summary>
    public ReactiveCommand<RxVoid, RxVoid> ExportVisibleCandlesCommand { get; }

    /// <summary>
    /// Asks the view for the visible-range CSV destination and the chart's
    /// visible slice (both view state): the output is the handoff, null
    /// cancels.
    /// </summary>
    public Interaction<string, VisibleCsvHandoff?> ExportVisibleCsvRequested { get; }

    /// <summary>The toolbar toggle: show or hide the session VWAP overlay (T-093).</summary>
    public ReactiveCommand<RxVoid, RxVoid> ToggleVwapCommand { get; }

    /// <summary>The toolbar toggle: show or hide the volume-at-price histogram (T-093).</summary>
    public ReactiveCommand<RxVoid, RxVoid> ToggleVolumeProfileCommand { get; }

    private IReadOnlyList<ChartCompareLayer> _compareLayers = [];

    /// <summary>
    /// The symbol-compare layers (T-124): every extra series the user
    /// added over the loaded one. Chart-level, transient state — never
    /// persisted and never part of the workspace symbol preference; the
    /// renderer aligns each layer against the visible window per pass.
    /// A layer survives a workspace symbol switch on the same adapter and
    /// interval, an interval or an exchange change drops the lot, and the
    /// layer list empties long before the fetched series could read
    /// against a foreign open-time grid.
    /// </summary>
    public IReadOnlyList<ChartCompareLayer> CompareLayers
    {
        get => _compareLayers;
        private set => this.RaiseAndSetIfChanged(ref _compareLayers, value);
    }

    private IReadOnlyList<ChartDrawing> _drawings = [];

    /// <summary>
    /// The standing drawings layer (T-091): the annotations of the loaded
    /// (exchange, symbol, interval) key, mirrored to the chart control and
    /// rewritten in place by commit/edit/delete. The delivered page owns the
    /// layer at every full load (see the drawings rebase in the delivered
    /// subscriber); between loads the layer is exactly what the store last
    /// acknowledged plus this session's own acknowledged saves.
    /// </summary>
    public IReadOnlyList<ChartDrawing> Drawings
    {
        get => _drawings;
        private set => this.RaiseAndSetIfChanged(ref _drawings, value);
    }

    private ChartDrawingTool _activeDrawingTool = ChartDrawingTool.None;

    /// <summary>
    /// The armed drawing tool (T-091): the palette's toggle state, bound to
    /// the chart control's own tool property — arming here arms the control's
    /// placement gesture, dropping to <see cref="ChartDrawingTool.None"/>
    /// stands it down. A same-tool press toggles off.
    /// </summary>
    public ChartDrawingTool ActiveDrawingTool
    {
        get => _activeDrawingTool;
        private set
        {
            this.RaiseAndSetIfChanged(ref _activeDrawingTool, value);
            this.RaisePropertyChanged(nameof(IsTrendLineToolActive));
            this.RaisePropertyChanged(nameof(IsLevelToolActive));
            this.RaisePropertyChanged(nameof(IsFibToolActive));
            this.RaisePropertyChanged(nameof(IsZoneToolActive));
            this.RaisePropertyChanged(nameof(IsTextToolActive));
            this.RaisePropertyChanged(nameof(IsMeasureToolActive));
        }
    }

    /// <summary>Whether the trend-line tool is the armed one (the palette button's on-state).</summary>
    public bool IsTrendLineToolActive => ActiveDrawingTool == ChartDrawingTool.TrendLine;

    /// <summary>Whether the horizontal-level tool is the armed one (the palette button's on-state).</summary>
    public bool IsLevelToolActive => ActiveDrawingTool == ChartDrawingTool.HorizontalLevel;

    /// <summary>Whether the fib-retracement tool is the armed one (the palette button's on-state).</summary>
    public bool IsFibToolActive => ActiveDrawingTool == ChartDrawingTool.FibRetracement;

    /// <summary>Whether the zone tool is the armed one (the palette button's on-state, T-200).</summary>
    public bool IsZoneToolActive => ActiveDrawingTool == ChartDrawingTool.Zone;

    /// <summary>Whether the text-annotation tool is the armed one (the palette button's on-state, T-200).</summary>
    public bool IsTextToolActive => ActiveDrawingTool == ChartDrawingTool.Text;

    /// <summary>Whether the measure tool is the armed one (the palette button's on-state, T-200).</summary>
    public bool IsMeasureToolActive => ActiveDrawingTool == ChartDrawingTool.Measure;

    private Guid? _selectedDrawingId;

    /// <summary>
    /// The selected drawing's identity (T-091): the control's press route
    /// raises it through <see cref="CandlestickChart.DrawingSelected"/> and
    /// the view mirrors it here; the Delete key's removal and the rebase
    /// clear it. Bound one-way to the control, so the loop closes VM-side.
    /// </summary>
    public Guid? SelectedDrawingId
    {
        get => _selectedDrawingId;
        private set => this.RaiseAndSetIfChanged(ref _selectedDrawingId, value);
    }

    /// <summary>
    /// The drawings palette (T-091): pressing a tool arms it (or toggles a
    /// standing tool off), the control's placement machine re-arms with the
    /// changed value and the in-flight gesture stands down.
    /// </summary>
    public ReactiveCommand<ChartDrawingTool, RxVoid> ToolCommand { get; }

    /// <summary>
    /// The selected drawing's removal affordance (T-178): the header
    /// strip's visible delete button rides the same route the chart's
    /// Delete key names — <see cref="DeleteDrawingAsync"/>, the whole
    /// layer saves and the settled layer republishes. Gated on the
    /// standing selection: with nothing selected the affordance stays
    /// disabled, the honest answer to "delete what?", and it re-arms the
    /// moment a drawing is selected again.
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid> DeleteSelectedDrawingCommand { get; }

    private string _mainSeriesLabel = string.Empty;

    /// <summary>
    /// The main series' label the legend chip strip prints (T-124): the
    /// standing workspace symbol's normalized name, empty while none
    /// stands. A derived view of <see cref="SelectedSymbol"/> — the raise
    /// subscription mirrors the selection.
    /// </summary>
    public string MainSeriesLabel
    {
        get => _mainSeriesLabel;
        private set => this.RaiseAndSetIfChanged(ref _mainSeriesLabel, value);
    }

    /// <summary>The toolbar action: asks the view for the compare symbol (T-124), via the palette's compare-mode ride (T-095).</summary>
    public ReactiveCommand<RxVoid, RxVoid> CompareCommand { get; }

    /// <summary>
    /// Asks the view to surface the global symbol palette in compare mode
    /// (T-095 synergy): the picked symbol enters the compare fetch flow,
    /// a null output is the user closing without a pick (not an error).
    /// </summary>
    public Interaction<RxVoid, Symbol?> PickCompareRequested { get; }

    /// <summary>
    /// Asks the view to prompt the text annotation's string (T-200): the
    /// answer becomes the annotation's text, a null output is the user
    /// cancelling — the placed anchor stands down and nothing persists.
    /// </summary>
    public Interaction<RxVoid, string?> TextAnnotationRequested { get; }

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

    /// <summary>
    /// The funding pane's standing series (T-240): one value per bar slot of
    /// the series it aligned against (wire fractions, null = the honest gap
    /// before the venue's first event), the identity it was loaded for, and
    /// the kick counter the stale-drop reads. Cleared on every rebase and
    /// every no-capability verdict.
    /// </summary>
    private IReadOnlyList<decimal?>? _fundingSeries;
    private (Symbol Symbol, string ExchangeId)? _fundingTarget;
    private int _fundingKickEpoch;

    /// <summary>
    /// The OI pane's standing series (T-241): one value per bar slot of the
    /// series it aligned against (venue sizes, null = the honest gap before
    /// the venue's first snapshot), the identity it was loaded for (the
    /// bar interval rides it — the bucket size keyed the fetch), and the
    /// kick counter the stale-drop reads. Cleared on every rebase and every
    /// no-capability verdict.
    /// </summary>
    private IReadOnlyList<decimal?>? _oiSeries;

    /// <summary>The standing bracket table the levels estimate through (T-243): null until the front answers.</summary>
    private IReadOnlyList<LeverageBracket>? _liqBrackets;

    /// <summary>The standing identity the liquidation levels' kicks address (T-243): the brackets are interval-free.</summary>
    private (Symbol Symbol, string ExchangeId)? _liqTarget;

    private int _liqKickEpoch;

    /// <summary>
    /// Whether the liquidation levels' bracket input settled for the
    /// standing identity (T-243): false until the load applies — the
    /// estimate publishes only when both inputs are settled, never a
    /// flashed wrong note while the fetch still rides.
    /// </summary>
    private bool _liqBracketsSettled;

    private (Symbol Symbol, string ExchangeId, TimeFrame Interval)? _oiTarget;
    private int _oiKickEpoch;

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

    #region Deep-history Repair State

    /// <summary>
    /// One repair chain's additions payload: the fetched hole-filling bars
    /// for the given delivered page (the page is the stamp the UI-thread
    /// merge subscriber matches against <see cref="_loadedTarget"/> — a
    /// repair racing a rebase drops with its stale stamp).
    /// </summary>
    private sealed record RepairedBars(
        ChartBarsPage Source,
        IReadOnlyList<Bar> Adds);

    /// <summary>The repair additions' sink (hot; see <see cref="RepairedBarsUpdates"/>).</summary>
    private readonly Subject<RepairedBars> _repairedAdds = new();

    /// <summary>
    /// One history-limit note payload: the boundary text stamped with the
    /// delivered page it was composed for (the chain composes on its own
    /// thread — the marshalled apply matches the stamp against
    /// <see cref="_loadedTarget"/> in dispatch order, so a note racing a
    /// rebase drops exactly like stale additions).
    /// </summary>
    private sealed record HistoryNote(
        ChartBarsPage Source,
        string Text);

    /// <summary>The history-limit notes' sink (hot; see the marshalled apply).</summary>
    private readonly Subject<HistoryNote> _historyNotes = new();

    /// <summary>
    /// The LIVING repair epoch: the pass chain in flight claims exactly this
    /// source, and the delivery boundary that replaces it takes it
    /// ATOMICALLY (an exchange — the chain's own finally clears the field
    /// with a compare-exchange that only ever matches its own epoch, so a
    /// newer delivery's epoch is never destroyed by a finishing chain). The
    /// cancel and dispose belong exactly to one side at any moment.
    /// </summary>
    private CancellationTokenSource? _repairEpoch;

    /// <summary>The deep-history fetcher the repair passes ride (bounded segments, write-through before emit).</summary>
    private readonly CandleBackfillService _backfill;

    /// <summary>
    /// The strategy signals service (T-112): the per-series stance lookups
    /// the marker publish maps over the loaded bars and the signal stream
    /// re-inking it. Inert output — read only, never routed toward an order.
    /// </summary>
    private readonly StrategySignalService _signalService;

    /// <summary>
    /// The loads relay (T-112): every delivered full load publishes here as
    /// the strategy host's feed address. The relay owns the chart↔service
    /// decoupling; the chart pushes only what the chart machine delivered.
    /// </summary>
    private readonly SignalSeriesRelay _seriesRelay;

    /// <summary>
    /// The signal rail panel (T-112): the workspace strip renders it under
    /// the chart; its coalescing and click routes live there.
    /// </summary>
    public SignalRailViewModel SignalRail { get; }

    #endregion

    #region Chart Templates Surface

    /// <summary>
    /// The named chart templates' manager (T-202): non-null on the primary
    /// only — the slots carry none, and the toolbar's templates affordance
    /// hides on them with it.
    /// </summary>
    public ChartTemplatesViewModel? Templates => _templates;

    /// <summary>
    /// Whether the templates affordance belongs to this chart's surface:
    /// the manager is hosted and the face choices persist from here (the
    /// primary's surface alone).
    /// </summary>
    public bool HasTemplates => _templates is not null && _persistsFaceChoices;

    /// <summary>
    /// Whether this instance persists the shared face state (the primary
    /// yes, a slot no) — the testability surface of the T-201 gate.
    /// </summary>
    internal bool PersistsFaceChoices => _persistsFaceChoices;

    /// <summary>
    /// Capture the current chart face for the templates manager's save
    /// (T-202): the rendering type, the scale mode, the indicator
    /// configuration verbatim and the two overlay toggles.
    /// </summary>
    internal ChartTemplateFace CaptureTemplateFace() =>
        new(ChartType, ScaleMode, _persistedIndicatorConfig, ShowVwap, ShowVolumeProfile);

    /// <summary>
    /// Report a templates-manager failure into the chart's error line: the
    /// manager's refusals and failed applies surface where the chart's own
    /// export failures already do.
    /// </summary>
    internal void ReportTemplateError(string message) => ErrorMessage = message;

    /// <summary>
    /// Re-apply the standing default template's face (T-202): a default's
    /// change — manager-made or external — reached the chart. The restore
    /// hooks resolve the standing symbol's stored entry first (a stored
    /// entry keeps winning over the default); an entry-less symbol seeds
    /// from the default.
    /// </summary>
    private void ReapplyDefaultTemplateFace()
    {
        ApplyPersistedChartType(SelectedSymbol);
        ApplyPersistedScaleMode(SelectedSymbol);
    }

    #endregion

    #region Indicator Overlay State

    /// <summary>
    /// The indicator overlay publishes here; the marshalled subscription
    /// assigns <see cref="IndicatorLines"/>. The compute itself is pure -
    /// the publish runs wherever its callers land and only the assignment
    /// crosses onto the UI thread.
    /// </summary>
    private readonly Subject<IReadOnlyList<ChartIndicatorLine>> _indicatorPublishes = new();

    /// <summary>
    /// The session VWAP overlay publishes here; the marshalled subscription
    /// assigns <see cref="VwapOverlay"/>. A null emission is the disabled
    /// state: the chart draws nothing. The compute itself is pure — the
    /// publish runs wherever its callers land and only the assignment
    /// crosses onto the UI thread.
    /// </summary>
    private readonly Subject<VwapOverlay?> _vwapPublishes = new();

    /// <summary>
    /// The Heikin-Ashi series publishes here (T-089); the marshalled
    /// subscription assigns <see cref="HeikinAshiBars"/>. A null emission is
    /// the non-Heikin-Ashi (or empty-series) state. The transform is pure —
    /// the publish runs wherever its callers land and only the assignment
    /// crosses onto the UI thread.
    /// </summary>
    private readonly Subject<IReadOnlyList<Bar>?> _heikinAshiPublishes = new();

    /// <summary>
    /// The funding pane's loads publish here (T-240): the fetch runs
    /// fire-and-forget wherever it lands, the marshalled subscription aligns
    /// the events against the standing series ON THE UI THREAD (the
    /// alignment reads the live bar collection) and re-publishes the
    /// overlays. A null emission is the honest no-pane: no capability, no
    /// events, or a dropped stale load.
    /// </summary>
    private readonly Subject<FundingPaneLoad?> _fundingPublishes = new();

    /// <summary>
    /// The OI pane's loads publish here (T-241): the same marshalled
    /// discipline as the funding pane's — the fetch runs fire-and-forget
    /// wherever it lands, the alignment reads the live bar collection on
    /// the UI thread, and a null emission is the honest no-pane.
    /// </summary>
    private readonly Subject<OpenInterestPaneLoad?> _oiPublishes = new();

    /// <summary>
    /// The liquidation levels' brackets loads (T-243): the raw publishes of
    /// the venue's public bracket table (null = the honest no-table), each
    /// stamped with its series identity and kick epoch — the apply drops a
    /// superseded load. Raw-then-marshal split: the apply lands on whatever
    /// thread ran the fetch, only the property assignment hops.
    /// </summary>
    private readonly Subject<LiquidationLevelsLoad?> _liqLevelsPublishes = new();

    /// <summary>
    /// The liquidation levels' overlay publishes (T-243): the raw publishes
    /// of the estimator's overlay (null = the honest absence), the
    /// assignment is the single UI-thread hop. Raw-then-marshal split, the
    /// same discipline as the VWAP overlay's.
    /// </summary>
    private readonly Subject<LiquidationLevelsOverlay?> _liqOverlayPublishes = new();

    /// <summary>
    /// The persisted VWAP overlay choice: the cache LEADS the toggles'
    /// persistence pipeline (updated at issue time, in issuance order), so
    /// a rapid settle burst never drops its second flip and an external
    /// writer's cached apply never rewrites the key (BUG-053's discipline).
    /// </summary>
    private bool _persistedShowVwap = UiSettingsKeys.DefaultChartShowVwap;

    /// <summary>The persisted volume-profile choice: the same leadership cache as <see cref="_persistedShowVwap"/>.</summary>
    private bool _persistedShowVolumeProfile = UiSettingsKeys.DefaultChartShowVolumeProfile;

    /// <summary>
    /// Whether the repository restore is addressing the chart type: the
    /// per-symbol application suppresses exactly that assignment — the
    /// persistence pipeline must never rewrite a stored choice it merely
    /// applied (the same restore-not-edit discipline the time frame keeps).
    /// </summary>
    private bool _isApplyingPersistedChartType;

    /// <summary>
    /// The persisted per-symbol chart types (T-089): seeded from the
    /// settings service and kept current by the changes stream, so every
    /// symbol switch resolves its stored choice from the cache (the same
    /// pure-consumer shape the symbol marks keep).
    /// </summary>
    private IReadOnlyDictionary<string, ChartSeriesType> _persistedChartTypes =
        new Dictionary<string, ChartSeriesType>(StringComparer.Ordinal);

    /// <summary>
    /// Whether the repository restore is addressing the Y-axis scale mode:
    /// the per-symbol application suppresses exactly that assignment —
    /// the persistence pipeline must never rewrite a stored mode it merely
    /// applied (the same restore-not-edit discipline the chart type keeps).
    /// </summary>
    private bool _isApplyingPersistedScaleMode;

    /// <summary>
    /// The persisted per-symbol Y-axis scale modes (T-090): seeded from the
    /// settings service and kept current by the changes stream, so every
    /// symbol switch resolves its stored mode from the cache (the same
    /// pure-consumer shape the chart types keep).
    /// </summary>
    private IReadOnlyDictionary<string, PriceScaleMode> _persistedScaleModes =
        new Dictionary<string, PriceScaleMode>(StringComparer.Ordinal);

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

    /// <summary>
    /// The persisted oscillator panes' heights (T-199): seeded from the
    /// settings service, kept current by the changes stream and merged into
    /// every indicator publish by name. The map rides the SIBLING settings
    /// key — the settings window's Save writes the indicator rows
    /// wholesale, so a pane height stored in a row would be clobbered by
    /// the next save; the sibling key keeps the two writers disjoint.
    /// </summary>
    private IReadOnlyDictionary<string, double> _persistedPaneHeights =
        new Dictionary<string, double>(StringComparer.Ordinal);

    /// <summary>
    /// The pane-heights JSON the pane pipeline last applied: the
    /// changes-stream echo guard — the persistence write comes back through
    /// the stream and an equal JSON re-applies nothing (the T-093
    /// issue-time-lead discipline).
    /// </summary>
    private string? _appliedPaneHeightsJson;

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

    #region Trading Footprint Overlay (T-122)

    /// <summary>
    /// The trading footprint's feed stream (T-122): the lane's keyed
    /// overlays the pick pass resolves against the workspace symbol.
    /// Consumed as inert drawing data — nothing routes back toward an
    /// order from here.
    /// </summary>
    private readonly IObservable<ChartFootprintSnapshot> _tradingFootprint;

    /// <summary>The funding-history front (T-240): the active adapter's capability probe and the per-symbol cache.</summary>
    private readonly FundingHistoryService _fundingHistory;

    /// <summary>The OI-history front (T-241): the active adapter's capability probe and the per-(symbol, interval) cache.</summary>
    private readonly OIHistoryService _openInterestHistory;

    /// <summary>The leverage-brackets front (T-243): the active adapter's capability probe and the per-symbol cache.</summary>
    private readonly LeverageBracketsService _leverageBrackets;

    /// <summary>The latest footprint snapshot the feed published (UI-thread-owned, marshalled on assignment).</summary>
    private ChartFootprintSnapshot _latestFootprint = ChartFootprintSnapshot.Empty;

    private PositionOverlay? _tradingPosition;

    /// <summary>
    /// The workspace symbol's position the chart draws (T-122): the entry
    /// line with its direction arrow and the floating PnL tag snapped to
    /// the latest observed price; null while the workspace symbol has no
    /// position on the trading lane.
    /// </summary>
    public PositionOverlay? TradingPosition
    {
        get => _tradingPosition;
        private set => this.RaiseAndSetIfChanged(ref _tradingPosition, value);
    }

    private IReadOnlyList<WorkingOrderLine> _tradingOrders = [];

    /// <summary>
    /// The workspace symbol's working price-bearing orders (T-122): one
    /// dashed line per order. Empty while none stand.
    /// </summary>
    public IReadOnlyList<WorkingOrderLine> TradingOrders
    {
        get => _tradingOrders;
        private set => this.RaiseAndSetIfChanged(ref _tradingOrders, value);
    }

    private IReadOnlyList<FillMarker> _tradingFills = [];

    /// <summary>
    /// The workspace symbol's executed fills from the journal of record
    /// (T-122): one diamond per execution, anchored at its own candle.
    /// Empty while the series shows none.
    /// </summary>
    public IReadOnlyList<FillMarker> TradingFills
    {
        get => _tradingFills;
        private set => this.RaiseAndSetIfChanged(ref _tradingFills, value);
    }

    private decimal? _measureStopPrice;

    /// <summary>
    /// The measure ruler's stop context (T-200): the workspace symbol's
    /// working stop order nearest the position's entry — the risk
    /// distance the R-multiple row divides the framed move by; null while
    /// no stop rides the symbol (the ruler reads its Δ rows alone).
    /// </summary>
    public decimal? MeasureStopPrice
    {
        get => _measureStopPrice;
        private set => this.RaiseAndSetIfChanged(ref _measureStopPrice, value);
    }

    /// <summary>
    /// Resolve the footprint overlays for the CURRENT workspace symbol:
    /// a pure pick over the feed's latest snapshot (a re-publish rides the
    /// feed's pushes and every symbol change; the overlays ride the UI
    /// thread by the same marshal discipline the other publishes keep).
    /// </summary>
    private void PublishTradingFootprint()
    {
        var symbol = SelectedSymbol?.Normalized;
        TradingPosition = SelectTradingPosition(symbol, _latestFootprint.Positions);
        TradingOrders = SelectTradingOrders(symbol, _latestFootprint.Orders);
        TradingFills = SelectTradingFills(symbol, _latestFootprint.Fills);
        MeasureStopPrice = SelectMeasureStopPrice(
            symbol,
            _latestFootprint.Stops,
            TradingPosition);
    }

    /// <summary>
    /// The pure stop-context pick the publish uses (T-200, internal static
    /// so the goldens pin it): the workspace symbol's stop nearest the
    /// position's entry — the protective stop's own risk distance — or,
    /// with no position standing, the symbol's first stop in feed order.
    /// </summary>
    internal static decimal? SelectMeasureStopPrice(
        string? symbolNormalized,
        IReadOnlyList<FootprintStop> stops,
        PositionOverlay? position)
    {
        if (symbolNormalized is null)
        {
            return null;
        }

        var candidates = stops
            .Where(s => string.Equals(s.Symbol, symbolNormalized, StringComparison.Ordinal))
            .Select(static s => s.Price)
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        return position is not { } overlay
            ? candidates[0]
            : candidates.OrderBy(s => Math.Abs(s - overlay.EntryPrice)).First();
    }

    /// <summary>
    /// The pure position pick the publish uses (internal static so the
    /// goldens pin it without a VM instance — the
    /// <see cref="ComposeSignalMarkers"/> precedent): exactly the one
    /// account of the matching normalized symbol; a lane holds one account
    /// pair, null when the symbol shows none.
    /// </summary>
    internal static PositionOverlay? SelectTradingPosition(
        string? symbolNormalized,
        IReadOnlyList<FootprintPosition> positions)
    {
        if (symbolNormalized is null)
        {
            return null;
        }

        return positions
            .FirstOrDefault(p => string.Equals(p.Symbol, symbolNormalized, StringComparison.Ordinal))
            is { } match
                ? match.Overlay
                : null;
    }

    /// <summary>The pure working-order pick: every order of the matching symbol, feed order kept.</summary>
    internal static IReadOnlyList<WorkingOrderLine> SelectTradingOrders(
        string? symbolNormalized,
        IReadOnlyList<FootprintOrder> orders)
    {
        if (symbolNormalized is null)
        {
            return [];
        }

        return orders
            .Where(o => string.Equals(o.Symbol, symbolNormalized, StringComparison.Ordinal))
            .Select(static o => o.Line)
            .ToArray();
    }

    /// <summary>The pure fill pick: every executed fill of the matching symbol, ascending order kept.</summary>
    internal static IReadOnlyList<FillMarker> SelectTradingFills(
        string? symbolNormalized,
        IReadOnlyList<FootprintFill> fills)
    {
        if (symbolNormalized is null)
        {
            return [];
        }

        return fills
            .Where(f => string.Equals(f.Symbol, symbolNormalized, StringComparison.Ordinal))
            .Select(static f => f.Marker)
            .ToArray();
    }

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
    /// <param name="export">The CSV export service the loaded series
    /// streams through when the user asks for the candles file.</param>
    /// <param name="localization">The resx mirror the export fault line
    /// resolves its message through.</param>
    /// <param name="candleCache">The local candle cache (T-085): the
    /// bootstrap page serves the newest cached window first and the
    /// network refresh rides behind it; every served bar set persists
    /// write-through, so the next start renders instantly.</param>
    /// <param name="backfill">The deep-history fetcher (T-086): the repair
    /// chain's passes ride it for the holes the calendar detects behind a
    /// delivered window.</param>
    /// <param name="drawingStore">The persisted chart drawings store
    /// (T-091): the standing key's annotations load with every full page
    /// and every commit/edit/delete saves the whole layer back.</param>
    /// <param name="signals">The strategy signals service (T-112): the
    /// current series's stances the marker overlay reads and the signal
    /// stream the markers re-ink on. Consumed as inert output — a signal
    /// never travels toward an order from here.</param>
    /// <param name="signalRail">The signal rail panel (T-112): rendered as
    /// the workspace's signal strip under the chart and wired in
    /// composition (the rail consumes the service's stream directly).</param>
    /// <param name="seriesRelay">The loads relay (T-112): every full series
    /// load publishes here for the strategy host's feed — the relay breaks
    /// the chart↔service cycle by being the one address both consume.</param>
    /// <param name="tradingFootprint">The footprint feed's stream (T-122):
    /// the trading lane's keyed position/order/fill overlays this panel
    /// picks from — the workspace symbol leads every pick, and a re-publish
    /// rides both the feed's pushes and the symbol changes.</param>
    public ChartViewModel(
        ActiveExchangeService activeExchanges,
        SymbolListProvider symbolList,
        ISettingsService settings,
        IIndicatorCatalog indicatorCatalog,
        ICsvExportService export,
        ILocalizationManager localization,
        ICandleCache candleCache,
        CandleBackfillService backfill,
        IChartDrawingStore drawingStore,
        StrategySignalService signals,
        SignalRailViewModel signalRail,
        SignalSeriesRelay seriesRelay,
        IObservable<ChartFootprintSnapshot> tradingFootprint,
        FundingHistoryService fundingHistory, // T-240: the funding pane's history front.
        OIHistoryService openInterestHistory, // T-241: the OI pane's history front.
        LeverageBracketsService leverageBrackets, // T-243: the liquidation levels' bracket front.
        ILogger<ChartViewModel> logger,
        string? slotId = null,
        ChartTemplatesViewModel? templates = null)
    {
        _activeExchanges = activeExchanges;
        _symbolList = symbolList;
        _settings = settings;
        _indicatorCatalog = indicatorCatalog;
        _export = export;
        _localization = localization;
        _candleCache = candleCache;
        _backfill = backfill;
        _drawingStore = drawingStore;
        _logger = logger;

        _signalService = signals;
        SignalRail = signalRail;
        _seriesRelay = seriesRelay;
        _tradingFootprint = tradingFootprint;
        _fundingHistory = fundingHistory;
        _openInterestHistory = openInterestHistory;
        _leverageBrackets = leverageBrackets;

        // The multi-chart grid (T-201): the primary keeps the workspace's
        // shared symbol key (the order book and the dialog follow it) and
        // the shared time-frame key; a slot instance persists under its own
        // ChartSlot{N}* keys in the UI section. The chart-face choices ride
        // the primary only - a slot's type/scale/pane/overlay pick stays
        // in-session (the shared maps would lose entries to interleaved
        // whole-map writes otherwise).
        _slotId = slotId;
        _persistsFaceChoices = slotId is null;

        // The named templates (T-202): the primary hosts the manager and
        // hands itself over for the face captures and the error reports;
        // the slots pass none. The default's change stream re-applies the
        // default face to the standing symbol (the restore hooks resolve
        // it — a stored entry keeps winning, an entry-less symbol seeds).
        // The initial seeding is a pull through the same hooks, so the
        // manager's earlier init publish needs no replay: whichever side
        // constructs first, the hooks see the resolved default.
        _templates = templates;
        _templates?.AttachChart(() => this);
        _templates?.DefaultTemplateChanged
            .ObserveOnUiThread()
            .Subscribe(
                _ => ReapplyDefaultTemplateFace(),
                ex => _logger.LogError(ex, "The chart default template stream stopped unexpectedly"));

        ExportCandlesCommand = ReactiveCommand.CreateFromTask(HandleExportCandlesAsync);
        SaveCandlesCsvRequested = new Interaction<string, string?>();
        ExportPngCommand = ReactiveCommand.CreateFromTask(HandleExportPngAsync);
        ExportPngRequested = new Interaction<PngExportRequest, string?>();
        ExportVisibleCandlesCommand = ReactiveCommand.CreateFromTask(HandleExportVisibleCandlesAsync);
        ExportVisibleCsvRequested = new Interaction<string, VisibleCsvHandoff?>();
        ToggleVwapCommand = ReactiveCommand.Create<RxVoid>(_ => { ShowVwap = !ShowVwap; });
        ToggleVolumeProfileCommand = ReactiveCommand.Create<RxVoid>(_ => { ShowVolumeProfile = !ShowVolumeProfile; });
        CompareCommand = ReactiveCommand.CreateFromTask(HandleCompareAsync);
        PickCompareRequested = new Interaction<RxVoid, Symbol?>();
        TextAnnotationRequested = new Interaction<RxVoid, string?>();
        ToolCommand = ReactiveCommand.Create<ChartDrawingTool>(tool =>
        {
            ActiveDrawingTool = ActiveDrawingTool == tool ? ChartDrawingTool.None : tool;
        });

        // The delete affordance rides the standing selection as its gate
        // (T-178): the AlertsViewModel grammar — the selection stream's
        // null-ness arms the button, the removal clears the selection and
        // the button stands down again with it.
        DeleteSelectedDrawingCommand = ReactiveCommand.CreateFromTask(
            async () =>
            {
                if (SelectedDrawingId is { } drawingId)
                {
                    await DeleteDrawingAsync(drawingId);
                }
            },
            canExecute: this.WhenAnyValue(x => x.SelectedDrawingId)
                .Select(static id => id is not null));

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
                LoadSeries(triple.Symbol, triple.TimeFrame, triple.Adapter)
                    .Select(load => new ChartBarsPage(
                        triple.Symbol,
                        triple.TimeFrame,
                        triple.Adapter,
                        load.Bars,
                        load.Drawings)))
            .Switch()
            .Replay(1)
            .RefCount();

        BarsUpdates = _barsPages.Select(static page => page.Bars);
        OlderBarsUpdates = _olderPages;
        IndicatorLinesUpdates = _indicatorPublishes;
        VwapOverlayUpdates = _vwapPublishes;
        HeikinAshiBarsUpdates = _heikinAshiPublishes;

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
                    // new series pages from its own head. The repair chain of
                    // the replaced series dies at its kick (the delivery
                    // boundary owns the epoch's cancel and dispose), and the
                    // fresh note (if this series proves a limit) recomposes.
                    _olderFetchEpoch?.Cancel();
                    _olderFetchEpoch = null;
                    _olderAppendsPending = 0;
                    _olderHistoryExhausted = false;
                    HistoryLimitNote = null;

                    // The T-124 compare layers are chart-level state keyed
                    // to the triple: an INTERVAL or an EXCHANGE change
                    // drops the lot — a layer's open times read against the
                    // standing grid only, so riding into a different one
                    // would align nonsense. A same-interval symbol switch
                    // keeps them, and a bootstrap (no previous target) has
                    // nothing to drop.
                    if (_loadedTarget is { } previous &&
                        (previous.TimeFrame != page.TimeFrame || previous.ExchangeId != page.Adapter.Id))
                    {
                        CompareLayers = [];
                    }

                    // The drawings layer (T-091) rides the delivered page:
                    // every (exchange, symbol, interval) key mirrors its own
                    // persisted layer in, and the selection dies with the
                    // rebase — the identities belong to the standing page's
                    // list, never carry into the fresh one.
                    Drawings = page.Drawings;
                    SelectedDrawingId = null;

                    _loadedTarget = (page.Symbol, page.TimeFrame, page.Adapter.Id);
                    _loadedAdapter = page.Adapter;

                    // The funding pane (T-240) is a rebase-volatile state: the
                    // standing alignment belongs to the replaced series, the
                    // fresh one re-aligns from the service's cache-first load
                    // (the kick below) — the pane never shows a stale series'
                    // values over a fresh grid.
                    _fundingSeries = null;
                    _fundingTarget = null;

                    // The OI pane (T-241) is the same rebase-volatile state.
                    _oiSeries = null;
                    _oiTarget = null;

                    // The liquidation levels (T-243) ride the same
                    // rebase-volatile state: the fresh grid estimates from
                    // ITS OWN OI deltas once both inputs land — the old
                    // series' bands never draw over a fresh grid.
                    _liqBrackets = null;
                    _liqBracketsSettled = false;
                    _liqOverlayPublishes.OnNext(null);

                    // The T-112 strategies feed: the full load is the host's
                    // closed-candle address, published through the relay (the
                    // composition-owned decoupling — the service never holds
                    // the chart) with its series identity stamped.
                    _seriesRelay.Publish(new ChartSeriesPage(page.Symbol, page.TimeFrame, page.Bars));

                    Bars = new ObservableCollection<Bar>(page.Bars);
                    _logger.LogInformation("Chart bars delivered: {Count} candles", page.Bars.Count);

                    // The overlays re-compute against the fresh series: the
                    // delivered list IS the compute input (a 200-bar copy of
                    // the collection's content, marshal-free).
                    PublishIndicatorLines(page.Bars);
                    PublishVwapOverlay(page.Bars);
                    PublishHeikinAshi(page.Bars);
                    PublishSignalMarkers();

                    // The delivered page starts this series' repair chain
                    // (T-086): the passes probe the cache and the feed behind
                    // the window's head and fill the calendar's missing bars.
                    KickRepairPass(page);

                    // The delivered page starts the funding pane's load
                    // (T-240): a perp on an adapter that serves the history
                    // fetches cache-first and re-publishes; anything else is
                    // the honest no-pane.
                    KickFundingLoad(page.Symbol, page.Adapter.Id);

                    // The delivered page starts the OI pane's load (T-241)
                    // the same way — the bar interval rides it (the bucket
                    // size keyed the fetch).
                    KickOpenInterestLoad(page.Symbol, page.Adapter.Id, page.TimeFrame);
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
                    PublishVwapOverlay(Bars);
                    PublishHeikinAshi(Bars);
                    PublishSignalMarkers();

                    // The funding pane re-aligns against the grown series too
                    // (T-240): the cache-first load re-runs over the standing
                    // head — the prepended stretch shows the honest gaps when
                    // the cached page predates it. The OI pane (T-241) rides
                    // the same re-kick.
                    if (_loadedTarget is { } target)
                    {
                        KickFundingLoad(target.Symbol, target.ExchangeId);
                        KickOpenInterestLoad(target.Symbol, target.ExchangeId, target.TimeFrame);
                    }
                },
                ex => _logger.LogError(ex, "The chart older-bars stream stopped unexpectedly"));

        // The repair additions merge into the displayed series on the UI
        // thread: they fill a hole the delivered page left, so the merge is
        // strictly additive (UnionAscending keeps every displayed bar and an
        // equal open keeps the displayed side). A repair racing a rebase
        // carries the stale page's stamp and drops — the delivered page owns
        // the series, not the stale repair.
        _repairedAdds.ObserveOnUiThread()
            .Subscribe(
                payload =>
                {
                    if (IsStaleDelivery(payload.Source))
                    {
                        _logger.LogInformation(
                            "Chart repair additions dropped: the series was rebased during the repair pass");
                        return;
                    }

                    if (BarSeriesMerge.UnionAscending(Bars, payload.Adds) is not { } merged)
                    {
                        _logger.LogError(
                            "Chart repair additions rejected: the merged sides are not strictly ascending ({Symbol} {TimeFrame} on {Exchange})",
                            payload.Source.Symbol.Normalized,
                            payload.Source.TimeFrame,
                            payload.Source.Adapter.Id);
                        return;
                    }

                    Bars = new ObservableCollection<Bar>(merged);
                    _logger.LogInformation(
                        "Chart bars healed: {Count} candles filled ({Symbol} {TimeFrame})",
                        payload.Adds.Count,
                        payload.Source.Symbol.Normalized,
                        payload.Source.TimeFrame);

                    // The merge GREW the series: the overlays re-align with
                    // the patched collection (the compute runs right here on
                    // the UI thread — the same discipline as the prepends).
                    PublishIndicatorLines(Bars);
                    PublishVwapOverlay(Bars);
                    PublishHeikinAshi(Bars);
                    PublishSignalMarkers();

                    // The funding pane re-aligns against the healed series
                    // too (T-240): the cache-first load re-runs over the
                    // standing head — the pane survives the repair merge. The
                    // OI pane (T-241) rides the same re-kick.
                    if (_loadedTarget is { } target)
                    {
                        KickFundingLoad(target.Symbol, target.ExchangeId);
                        KickOpenInterestLoad(target.Symbol, target.ExchangeId, target.TimeFrame);
                    }
                },
                ex => _logger.LogError(ex, "The chart repair stream stopped unexpectedly"));

        // The history-limit note applies stamped (the same stale-drop the
        // additions keep): the repair chain composes on its own thread and
        // the marshalled apply assigns only while the page still owns the
        // series — in dispatch order with the deliveries.
        _historyNotes.ObserveOnUiThread()
            .Subscribe(
                note =>
                {
                    if (IsStaleDelivery(note.Source))
                    {
                        return;
                    }

                    HistoryLimitNote = note.Text;
                },
                ex => _logger.LogError(ex, "The chart history-limit stream stopped unexpectedly"));

        // The T-112 signal markers re-ink on every strategy signal: the
        // publish re-resolves the loaded series' stances against the
        // CURRENT bars (a rebase may have landed in between), so a signal
        // that raced a series swap anchors nothing stale.
        _signalService.Signals
            .ObserveOnUiThread()
            .Subscribe(
                _ => PublishSignalMarkers(),
                ex => _logger.LogError(ex, "The signal markers stream stopped unexpectedly"));

        // #region T-122: the trading footprint. Two live inputs: the feed's
        // pushes (the lane's whole keyed inventory) and the workspace
        // symbol changes — both re-run the same pure pick pass.
        tradingFootprint.ObserveOnUiThread()
            .Subscribe(
                snapshot =>
                {
                    _latestFootprint = snapshot;
                    PublishTradingFootprint();
                },
                ex => _logger.LogError(ex, "The trading footprint stream stopped unexpectedly"));
        this.WhenAnyValue(x => x.SelectedSymbol)
            .Subscribe(
                _ => PublishTradingFootprint(),
                ex => _logger.LogError(ex, "The footprint symbol pick stream stopped unexpectedly"));
        // #endregion T-122

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
                TimeFrameSettingsKey,
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
                SymbolSettingsSection,
                SymbolSettingsKey,
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

        // The oscillator panes' heights follow the settings service live
        // (T-199): one async read seeds the persisted map (a fresh install
        // keeps every pane on the layout default) and the changes stream
        // keeps it current. The map rides the SIBLING key — the settings
        // window writes the indicator rows wholesale, so a height stored in
        // a row would be clobbered by the next save.
        Observable.FromAsync(() => _settings.GetAsync<string>(
                UiSettingsKeys.Section,
                UiSettingsKeys.ChartIndicatorPaneHeights,
                UiSettingsKeys.DefaultChartIndicatorPaneHeights))
            .Subscribe(
                ApplyPaneHeights,
                ex => _logger.LogError(ex, "Failed to read the chart indicator pane heights"));

        _settings.Changes
            .Where(change => change.Section == UiSettingsKeys.Section &&
                             change.Key == UiSettingsKeys.ChartIndicatorPaneHeights)
            .Select(change => change.NewValue)
            .OfType<string>()
            .Subscribe(
                ApplyPaneHeights,
                ex => _logger.LogError(ex, "The chart indicator pane heights stream stopped unexpectedly"));

        // The computed overlays ride the same marshal chain the Bars do: the
        // publishes land on whatever thread ran the compute (delivered and
        // prepend subscribers are already marshalled, the settings paths run
        // synchronously), only the property assignment hops.
        _indicatorPublishes.ObserveOnUiThread()
            .Subscribe(
                lines => IndicatorLines = lines,
                ex => _logger.LogError(ex, "The chart indicator lines stream stopped unexpectedly"));

        // The session VWAP overlay rides the same raw-then-marshal split.
        _vwapPublishes.ObserveOnUiThread()
            .Subscribe(
                overlay => VwapOverlay = overlay,
                ex => _logger.LogError(ex, "The chart vwap overlay stream stopped unexpectedly"));

        // The Heikin-Ashi series rides the same raw-then-marshal split (T-089).
        _heikinAshiPublishes.ObserveOnUiThread()
            .Subscribe(
                bars => HeikinAshiBars = bars,
                ex => _logger.LogError(ex, "The chart heikin-ashi stream stopped unexpectedly"));

        // The funding pane's loads (T-240): the alignment runs ON the UI
        // thread (it reads the live bar collection), then the overlays
        // republish through the standing pipeline. A stale load — its kick
        // superseded by a newer one or its identity no longer standing —
        // drops silently.
        _fundingPublishes.ObserveOnUiThread()
            .Subscribe(
                ApplyFundingPaneLoad,
                ex => _logger.LogError(ex, "The chart funding pane stream stopped unexpectedly"));

        // The OI pane's loads (T-241): the same marshalled discipline — the
        // alignment runs ON the UI thread, a stale load drops silently.
        _oiPublishes.ObserveOnUiThread()
            .Subscribe(
                ApplyOpenInterestPaneLoad,
                ex => _logger.LogError(ex, "The chart open-interest pane stream stopped unexpectedly"));

        // The liquidation levels' bracket loads (T-243): the same marshalled
        // discipline — the standing table lands ON the UI thread, a stale
        // load drops silently.
        _liqLevelsPublishes.ObserveOnUiThread()
            .Subscribe(
                ApplyLiquidationLevelsLoad,
                ex => _logger.LogError(ex, "The chart liquidation-levels stream stopped unexpectedly"));

        // The liquidation levels' overlay rides the same raw-then-marshal
        // split as the VWAP overlay's (T-243): publishes stay raw, only the
        // assignment hops.
        _liqOverlayPublishes.ObserveOnUiThread()
            .Subscribe(
                overlay => LiquidationLevelsOverlay = overlay,
                ex => _logger.LogError(ex, "The chart liquidation-levels overlay stream stopped unexpectedly"));

        // The two overlay toggles persist like the AI panel's visibility
        // does (T-093): the cache LEADS the pipeline — it is updated at
        // ISSUE time, in issuance order — while the writes serialize behind
        // it, so a rapid settle burst never drops its second flip and a
        // completion-order race cannot make a stale value pass the filter
        // (the BUG-053 discipline). The real-difference filter also keeps
        // the restore's cached seed in charge: reads are never re-written.
        this.WhenAnyValue(x => x.ShowVwap)
            .Skip(1)
            .Where(value => value != _persistedShowVwap)
            .Do(value => _persistedShowVwap = value)
            .Select(value => Observable
                .FromAsync(() => SetOverlayChoiceAsync(UiSettingsKeys.ChartShowVwap, value))
                .Select(_ => value))
            .Concat()
            .Subscribe(
                _ => { },
                ex => _logger.LogError(ex, "The vwap overlay persistence pipeline stopped unexpectedly"));

        this.WhenAnyValue(x => x.ShowVolumeProfile)
            .Skip(1)
            .Where(value => value != _persistedShowVolumeProfile)
            .Do(value => _persistedShowVolumeProfile = value)
            .Select(value => Observable
                .FromAsync(() => SetOverlayChoiceAsync(UiSettingsKeys.ChartShowVolumeProfile, value))
                .Select(_ => value))
            .Concat()
            .Subscribe(
                _ => { },
                ex => _logger.LogError(ex, "The volume profile persistence pipeline stopped unexpectedly"));

        // Every toggle re-keys the published overlay against the CURRENT
        // series: a toolbar flip recomputes immediately (the bars are the
        // series' copy — the pure compute), a restore re-asserts the value.
        // The subject's publishes stay raw; only the assignment hops.
        this.WhenAnyValue(x => x.ShowVwap)
            .Subscribe(
                _ => PublishVwapOverlay(Bars),
                ex => _logger.LogError(ex, "The vwap overlay publish stream stopped unexpectedly"));

        // The overlay state follows the settings service live: an external
        // writer applies with the cache leading (so the persistence pipeline
        // above sees an equal value and skips the write). Non-bool values
        // drop — the applied choice stays in charge.
        _settings.Changes
            .Where(change => change.Section == UiSettingsKeys.Section &&
                             change.Key == UiSettingsKeys.ChartShowVwap)
            .Select(change => change.NewValue)
            .OfType<bool>()
            .Subscribe(
                enabled =>
                {
                    _persistedShowVwap = enabled;
                    ShowVwap = enabled;
                },
                ex => _logger.LogError(ex, "The chart vwap overlay stream stopped unexpectedly"));

        _settings.Changes
            .Where(change => change.Section == UiSettingsKeys.Section &&
                             change.Key == UiSettingsKeys.ChartShowVolumeProfile)
            .Select(change => change.NewValue)
            .OfType<bool>()
            .Subscribe(
                enabled =>
                {
                    _persistedShowVolumeProfile = enabled;
                    ShowVolumeProfile = enabled;
                },
                ex => _logger.LogError(ex, "The chart volume profile stream stopped unexpectedly"));

        // The chart type selector (T-089): the choice persists PER SYMBOL, in
        // the same document shape the symbol marks keep — the whole map
        // re-serializes on every flip under the workspace symbol. The cache
        // LEADS the pipeline (updated at issue time, in issuance order), so
        // a rapid flip burst never drops its second write; the gate keeps
        // the addressed restores (a symbol switch resolving its stored
        // choice) out of the persistence path — a restore is a read, not an
        // edit. A flip while no symbol stands cannot address the map and
        // stays transient (a pick without a workspace context is no one's
        // preference).
        this.WhenAnyValue(x => x.ChartType)
            .Skip(1)
            .Where(_ => !_isApplyingPersistedChartType && SelectedSymbol is not null)
            .Do(type =>
            {
                var addressedSymbol = SelectedSymbol!.Normalized;
                _persistedChartTypes = SymbolChartTypeMap.WithEntry(
                    _persistedChartTypes,
                    addressedSymbol,
                    type);
            })
            .SelectMany(_ => Observable.FromAsync(PersistChartTypesAsync))
            .Subscribe(
                _ => { },
                ex => _logger.LogError(ex, "The chart type persistence pipeline stopped unexpectedly"));

        // Every type flip re-keys the drawn Heikin-Ashi series against the
        // CURRENT series: a selector change recomputes immediately (the bars
        // are the series' copy — the pure transform), a restore re-asserts
        // the value. The subject's publishes stay raw; only the assignment
        // hops.
        this.WhenAnyValue(x => x.ChartType)
            .Subscribe(
                _ => PublishHeikinAshi(Bars),
                ex => _logger.LogError(ex, "The chart type publish stream stopped unexpectedly"));

        // The persisted per-symbol types follow the settings service live
        // (the same pure-consumer contract the marks keep): one async read
        // seeds the map at construction and every changes-stream emission
        // re-parses it. The standing selection re-addresses on every fresh
        // document, so a seed racing the symbols bootstrap still delivers
        // regardless of which side lands first.
        Observable.FromAsync(() => _settings.GetAsync<string>(
                UiSettingsKeys.Section,
                UiSettingsKeys.ChartSymbolTypes,
                UiSettingsKeys.DefaultChartSymbolTypes))
            .Subscribe(
                ApplyPersistedChartTypesDocument,
                ex => _logger.LogError(ex, "Failed to read the chart type map"));

        _settings.Changes
            .Where(change => change.Section == UiSettingsKeys.Section &&
                             change.Key == UiSettingsKeys.ChartSymbolTypes)
            .Select(change => change.NewValue)
            .OfType<string>()
            .Subscribe(
                ApplyPersistedChartTypesDocument,
                ex => _logger.LogError(ex, "The chart type map stream stopped unexpectedly"));

        // The chart type is PER SYMBOL: every selection change resolves the
        // new workspace symbol's stored choice from the cache and applies it
        // under the gate — no entry keeps the standing type (a preference
        // map substitutes nothing, the persisted-value deferral rule).
        this.WhenAnyValue(x => x.SelectedSymbol)
            .Subscribe(
                symbol => ApplyPersistedChartType(symbol),
                ex => _logger.LogError(ex, "The chart type restore stream stopped unexpectedly"));

        // The Y-axis scale mode selector (T-090): the choice persists PER
        // SYMBOL, in the same document shape the chart types keep — the
        // whole map re-serializes on every flip under the workspace symbol.
        // The cache LEADS the pipeline (updated at issue time, in issuance
        // order), so a rapid flip burst never drops its second write; the
        // gate keeps the addressed restores (a symbol switch resolving its
        // stored mode) out of the persistence path — a restore is a read,
        // not an edit. A flip while no symbol stands cannot address the map
        // and stays transient (a pick without a workspace context is no
        // one's preference).
        this.WhenAnyValue(x => x.ScaleMode)
            .Skip(1)
            .Where(_ => !_isApplyingPersistedScaleMode && SelectedSymbol is not null)
            .Do(mode =>
            {
                var addressedSymbol = SelectedSymbol!.Normalized;
                _persistedScaleModes = SymbolScaleModeMap.WithEntry(
                    _persistedScaleModes,
                    addressedSymbol,
                    mode);
            })
            .SelectMany(_ => Observable.FromAsync(PersistScaleModesAsync))
            .Subscribe(
                _ => { },
                ex => _logger.LogError(ex, "The chart scale mode persistence pipeline stopped unexpectedly"));

        // The persisted per-symbol scale modes follow the settings service
        // live (the same pure-consumer contract the chart types keep): one
        // async read seeds the map at construction and every changes-stream
        // emission re-parses it. The standing selection re-addresses on
        // every fresh document, so a seed racing the symbols bootstrap
        // still delivers regardless of which side lands first.
        Observable.FromAsync(() => _settings.GetAsync<string>(
                UiSettingsKeys.Section,
                UiSettingsKeys.ChartSymbolScales,
                UiSettingsKeys.DefaultChartSymbolScales))
            .Subscribe(
                ApplyPersistedScaleModesDocument,
                ex => _logger.LogError(ex, "Failed to read the chart scale mode map"));

        _settings.Changes
            .Where(change => change.Section == UiSettingsKeys.Section &&
                             change.Key == UiSettingsKeys.ChartSymbolScales)
            .Select(change => change.NewValue)
            .OfType<string>()
            .Subscribe(
                ApplyPersistedScaleModesDocument,
                ex => _logger.LogError(ex, "The chart scale mode map stream stopped unexpectedly"));

        // The scale mode is PER SYMBOL: every selection change resolves the
        // new workspace symbol's stored mode from the cache and applies it
        // under the gate — no entry keeps the standing mode (a preference
        // map substitutes nothing, the persisted-value deferral rule).
        this.WhenAnyValue(x => x.SelectedSymbol)
            .Subscribe(
                symbol => ApplyPersistedScaleMode(symbol),
                ex => _logger.LogError(ex, "The chart scale mode restore stream stopped unexpectedly"));

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

        // The compare chip strip's main label (T-124): the derived view
        // mirrors the workspace selection (empty while none stands).
        this.WhenAnyValue(x => x.SelectedSymbol)
            .Subscribe(
                symbol => MainSeriesLabel = symbol?.Normalized ?? string.Empty,
                ex => _logger.LogError(ex, "The chart main series label stream stopped unexpectedly"));
    }

    #endregion

    #region Heikin-Ashi Series

    /// <summary>
    /// Compute the Heikin-Ashi series (T-089) for the given series and
    /// publish it; null while the selector stands on any other type or the
    /// series is empty (the chart's own no-data state owns an empty window).
    /// The transform is pure and marshal-free — the same publication
    /// discipline as the VWAP publishes — and runs once per delivered
    /// series, never per draw pass: every HA open depends on the previous
    /// HA bar, so the chain always starts at the series' head.
    /// </summary>
    private void PublishHeikinAshi(IReadOnlyList<Bar>? bars)
    {
        if (ChartType != ChartSeriesType.HeikinAshi || bars is not { Count: > 0 })
        {
            _heikinAshiPublishes.OnNext(null);
            return;
        }

        _heikinAshiPublishes.OnNext(HeikinAshi.Compute(bars));
    }

    #endregion

    #region Chart Type Persistence

    /// <summary>
    /// Re-key the chart types to a settings value (the seed read and every
    /// changes-stream emission route here): the document parses into the
    /// persisted cache and the standing selection re-addresses its choice.
    /// </summary>
    private void ApplyPersistedChartTypesDocument(string chartTypesJson)
    {
        _persistedChartTypes = SymbolChartTypeMap.Parse(chartTypesJson);
        ApplyPersistedChartType(SelectedSymbol);
    }

    /// <summary>
    /// Apply the workspace symbol's stored chart type, under the restore
    /// gate: a stored entry re-asserts the type (the persistence pipeline
    /// sees an equal address and skips the write — the same suppress shape
    /// the time frame restore keeps); a symbol without a stored entry
    /// keeps the standing type — unless the default template (T-202) stands
    /// on the primary, whose type seeds the entry-less pick. The seed is a
    /// READ: the map entry is never materialized, the persistence pipeline
    /// never learns of it, and a stored preference appears only when the
    /// user flips the selector themselves.
    /// </summary>
    private void ApplyPersistedChartType(Symbol? symbol)
    {
        if (symbol is null)
        {
            return;
        }

        if (!_persistedChartTypes.TryGetValue(symbol.Normalized, out var restored))
        {
            // No stored entry: the standing default template (T-202, the
            // primary only) seeds the entry-less pick's face. The seed is
            // a READ — the map entry is never materialized (a stored
            // preference appears only when the user flips the selector
            // themselves), so the persistence pipeline never learns of it.
            // Without a default (or on a slot) the standing type stays — a
            // preference map substitutes nothing silently.
            if (!_persistsFaceChoices || _templates?.DefaultTemplate is not { } seeded)
            {
                return;
            }

            restored = seeded.ChartType;
        }

        _isApplyingPersistedChartType = true;
        try
        {
            ChartType = restored;
        }
        finally
        {
            _isApplyingPersistedChartType = false;
        }
    }

    /// <summary>
    /// One serialized write of the whole per-symbol chart-type map (the
    /// cache already leads it — the issue-time update ran before this
    /// serialize). A failed write is logged and swallowed HERE (not at the
    /// stream): the pipeline keeps serving later flips instead of dying on
    /// the first fault, the issued choice stays cached and the file catches
    /// up on the next real difference (the degrade the overlay choices
    /// keep).
    /// </summary>
    private async Task PersistChartTypesAsync()
    {
        if (!_persistsFaceChoices)
        {
            // A grid slot's chart-face pick stays in-session (T-201): the
            // per-symbol type map is written whole, so a slot's write would
            // lose the primary's entries to the interleaving.
            return;
        }

        try
        {
            await _settings.SetAsync(
                UiSettingsKeys.Section,
                UiSettingsKeys.ChartSymbolTypes,
                SymbolChartTypeMap.Serialize(_persistedChartTypes));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist the chart type map");
        }
    }

    #endregion

    #region Scale Mode Persistence

    /// <summary>
    /// Re-key the scale modes to a settings value (the seed read and every
    /// changes-stream emission route here): the document parses into the
    /// persisted cache and the standing selection re-addresses its mode.
    /// </summary>
    private void ApplyPersistedScaleModesDocument(string scaleModesJson)
    {
        _persistedScaleModes = SymbolScaleModeMap.Parse(scaleModesJson);
        ApplyPersistedScaleMode(SelectedSymbol);
    }

    /// <summary>
    /// Apply the workspace symbol's stored scale mode, under the restore
    /// gate: a stored entry re-asserts the mode (the persistence pipeline
    /// sees an equal address and skips the write — the same suppress shape
    /// the chart type restore keeps); a symbol without a stored entry
    /// keeps the standing mode — unless the default template (T-202)
    /// stands on the primary, whose mode seeds the entry-less pick. The
    /// seed is a READ: the map entry is never materialized, the
    /// persistence pipeline never learns of it, and a stored preference
    /// appears only when the user flips the selector themselves.
    /// </summary>
    private void ApplyPersistedScaleMode(Symbol? symbol)
    {
        if (symbol is null)
        {
            return;
        }

        if (!_persistedScaleModes.TryGetValue(symbol.Normalized, out var restored))
        {
            if (!_persistsFaceChoices || _templates?.DefaultTemplate is not { } seeded)
            {
                return;
            }

            restored = seeded.ScaleMode;
        }

        _isApplyingPersistedScaleMode = true;
        try
        {
            ScaleMode = restored;
        }
        finally
        {
            _isApplyingPersistedScaleMode = false;
        }
    }

    /// <summary>
    /// One serialized write of the whole per-symbol scale-mode map (the
    /// cache already leads it — the issue-time update ran before this
    /// serialize). A failed write is logged and swallowed HERE (not at the
    /// stream): the pipeline keeps serving later flips instead of dying on
    /// the first fault, the issued mode stays cached and the file catches
    /// up on the next real difference (the degrade the chart types keep).
    /// </summary>
    private async Task PersistScaleModesAsync()
    {
        if (!_persistsFaceChoices)
        {
            // A grid slot's chart-face pick stays in-session (T-201): the
            // per-symbol scale map is written whole, so a slot's write would
            // lose the primary's entries to the interleaving.
            return;
        }

        try
        {
            await _settings.SetAsync(
                UiSettingsKeys.Section,
                UiSettingsKeys.ChartSymbolScales,
                SymbolScaleModeMap.Serialize(_persistedScaleModes));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist the chart scale mode map");
        }
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
        // The restore's task doubles as the prewarm's gate (T-226): the
        // persisted symbol's apply holds behind it, so the chart never loads
        // against the constructor-time default time frame. The assignment
        // lands at entry — an apply already posted (the constructor's own
        // bootstrap emission) still waits for the restore's END, and facts
        // that never call this method keep the completed default.
        var restore = RestorePersistedFaceAsync();
        _startupRestore = restore;
        await restore;
    }

    private async Task RestorePersistedFaceAsync()
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

        // The chart overlays restore with the same every-path-first shape
        // the clock-format restore carries (BUG-015's class): the reads
        // seed the persisted caches AND the properties, so the persistence
        // pipelines see an equal value and never rewrite the keys back.
        var restoredVwap = await _settings.GetAsync<bool>(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartShowVwap,
            UiSettingsKeys.DefaultChartShowVwap);
        _persistedShowVwap = restoredVwap;
        ShowVwap = restoredVwap;

        var restoredProfile = await _settings.GetAsync<bool>(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartShowVolumeProfile,
            UiSettingsKeys.DefaultChartShowVolumeProfile);
        _persistedShowVolumeProfile = restoredProfile;
        ShowVolumeProfile = restoredProfile;

        _logger.LogInformation(
            "Restored persisted chart overlays: vwap '{Vwap}', volume profile '{Profile}'",
            ShowVwap,
            ShowVolumeProfile);

        var persisted = await _settings.GetAsync<string>(
            UiSettingsKeys.Section,
            TimeFrameSettingsKey);

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

    #region Symbol Compare (T-124)

    /// <summary>
    /// One compare flow: the view hands a picked symbol back through the
    /// palette interaction (a null is a closed palette — no pick, not an
    /// error), and the non-null flow fetches the same interval from the
    /// standing adapter and appends the layer. The fetch reads the
    /// standing series' open span (plus a two-slot pad), so an aligned
    /// layer covers the loaded window it is drawn against.
    /// </summary>
    private async Task HandleCompareAsync(CancellationToken cancellationToken)
    {
        ErrorMessage = null;

        var picked = await PickCompareRequested.Handle(RxVoid.Default);
        if (picked is null)
        {
            _logger.LogInformation(
                "Chart panel: the compare pick ended on a closed palette");
            return;
        }

        await AddCompareAsync(picked);
    }

    /// <summary>
    /// Add one compare layer for the symbol (T-124): the fetch reads the
    /// SAME interval from the workspace's standing adapter over the
    /// standing series' span, the layer keys by the normalized name, and
    /// every refusal surfaces on the chart panel's own error line,
    /// localized — never a silent substitution and never a barred pick
    /// that pretends to succeed. A fetch racing an interval or an
    /// exchange change drops its result (the stale-fetch guard — the
    /// result no longer reads against the standing grid).
    /// </summary>
    /// <param name="symbol">The picked compare symbol.</param>
    public async Task AddCompareAsync(Symbol symbol)
    {
        ErrorMessage = null;

        if (_loadedAdapter is null || _loadedTarget is not { } target || Bars.Count == 0)
        {
            ErrorMessage = _localization.GetString(CompareNoSeriesKey);
            return;
        }

        var label = symbol.Normalized;
        if (string.Equals(label, target.Symbol.Normalized, StringComparison.Ordinal))
        {
            ErrorMessage = _localization.GetString(CompareSelfKey);
            return;
        }

        if (_compareLayers.Any(layer => string.Equals(layer.Label, label, StringComparison.Ordinal)))
        {
            ErrorMessage = _localization.GetString(CompareDuplicateKey);
            return;
        }

        if (_compareLayers.Count >= ChartCompareSeries.MaxCompareLayers)
        {
            ErrorMessage = _localization.GetString(CompareCapKey, ChartCompareSeries.MaxCompareLayers);
            return;
        }

        // The fetch stamps its own frame at its start: an interval or an
        // adapter change during the fetch invalidates the result (the
        // re-check below), the same stale-drop shape the repair chain
        // keeps. The window's head anchors the span; the limit carries a
        // small pad — the feed may deliver bars before the boundary, and
        // the alignment tolerates extra elders.
        var adapter = _loadedAdapter;
        var timeFrame = target.TimeFrame;
        var from = Bars[0].Time;
        var limit = Bars.Count + CompareFetchBarPad;

        IReadOnlyList<Bar> fetched;
        try
        {
            fetched = await adapter.GetBarsAsync(symbol, timeFrame, from, limit: limit);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                ex,
                "Chart compare fetch failed for {Symbol} {TimeFrame} on {Exchange}",
                label,
                timeFrame,
                adapter.Id);
            ErrorMessage = _localization.GetString(CompareFetchFailedKey, ex.Message);
            return;
        }

        if (!ReferenceEquals(adapter, _loadedAdapter) ||
            timeFrame != target.TimeFrame ||
            Bars.Count == 0)
        {
            _logger.LogInformation(
                "Chart compare fetch dropped: the series rebased during the fetch ({Symbol})",
                label);
            return;
        }

        if (fetched.Count == 0)
        {
            ErrorMessage = _localization.GetString(CompareFetchEmptyKey);
            return;
        }

        CompareLayers = [.. _compareLayers, new ChartCompareLayer(label, fetched)];
        _logger.LogInformation(
            "Chart compare added: {Symbol} {TimeFrame} on {Exchange} ({Count} candles)",
            label,
            timeFrame,
            adapter.Id,
            fetched.Count);
    }

    /// <summary>
    /// Remove one compare layer by its chip label (the normalized name,
    /// T-124): the currency of the chart's chip-press removal stream the
    /// view routes here. An unknown label removes nothing (the strip and
    /// the cache re-publish per frame anyway).
    /// </summary>
    public void RemoveCompare(string label)
    {
        var kept = _compareLayers
            .Where(layer => !string.Equals(layer.Label, label, StringComparison.Ordinal))
            .ToArray();
        if (kept.Length == _compareLayers.Count)
        {
            _logger.LogInformation(
                "Chart compare removal ignored: the label holds no standing layer ({Symbol})",
                label);
            return;
        }

        CompareLayers = kept;
        _logger.LogInformation("Chart compare removed: {Symbol}", label);
    }

    #endregion

    #region Drawing Actions (T-091)

    /// <summary>
    /// Mirrors the control's selection (T-091): the pressed drawing's
    /// identity takes the standing value, an empty-plot press or an Escape
    /// clears it. Pure view-boundary state — nothing here touches the
    /// store (selection is transient, the layer's is not).
    /// </summary>
    public void SelectDrawing(Guid? drawingId)
    {
        SelectedDrawingId = drawingId;
    }

    /// <summary>
    /// Commits a fresh placement (T-091): the control's placed geometry —
    /// already validated there (a degenerate release stands down before
    /// any event) — joins the standing layer and the whole layer saves.
    /// The store's answer carries the naming for the fresh drawing, which
    /// republishes as the layer, so the VM's memory equals the store's
    /// identity state. A store fault keeps the in-memory layer visible
    /// (the annotation stays drawn; the store stays as it was and the
    /// next save of the key rewrites everything from this state).
    /// A text annotation (T-200) prompts the view first: the answer
    /// becomes the annotation's string, a cancel or an empty answer
    /// places nothing (an annotation never persists unlabelled).
    /// </summary>
    public async Task CommitDrawingAsync(ChartDrawingEventArgs request)
    {
        if (_loadedTarget is not { } target)
        {
            _logger.LogWarning("Drawing commit ignored: no loaded chart to anchor to");
            return;
        }

        try
        {
            ChartDrawing.Validate(request.Kind, request.Anchors);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Drawing commit refused: invalid {Kind} geometry", request.Kind);
            return;
        }

        string? annotationText = null;
        if (request.Kind == ChartDrawingKind.Text)
        {
            annotationText = (await TextAnnotationRequested.Handle(RxVoid.Default))?.Trim();
            if (string.IsNullOrEmpty(annotationText))
            {
                return;
            }
        }

        var layer = _drawings.Append(new ChartDrawing
        {
            Kind = request.Kind,
            Anchors = request.Anchors,
            Text = annotationText,
        });

        await SaveDrawingLayerAsync(target, layer);
    }

    /// <summary>
    /// Settles a drag/edit (T-091): the named drawing's anchors replace
    /// with the event's (the settle guard validated them control-side),
    /// the whole layer saves and the settled layer republishes. An event
    /// naming no standing drawing drops silently — a stale route after a
    /// rebase, where the layer it named is already gone.
    /// </summary>
    public async Task EditDrawingAsync(ChartDrawingEventArgs request)
    {
        if (request.DrawingId is not { } drawingId || _loadedTarget is not { } target)
        {
            return;
        }

        var listing = IndexOfDrawing(_drawings, drawingId);
        if (listing < 0)
        {
            return;
        }

        var edited = _drawings.ToList();
        edited[listing] = request.Kind == edited[listing].Kind
            ? edited[listing] with { Anchors = request.Anchors }
            : edited[listing] with { Kind = request.Kind, Anchors = request.Anchors };

        await SaveDrawingLayerAsync(target, edited);
    }

    /// <summary>
    /// Removes the named drawing (T-091): the rest of the layer saves and
    /// republishes; the selection clears when it pointed at the removed
    /// drawing. An absent name drops silently — a stale Delete after the
    /// rebase already replaced the layer.
    /// </summary>
    public async Task DeleteDrawingAsync(Guid drawingId)
    {
        if (_loadedTarget is not { } target || IndexOfDrawing(_drawings, drawingId) < 0)
        {
            return;
        }

        var kept = _drawings.Where(drawing => drawing.Id != drawingId).ToArray();
        if (SelectedDrawingId == drawingId)
        {
            SelectedDrawingId = null;
        }

        await SaveDrawingLayerAsync(target, kept);
    }

    /// <summary>The layer position of the named drawing, -1 when absent.</summary>
    private static int IndexOfDrawing(IReadOnlyList<ChartDrawing> layer, Guid drawingId)
    {
        for (var i = 0; i < layer.Count; i++)
        {
            if (layer[i].Id == drawingId)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The commit/edit/delete shared write (T-091): the pushed layer
    /// publishes FIRST (the drawing visibly stands the moment it was
    /// placed, edited or removed), then the whole layer saves through the
    /// store and the store's answer (the naming reality) republishes as
    /// the standing layer, so the VM's memory and the store never drift on
    /// identities. A store fault keeps the pushed state visible in memory
    /// and logs the warning: the chart's annotations are a convenience,
    /// their path never becomes a crash.
    /// </summary>
    private async Task SaveDrawingLayerAsync(
        (Symbol Symbol, TimeFrame TimeFrame, string ExchangeId) target,
        IEnumerable<ChartDrawing> layer)
    {
        var pushed = layer.ToArray();
        Drawings = pushed;

        IReadOnlyList<ChartDrawing> saved;
        try
        {
            saved = await _drawingStore.SaveAsync(
                target.ExchangeId,
                target.Symbol.Normalized,
                target.TimeFrame,
                pushed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Chart drawings save failed for {Symbol} {TimeFrame} on {ExchangeId}: the pushed layer stands in memory",
                target.Symbol,
                target.TimeFrame,
                target.ExchangeId);
            return;
        }

        Drawings = saved;
        _logger.LogInformation(
            "Chart drawings saved: {Count} drawing(s) for {Symbol} {TimeFrame} on {ExchangeId}",
            saved.Count,
            target.Symbol,
            target.TimeFrame,
            target.ExchangeId);
    }

    #endregion Drawing Actions (T-091)

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
    /// the single UI-thread hop. The persisted pane heights (T-199) merge
    /// into the lines by name on the way out — the pipeline carries the
    /// descriptors' pane declaration, the persisted map carries the
    /// splitter's settled geometry.
    /// </summary>
    private void PublishIndicatorLines(IReadOnlyList<Bar> bars)
    {
        var lines = ChartIndicatorPipeline.Compute(
            bars,
            _persistedIndicatorConfig,
            _indicatorCatalog,
            _logger);

        // The funding pane (T-240): the chart's own synthesized line rides
        // the same pipeline tail — appended after the catalog's plugin lines,
        // merged with the persisted heights by the SAME Name key (the pane
        // splitter's settled geometry persists across sessions). A stale
        // alignment (its series no longer standing) contributes nothing.
        if (_fundingSeries is { } funding && funding.Count == bars.Count)
        {
            lines = [.. lines, new ChartIndicatorLine(
                ChartFundingSeries.Name,
                funding,
                IndicatorPaneKind.Oscillator,
                LabelKey: ChartFundingSeries.LabelKey)];
        }

        // The OI pane (T-241): the chart's own synthesized line rides the
        // same tail after the funding one, merged with the persisted heights
        // by the SAME Name key. A stale alignment (its series no longer
        // standing) contributes nothing.
        if (_oiSeries is { } openInterest && openInterest.Count == bars.Count)
        {
            lines = [.. lines, new ChartIndicatorLine(
                ChartOpenInterestSeries.Name,
                openInterest,
                IndicatorPaneKind.Oscillator,
                LabelKey: ChartOpenInterestSeries.LabelKey)];
        }

        var heights = _persistedPaneHeights;
        if (heights.Count > 0)
        {
            ChartIndicatorLine[] merged = [.. lines.Select(line => heights.TryGetValue(line.Name, out var height)
                ? line with { PaneHeight = height }
                : line)];
            lines = merged;
        }

        _indicatorPublishes.OnNext(lines);
    }

    /// <summary>
    /// Re-key the pane heights to a settings value (T-199): the map clamps
    /// into the pane layout's usable band at apply time (a stored truth is
    /// never an out-of-band height — the clamp-on-restore discipline) and
    /// the current series republishes so a live window re-applies without a
    /// restart.
    /// </summary>
    private void ApplyPaneHeights(string paneHeightsJson)
    {
        // The persistence write echoes back through the changes stream: an
        // equal JSON re-applies nothing (the write's own value already
        // leads the cache).
        if (string.Equals(paneHeightsJson, _appliedPaneHeightsJson, StringComparison.Ordinal))
        {
            return;
        }

        _appliedPaneHeightsJson = paneHeightsJson;
        _persistedPaneHeights = ClampPaneHeights(ChartIndicatorPaneHeights.Parse(paneHeightsJson));
        PublishIndicatorLines(Bars);
    }

    /// <summary>Clamp a parsed map into the pane layout's usable band.</summary>
    private static IReadOnlyDictionary<string, double> ClampPaneHeights(
        IReadOnlyDictionary<string, double> heights) =>
        new Dictionary<string, double>(
            heights.ToDictionary(
                row => row.Key,
                row => Math.Clamp(row.Value, ChartPaneLayout.PaneMinHeightPx, ChartPaneLayout.PaneMaxHeightPx)),
            StringComparer.Ordinal);

    /// <summary>
    /// A settled splitter drag (T-199): the cache leads the write (the
    /// issue-time discipline the overlay toggles keep — a settle is already
    /// one event per drag, no throttle needed), the current series
    /// republishes with the settled height, and the sibling settings key
    /// stores the compact map behind the issued value.
    /// </summary>
    public void SettleIndicatorPaneHeight(string name, double height)
    {
        var clamped = Math.Clamp(height, ChartPaneLayout.PaneMinHeightPx, ChartPaneLayout.PaneMaxHeightPx);
        var next = new Dictionary<string, double>(_persistedPaneHeights, StringComparer.Ordinal)
        {
            [name] = clamped,
        };

        _persistedPaneHeights = next;
        _appliedPaneHeightsJson = ChartIndicatorPaneHeights.Write(next);
        PublishIndicatorLines(Bars);

        if (!_persistsFaceChoices)
        {
            // A grid slot's pane-height pick stays in-session (T-201): the
            // key is shared and written whole, the primary's map must not
            // lose its entries to a slot's settle.
            return;
        }

        Observable
            .FromAsync(() => _settings.SetAsync(
                UiSettingsKeys.Section,
                UiSettingsKeys.ChartIndicatorPaneHeights,
                _appliedPaneHeightsJson))
            .Subscribe(
                _ => { },
                ex => _logger.LogError(ex, "Failed to persist the chart indicator pane heights"));
    }

    #endregion

    #region Funding Pane (T-240)

    /// <summary>
    /// Kick the funding pane's load (T-240): the fresh kick takes the next
    /// epoch and the standing identity, and the fetch runs fire-and-forget
    /// through the service's cache-first front — a venue without the
    /// funding-history capability answers the honest no-pane without a
    /// network round-trip. The load publishes through the marshalled
    /// subject; a superseded kick drops at apply. Nothing aligns here: the
    /// alignment reads the live bar collection and rides the UI thread, so
    /// the window's start is captured from the standing head at kick time.
    /// </summary>
    /// <param name="symbol">The standing series' symbol.</param>
    /// <param name="exchangeId">The standing series' exchange.</param>
    private void KickFundingLoad(Symbol symbol, string exchangeId)
    {
        if (Bars.Count == 0)
        {
            // No series to align onto (no kick site can produce this; the
            // guard is the honest shape) — nothing fetches.
            return;
        }

        var from = Bars[0].Time;
        var epoch = ++_fundingKickEpoch;
        _fundingTarget = (symbol, exchangeId);

        Observable
            .FromAsync(() => _fundingHistory.GetAsync(symbol, from, ct: CancellationToken.None))
            .Subscribe(
                points => _fundingPublishes.OnNext(new FundingPaneLoad(symbol, exchangeId, epoch, points)),
                error => _logger.LogWarning(
                    error,
                    "The funding-history load for {Symbol} on {Exchange} failed; the pane waits for the next kick",
                    symbol.Normalized,
                    exchangeId));
    }

    /// <summary>
    /// Apply one funding pane load (T-240) on the UI thread (the subject's
    /// marshalled subscription): a stale load — a kick superseded by a newer
    /// one or an identity no longer standing — drops silently; the standing
    /// load aligns against the live bar collection and republishes the
    /// overlays. A null emission (the service's honest no-capability answer)
    /// or an empty page clears the standing series: the overlays republish
    /// without the funding line, the honest absence.
    /// </summary>
    /// <param name="load">The resolved load, or nothing for the honest no-pane.</param>
    private void ApplyFundingPaneLoad(FundingPaneLoad? load)
    {
        if (load is null || _fundingTarget != (load.Symbol, load.ExchangeId) || load.Epoch != _fundingKickEpoch)
        {
            // A stale load drops: a newer kick owns the pane (a rebase
            // cleared the identity before its own kick armed).
            return;
        }

        _fundingSeries = load.Points is { Count: > 0 } points
            ? FundingSeriesAlignment.Align(Bars, points)
            : null;
        PublishIndicatorLines(Bars);
    }

    #endregion Funding Pane (T-240)

    #region Open-Interest Pane (T-241)

    /// <summary>
    /// Kick the OI pane's load (T-241): the fresh kick takes the next epoch
    /// and the standing identity (the bar interval rides it — the bucket
    /// size keyed the fetch), and the fetch runs fire-and-forget through
    /// the service's cache-first front — a venue without the OI-history
    /// capability answers the honest no-pane without a network round-trip.
    /// The load publishes through the marshalled subject; a superseded kick
    /// drops at apply. Nothing aligns here: the alignment reads the live
    /// bar collection and rides the UI thread, so the window's start is
    /// captured from the standing head at kick time.
    /// </summary>
    /// <param name="symbol">The standing series' symbol.</param>
    /// <param name="exchangeId">The standing series' exchange.</param>
    /// <param name="interval">The standing series' bar interval.</param>
    private void KickOpenInterestLoad(Symbol symbol, string exchangeId, TimeFrame interval)
    {
        if (Bars.Count == 0)
        {
            // No series to align onto (no kick site can produce this; the
            // guard is the honest shape) — nothing fetches.
            return;
        }

        var from = Bars[0].Time;
        var epoch = ++_oiKickEpoch;
        _oiTarget = (symbol, exchangeId, interval);

        Observable
            .FromAsync(() => _openInterestHistory.GetAsync(symbol, interval, from, ct: CancellationToken.None))
            .Subscribe(
                points => _oiPublishes.OnNext(new OpenInterestPaneLoad(symbol, exchangeId, interval, epoch, points)),
                error => _logger.LogWarning(
                    error,
                    "The open-interest-history load for {Symbol} on {Exchange} failed; the pane waits for the next kick",
                    symbol.Normalized,
                    exchangeId));

        // The liquidation levels (T-243) ride the same kicks: the venue's
        // public bracket table is the estimate's second input — cache-first
        // through the front, its own epoch (the table is interval-free, so
        // its identity carries the symbol and the exchange only).
        KickLiquidationBracketsLoad(symbol, exchangeId);
    }

    /// <summary>
    /// Apply one OI pane load (T-241) on the UI thread (the subject's
    /// marshalled subscription): a stale load — a kick superseded by a
    /// newer one or an identity no longer standing — drops silently; the
    /// standing load aligns against the live bar collection and republishes
    /// the overlays. A null emission (the service's honest no-capability
    /// answer) or an empty page clears the standing series: the overlays
    /// republish without the OI line, the honest absence.
    /// </summary>
    /// <param name="load">The resolved load, or nothing for the honest no-pane.</param>
    private void ApplyOpenInterestPaneLoad(OpenInterestPaneLoad? load)
    {
        if (load is null ||
            _oiTarget != (load.Symbol, load.ExchangeId, load.Interval) ||
            load.Epoch != _oiKickEpoch)
        {
            // A stale load drops: a newer kick owns the pane (a rebase
            // cleared the identity before its own kick armed).
            return;
        }

        _oiSeries = load.Points is { Count: > 0 } points
            ? OISeriesAlignment.Align(Bars, points)
            : null;
        PublishIndicatorLines(Bars);
        PublishLiquidationLevels();
    }

    #endregion Open-Interest Pane (T-241)

    #region Liquidation Levels (T-243)

    /// <summary>
    /// Kick the liquidation levels' bracket load (T-243): the fresh kick
    /// takes the next epoch and the standing identity, and the fetch runs
    /// fire-and-forget through the service's cache-first front — a venue
    /// without the bracket capability (or a spot symbol) answers the honest
    /// no-table without a network round-trip. The load publishes through
    /// the marshalled subject; a superseded kick drops at apply.
    /// </summary>
    /// <param name="symbol">The standing series' symbol.</param>
    /// <param name="exchangeId">The standing series' exchange.</param>
    private void KickLiquidationBracketsLoad(Symbol symbol, string exchangeId)
    {
        var epoch = ++_liqKickEpoch;
        _liqTarget = (symbol, exchangeId);

        Observable
            .FromAsync(() => _leverageBrackets.GetAsync(symbol, CancellationToken.None))
            .Subscribe(
                brackets => _liqLevelsPublishes.OnNext(new LiquidationLevelsLoad(symbol, exchangeId, epoch, brackets)),
                error => _logger.LogWarning(
                    error,
                    "The leverage-brackets load for {Symbol} on {Exchange} failed; the estimate waits for the next kick",
                    symbol.Normalized,
                    exchangeId));
    }

    /// <summary>
    /// Apply one liquidation-levels load (T-243) on the UI thread (the
    /// subject's marshalled subscription): a stale load — a kick superseded
    /// by a newer one or an identity no longer standing — drops silently;
    /// the standing table (null = the honest no-table) republishes the
    /// estimate against the live bar collection and the standing OI series.
    /// </summary>
    /// <param name="load">The resolved load, or nothing for the honest no-table.</param>
    private void ApplyLiquidationLevelsLoad(LiquidationLevelsLoad? load)
    {
        if (load is null || _liqTarget != (load.Symbol, load.ExchangeId) || load.Epoch != _liqKickEpoch)
        {
            // A stale load drops: a newer kick owns the estimate (a rebase
            // cleared the identity before its own kick armed).
            return;
        }

        _liqBrackets = load.Brackets;
        _liqBracketsSettled = true;
        PublishLiquidationLevels();
    }

    /// <summary>
    /// Compute the liquidation-levels estimate (T-243) from the standing
    /// inputs and publish it. The estimate needs BOTH inputs standing: the
    /// OI series aligned with the live bar collection (the chart's own
    /// deltas — a stale alignment holds, the re-kick re-publishes) and the
    /// bracket table settled for the standing identity (a fetch still in
    /// flight holds — never a flashed wrong note). Either input absent is
    /// the honest null (the chart draws nothing); the OI standing with the
    /// venue's explicit no-table is the estimator's own honesty state, the
    /// note the chart carries.
    /// </summary>
    private void PublishLiquidationLevels()
    {
        if (_oiSeries is not { } openInterest || openInterest.Count != Bars.Count)
        {
            // No OI pane stands for the live collection (spot, no
            // capability, the load still riding, or a stale alignment a
            // prepend grew out) — the honest absence.
            _liqOverlayPublishes.OnNext(null);
            return;
        }

        if (!_liqBracketsSettled)
        {
            // The bracket load still rides: hold — the standing overlay
            // keeps serving until both inputs settle (the bands are price
            // geometry, a held overlay stays honest through a pan).
            return;
        }

        _liqOverlayPublishes.OnNext(LiquidationLevelEstimator.Compute(Bars, _liqBrackets, openInterest));
    }

    #endregion Liquidation Levels (T-243)

    #region Vwap Overlay

    /// <summary>
    /// Compute the session VWAP overlay (T-093) for the given series and
    /// publish it; null while the overlay is switched off (the chart draws
    /// nothing) or the series is empty (the chart's own no-data state owns
    /// an empty window). The compute is pure and marshal-free — the same
    /// publication discipline as the indicator publishes.
    /// </summary>
    private void PublishVwapOverlay(IReadOnlyList<Bar>? bars)
    {
        if (!ShowVwap || bars is not { Count: > 0 })
        {
            _vwapPublishes.OnNext(null);
            return;
        }

        _vwapPublishes.OnNext(VwapOverlay.Compute(bars));
    }

    /// <summary>
    /// The pure signal-marker mapping the publish uses (T-112): the
    /// service's stance registry maps onto the loaded bars by open time,
    /// one marker per matching bar — a candle the registry knows and the
    /// series displays anchors a marker exactly at itself; every other bar
    /// stays clean. Neutral is an honest non-reading and never markers.
    /// Internal static so the golden tests pin the mapping without a VM
    /// instance (the <see cref="ChartViewModel.Preselect"/> precedent).
    /// </summary>
    /// <param name="bars">The displayed ascending bar series.</param>
    /// <param name="stances">The host registry's stances keyed by the candles' open times.</param>
    /// <returns>The markers, in bar order.</returns>
    internal static IReadOnlyList<SignalMarker> ComposeSignalMarkers(
        IReadOnlyList<Bar> bars,
        IReadOnlyDictionary<DateTimeOffset, StrategyStance> stances)
    {
        var markers = new List<SignalMarker>(stances.Count);
        foreach (var bar in bars)
        {
            if (stances.TryGetValue(bar.Time, out var stance) &&
                stance != StrategyStance.Neutral)
            {
                markers.Add(new SignalMarker(
                    bar,
                    stance == StrategyStance.Long ? SignalMarkerRole.Long : SignalMarkerRole.Flat));
            }
        }

        return markers;
    }

    /// <summary>
    /// Resolve the strategy-signal markers (T-112) against the current
    /// loaded series and publish them: the service's stance registry for
    /// the loaded (symbol, time frame) series maps onto the loaded bars by
    /// open time, one marker per matching bar — a candle the registry
    /// knows and the series displays anchors a marker exactly at itself;
    /// every other bar stays clean. Called from the same handlers the
    /// other overlays re-publish from (delivery, prepend, repair) and from
    /// the signal stream (a new stance re-inks once): the assignment is
    /// the single UI-thread hop, the same publication discipline the
    /// indicator and VWAP publishes keep.
    /// </summary>
    private void PublishSignalMarkers()
    {
        var bars = Bars;
        if (bars.Count == 0 || _loadedTarget is not { } target)
        {
            SignalMarkers = [];
            return;
        }

        var stances = _signalService.StancesFor(target.Symbol, target.TimeFrame);
        SignalMarkers = ComposeSignalMarkers(bars, stances);
    }

    /// <summary>
    /// One serialized persistence write for a chart overlay choice. A failed
    /// write is logged and swallowed HERE (not at the stream): the pipeline
    /// keeps serving later flips instead of dying on the first fault, the
    /// issued choice stays cached and the file catches up with the property
    /// on the next real difference (the same degrade the AI panel's
    /// visibility write keeps).
    /// </summary>
    /// <param name="key">The settings key of the overlay choice
    /// (<see cref="UiSettingsKeys.ChartShowVwap"/>,
    /// <see cref="UiSettingsKeys.ChartShowVolumeProfile"/>).</param>
    /// <param name="value">The issued choice to persist.</param>
    /// <returns>The completed write.</returns>
    private async Task SetOverlayChoiceAsync(string key, bool value)
    {
        if (!_persistsFaceChoices)
        {
            // A grid slot's overlay pick stays in-session (T-201): the key
            // is shared, the primary's choice must not be overwritten by a
            // slot's toggle.
            return;
        }

        try
        {
            await _settings.SetAsync(UiSettingsKeys.Section, key, value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist the chart overlay choice '{Key}'", key);
        }
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// Fetch the symbol list through the shared
    /// <see cref="SymbolListProvider"/> (one <c>exchangeInfo</c> fetch per
    /// exchange per session serves the chart combo and the order dialog
    /// autocomplete alike; the provider warms every registered exchange at
    /// startup and keeps each list for the session, so this bootstrap
    /// usually answers from the ready cache). The FULL parsed list
    /// returns — the marks
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

        // The prewarm (T-226): the persisted workspace preference reads
        // BEFORE the symbols fetch, so the chart renders from the candle
        // cache without waiting for the instrument list. The read is cheap
        // and local (the settings service answers from memory); the fetch
        // baseline becomes the read's parsed symbol, which keeps the
        // prewarm's own apply out of the pick detection (BUG-016) — a
        // mid-flight USER pick still supersedes it exactly as before.
        Observable
            .FromAsync(() => ReadPersistedSelectionAsync(epoch))
            .Subscribe(
                persisted =>
                {
                    StartSymbolsFetch(trigger, epoch, exchangeName, persisted ?? selectionAtStart);

                    // The apply rides the UI marshal separately: the
                    // property's raises must never run off-owner, while the
                    // fetch kick keeps the pre-existing synchronous start
                    // shape (the facts pin it — no pump needed).
                    Observable
                        .Return(persisted)
                        .Where(static persisted => persisted is not null)
                        .ObserveOnUiThread()
                        .Subscribe(
                            persisted => ApplyPersistedSelection(persisted!, epoch, exchangeName),
                            ex => _logger.LogWarning(
                                ex,
                                "The chart prewarm apply failed; the fetch delivery decides the selection"));
                },
                ex =>
                {
                    _logger.LogWarning(
                        ex,
                        "The chart prewarm read failed; the bootstrap fetch starts from the standing selection");
                    StartSymbolsFetch(trigger, epoch, exchangeName, selectionAtStart);
                });
    }

    /// <summary>
    /// The prewarm's read half: the persisted workspace preference for THIS
    /// instance's settings key, parsed in the pure form the delivery shares.
    /// A null (no stored value, an unparsable one) is the honest "nothing to
    /// prewarm" — the fetch then starts from the standing selection exactly
    /// as before. The epoch guard drops a read whose bootstrap a newer
    /// emission already superseded.
    /// </summary>
    private async Task<Symbol?> ReadPersistedSelectionAsync(int epoch)
    {
        if (Volatile.Read(ref _symbolBootstrapEpoch) != epoch)
        {
            return null;
        }

        var stored = await _settings.GetAsync<string>(SymbolSettingsSection, SymbolSettingsKey);
        return TryParseStoredSymbol(stored?.Trim());
    }

    /// <summary>
    /// The prewarm's apply half (the UI thread): the persisted symbol takes
    /// the selection under the bootstrap gate — a restore is not an edit,
    /// the persistence chain skips it — and the bars pipeline's cache-first
    /// load serves the chart while the symbols fetch is still in flight.
    /// The epoch guard drops an apply whose bootstrap a newer emission
    /// already superseded (the delivery's own rule), an equal selection
    /// re-applies nothing, and the startup restore's gate holds the apply
    /// until the persisted time frame stands — the persisted symbol never
    /// loads against the constructor-time default frame.
    /// </summary>
    private void ApplyPersistedSelection(Symbol persisted, int epoch, string exchangeName)
    {
        if (Volatile.Read(ref _symbolBootstrapEpoch) != epoch)
        {
            return;
        }

        Observable
            .FromAsync(() => _startupRestore)
            .ObserveOnUiThread()
            .Subscribe(
                _ => ApplyPersistedSelectionCore(persisted, exchangeName),
                ex => _logger.LogWarning(
                    ex,
                    "The chart prewarm apply failed; the fetch delivery decides the selection"));
    }

    private void ApplyPersistedSelectionCore(Symbol persisted, string exchangeName)
    {
        if (Equals(SelectedSymbol, persisted))
        {
            return;
        }

        _isApplyingBootstrapSelection = true;
        try
        {
            SelectedSymbol = persisted;
        }
        finally
        {
            _isApplyingBootstrapSelection = false;
        }

        _logger.LogInformation(
            "Chart prewarmed: the persisted {Symbol} renders from the candle cache while the {Exchange} symbols fetch is in flight",
            persisted,
            exchangeName);
    }

    /// <summary>
    /// The symbols fetch half of the bootstrap (the pre-existing machine):
    /// the persisted preference and the marks load, the provider answers
    /// from its cache (or fetches), and the delivery applies on the UI
    /// thread under the claimed epoch.
    /// </summary>
    private void StartSymbolsFetch(
        IMarketDataAdapter trigger,
        int epoch,
        string exchangeName,
        Symbol? selectionAtStart)
    {
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
    /// <see cref="Livery.Extensions.ObservableExtensions.ObserveOnUiThread"/>
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
            SymbolSettingsSection,
            SymbolSettingsKey);

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
    /// the exact time frame of the triggering change: the cached page (if
    /// any) rides FIRST and the network refresh follows behind it, so a
    /// fresh start renders instantly from the cache (T-085). A failure
    /// ends this selection's data (logged, never a default value) but must
    /// not terminate the switching machine for later selections.
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

        return Observable.Concat(
            CachedBars(symbol, timeFrame, adapter),
            FetchBars(symbol, timeFrame, adapter));
    }

    /// <summary>
    /// The full load's pair (T-091): the bars pipeline joined with the
    /// same key's persisted drawings layer, so a delivered page is always
    /// complete. The join keeps the bars pipeline's own sequencing (cache
    /// behind it) untouched — the drawings only ride along.
    /// </summary>
    private IObservable<ChartSeriesLoad> LoadSeries(
        Symbol symbol,
        TimeFrame timeFrame,
        IMarketDataAdapter adapter)
    {
        return LoadBars(symbol, timeFrame, adapter)
            .CombineLatest(
                LoadDrawings(symbol, timeFrame, adapter),
                static (bars, drawings) => new ChartSeriesLoad(bars, drawings));
    }

    /// <summary>
    /// The key's persisted drawings (T-091), the load half of the drawings
    /// pipeline: one async read through the store, and every fault stands
    /// down honestly — a cancellation delivers nothing (the superseded
    /// load is dead by construction), any other fault logs a warning and
    /// delivers an EMPTY layer, never breaking the bars load. The chart
    /// is a convenience surface; trading never reads these annotations.
    /// </summary>
    private IObservable<IReadOnlyList<ChartDrawing>> LoadDrawings(
        Symbol symbol,
        TimeFrame timeFrame,
        IMarketDataAdapter adapter)
    {
        return Observable
            .FromAsync(ct => _drawingStore.LoadAsync(adapter.Id, symbol.Normalized, timeFrame, ct))
            .Catch<IReadOnlyList<ChartDrawing>, OperationCanceledException>(
                _ => Observable.Empty<IReadOnlyList<ChartDrawing>>())
            .Catch<IReadOnlyList<ChartDrawing>, Exception>(ex =>
            {
                _logger.LogWarning(
                    ex,
                    "Chart drawings load failed for {Symbol} {TimeFrame} on {Exchange}: the layer stands empty",
                    symbol,
                    timeFrame,
                    adapter.Name);

                return Observable.Return<IReadOnlyList<ChartDrawing>>([]);
            });
    }

    /// <summary>
    /// The cached window for the selection (T-085): the newest
    /// <see cref="InitialBarCount"/>+1 bars the cache holds for this
    /// (adapter, symbol, interval) key, delivered BEFORE the fetch so a
    /// fresh start renders instantly. An empty or faulted read delivers
    /// nothing — the fetch page then stands alone. The page may end at the
    /// previous session's edge: the still-forming candle shown until the
    /// refresh lands is the honest cache picture, never a fabricated bar.
    /// </summary>
    private IObservable<IReadOnlyList<Bar>> CachedBars(
        Symbol symbol,
        TimeFrame timeFrame,
        IMarketDataAdapter adapter) => Observable
        .FromAsync(token => _candleCache.GetLatestAsync(
            adapter.Id,
            symbol,
            timeFrame,
            InitialBarCount + 1,
            token))
        // A cancelled read is the normal outcome of Switch dropping the
        // previous selection or exchange: keep it silent (real failures
        // log below).
        .Catch<IReadOnlyList<Bar>, OperationCanceledException>(_ =>
            Observable.Empty<IReadOnlyList<Bar>>())
        .Where(static bars => bars.Count > 0)
        .Catch<IReadOnlyList<Bar>, Exception>(ex =>
        {
            _logger.LogWarning(
                ex,
                "Candle cache read failed for {Symbol} {TimeFrame} on {Exchange}; the fetch page stands alone",
                symbol.Normalized,
                timeFrame,
                adapter.Id);
            return Observable.Empty<IReadOnlyList<Bar>>();
        });

    /// <summary>
    /// The network page for the selection, riding WRITE-THROUGH (T-085):
    /// the adapter-served set persists before the page delivers, so the
    /// next start serves these bars from the cache even if this session
    /// dies right after.
    /// </summary>
    private IObservable<IReadOnlyList<Bar>> FetchBars(
        Symbol symbol,
        TimeFrame timeFrame,
        IMarketDataAdapter adapter) => Observable
        .FromAsync(async token =>
        {
            // From = the frame's own bar depth walked back from now: every time
            // frame loads its full history depth. The limit is one over the bar
            // count because the request window [start, now] contains count+1
            // open times when start lands on a bar boundary (the current,
            // still-forming candle included), and the exchange answers a
            // saturated window with its OLDEST bars — a saturated limit would
            // cut exactly the fresh edge.
            var bars = await adapter.GetBarsAsync(
                symbol,
                timeFrame,
                HistoryStart(DateTimeOffset.UtcNow, timeFrame, InitialBarCount),
                limit: InitialBarCount + 1,
                ct: token);

            if (bars.Count > 0)
            {
                await WriteThroughBarsAsync(adapter, symbol, timeFrame, bars, token);
            }

            return bars;
        })
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

    /// <summary>
    /// One best-effort write-through: a cache fault is logged and never
    /// breaks the load (the refresh page still delivers — the cache is
    /// bookkeeping, not a source of truth), while a cancellation
    /// propagates (the Switch dropped this load; its bookkeeping must stop).
    /// </summary>
    private async Task WriteThroughBarsAsync(
        IMarketDataAdapter adapter,
        Symbol symbol,
        TimeFrame timeFrame,
        IReadOnlyList<Bar> bars,
        CancellationToken ct)
    {
        try
        {
            await _candleCache.WriteThroughAsync(
                adapter.Id,
                symbol,
                timeFrame,
                bars,
                ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Candle cache write-through failed for {Symbol} {TimeFrame} on {Exchange}",
                symbol.Normalized,
                timeFrame,
                adapter.Name);
        }
    }

    #endregion

    #region Candle Export

    /// <summary>
    /// One candles-export flow: clear the previous error line first, ask the
    /// view for a destination (a null is the user cancelling the dialog —
    /// not an error, nothing is written), then stream the currently loaded
    /// series to the file. A write fault surfaces on the chart panel's own
    /// error line, localized: the panel stays usable and the series stays
    /// intact.
    /// </summary>
    private async Task HandleExportCandlesAsync(CancellationToken cancellationToken)
    {
        ErrorMessage = null;

        try
        {
            // The dialog pick has no cancellation of its own: the user either
            // confirms a path or cancels the dialog (a null destination).
            var path = await SaveCandlesCsvRequested.Handle(CandlesCsvFileName);
            if (path is null)
            {
                _logger.LogInformation(
                    "Chart panel: candles export ended on a cancelled save dialog");
                return;
            }

            // The export set is exactly the displayed series: what the chart
            // draws (rebase + paged prepends included) is what lands in the
            // file, oldest first.
            await _export.ExportCandlesAsync(path, Bars, cancellationToken);
            _logger.LogInformation(
                "Chart panel: exported {Count} candles to {Path}",
                Bars.Count,
                path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Broader than the data flows: the write can fail on a missing
            // directory, a locked file or any local IO fault; the chart stays
            // usable and its series undisturbed.
            _logger.LogError(ex, "Chart panel: candles export failed");
            ErrorMessage = _localization.GetString(ExportFailedKey, ex.Message);
        }
    }

    #endregion Candle Export

    #region Snapshot & Visible-Range Export (T-094)

    /// <summary>
    /// One PNG-snapshot flow: clear the previous error line first, build the
    /// request (the suggested file name and the watermark off the workspace
    /// symbol and the standing time frame), then hand the view the render —
    /// the window picks the destination and writes the file through the
    /// chart control (picking and rendering are view behavior), a null
    /// destination is the user cancelling the dialog (not an error, nothing
    /// is written), and a render or write fault surfaces on the chart
    /// panel's own error line, localized.
    /// </summary>
    private async Task HandleExportPngAsync(CancellationToken cancellationToken)
    {
        ErrorMessage = null;

        // No workspace symbol — no context to stamp: a pick without an
        // address exports nothing (the transient-pick rule the persistence
        // keeps), and stays quiet.
        if (SelectedSymbol is not { } symbol)
        {
            _logger.LogInformation(
                "Chart panel: PNG snapshot export skipped — no workspace symbol stands");
            return;
        }

        try
        {
            var request = new PngExportRequest(
                PngFileName(symbol.Normalized, SelectedTimeFrame, DateTimeOffset.Now),
                PngWatermark(symbol.Normalized, SelectedTimeFrame));

            var path = await ExportPngRequested.Handle(request);
            if (path is null)
            {
                _logger.LogInformation(
                    "Chart panel: PNG snapshot export ended on a cancelled save dialog");
                return;
            }

            _logger.LogInformation(
                "Chart panel: exported the PNG snapshot to {Path}",
                path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Chart panel: PNG snapshot export failed");
            ErrorMessage = _localization.GetString(ExportFailedKey, ex.Message);
        }
    }

    /// <summary>
    /// One visible-range candles flow: clear the previous error line first,
    /// ask the view for the destination and the chart's own visible slice
    /// (the handoff), then stream that slice through the shared CSV writer —
    /// the file carries exactly the bars the frame paints, never the whole
    /// loaded series. A null handoff is the user cancelling the dialog (not
    /// an error, nothing is written), a write fault surfaces on the chart
    /// panel's own error line, localized.
    /// </summary>
    private async Task HandleExportVisibleCandlesAsync(CancellationToken cancellationToken)
    {
        ErrorMessage = null;

        try
        {
            var handoff = await ExportVisibleCsvRequested.Handle(VisibleCandlesCsvFileName);
            if (handoff is null)
            {
                _logger.LogInformation(
                    "Chart panel: the visible-range candles export ended on a cancelled save dialog");
                return;
            }

            await _export.ExportCandlesAsync(handoff.Path, handoff.Bars, cancellationToken);
            _logger.LogInformation(
                "Chart panel: exported {Count} visible candles to {Path}",
                handoff.Bars.Count,
                handoff.Path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Chart panel: the visible-range candles export failed");
            ErrorMessage = _localization.GetString(ExportFailedKey, ex.Message);
        }
    }

    /// <summary>
    /// The suggested PNG file name: the symbol's name made file-safe (the
    /// workspace symbols' slash separator becomes an underscore, any other
    /// filename-hostile character too), the time frame's name, a UTC
    /// timestamp to the second, the extension. Culture-invariant — the file
    /// name is data, never a localized string.
    /// </summary>
    internal static string PngFileName(
        string symbol,
        TimeFrame timeFrame,
        DateTimeOffset timestamp)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var safeSymbol = string.Concat(symbol.Select(ch => invalidChars.Contains(ch) ? '_' : ch));

        return $"{safeSymbol}_{timeFrame}_{timestamp.ToUniversalTime():yyyyMMdd_HHmmss}.png";
    }

    /// <summary>
    /// The snapshot watermark: the symbol and the time frame read together.
    /// The middot is a typographic glyph joining two data values (the same
    /// composition discipline the axis ticks keep — the watermark is data,
    /// not prose, so it carries no resx entry).
    /// </summary>
    internal static string PngWatermark(string symbol, TimeFrame timeFrame) =>
        $"{symbol} · {timeFrame}";

    #endregion Snapshot & Visible-Range Export (T-094)

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
            var token = CancellationTokenSource.CreateLinkedTokenSource(ct, _olderFetchEpoch.Token).Token;

            var page = await _loadedAdapter.GetBarsAsync(
                target.Symbol,
                target.TimeFrame,
                HistoryStart(anchor, target.TimeFrame, InitialBarCount),
                to: anchor,
                limit: InitialBarCount + 1,
                ct: token);

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
                // Deeper panning stays on the loaded bars until a rebase, and
                // the series' own depth edge surfaces as the localized note
                // (T-086 — the honest end, never a fabricated substitute).
                _olderHistoryExhausted = true;
                // The request path runs marshalled end-to-end (the chart's
                // fetch threshold drives it on the UI thread), so the note
                // assigns directly: the anchor is where the loaded history
                // ends — the feed served nothing older (T-086, the honest
                // end, never a fabricated substitute).
                HistoryLimitNote = BuildHistoryLimitText(series[0].Time);
                _logger.LogInformation(
                    "Chart older history exhausted for {Symbol} {TimeFrame} on {Exchange}",
                    target.Symbol.Normalized,
                    target.TimeFrame,
                    target.ExchangeId);
                return;
            }

            // WRITE-THROUGH (T-085): the fetched page persists before the
            // prepend delivers — the overlap conflates on the composite key,
            // so the next start serves the extended history from the cache.
            await WriteThroughBarsAsync(_loadedAdapter, target.Symbol, target.TimeFrame, page, token);

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

    #region Deep-history Repair

    /// <summary>
    /// Whether a payload stamped with the given delivered page still owns the
    /// series: the target triple must match exactly (a rebase re-pins the
    /// triple — they compare by field, so a page of a REPLACED series drops).
    /// The marshalled applies call this in dispatch order, so a rebase that
    /// landed before the payload's dispatch makes every stale payload drop.
    /// </summary>
    private bool IsStaleDelivery(ChartBarsPage source) =>
        _loadedTarget is not { } target
        || target.Symbol != source.Symbol
        || target.TimeFrame != source.TimeFrame
        || target.ExchangeId != source.Adapter.Id
        || !ReferenceEquals(_loadedAdapter, source.Adapter);

    /// <summary>
    /// Start one repair chain for a freshly delivered page (T-086): the chain
    /// probes the cache behind the window's head, detects the calendar holes
    /// and fills the fillable ones through <see cref="_backfill"/>. One epoch
    /// per delivery: the boundary takes the living chain's epoch atomically
    /// (cancel and dispose are the replacing delivery's property — no stale
    /// chain is ever cancelled after its own or another's dispose, and no
    /// finished chain re-clears a newer chain's epoch).
    /// </summary>
    private void KickRepairPass(ChartBarsPage page)
    {
        var spent = Interlocked.Exchange(ref _repairEpoch, null);
        if (spent is not null)
        {
            spent.Cancel();
            spent.Dispose();
        }

        var epoch = new CancellationTokenSource();
        _repairEpoch = epoch;

        Observable
            .FromAsync(_ => RepairLoadedSeriesAsync(page, epoch))
            .Subscribe(
                static _ => { },
                error => _logger.LogError(
                    error,
                    "Failed to repair the chart series for {Symbol} {TimeFrame} on {Exchange}",
                    page.Symbol.Normalized,
                    page.TimeFrame,
                    page.Adapter.Id));
    }

    /// <summary>
    /// The repair chain for one delivered page: every pass probes the cache
    /// strictly older than the candidate series' head, grows the candidate
    /// with the cached coverage (detection context only — contiguous cached
    /// bars NEVER deepen the view; the additions stay fetched gap-fill bars),
    /// detects the calendar holes and fetches the fillable ones through the
    /// backfill service, then publishes the pass's additions — the marshalled
    /// merge subscriber applies them by open time. Oversized truncated holes
    /// stay unfetched (deeper history is the paging machinery's own bounded
    /// passes), and a floor the feed reveals ends the chain with the
    /// localized <c>Chart.HistoryLimit</c> note at the merged series' oldest
    /// bar. A cancelled epoch is the normal rebase outcome — quiet.
    /// </summary>
    private async Task RepairLoadedSeriesAsync(ChartBarsPage page, CancellationTokenSource epoch)
    {
        try
        {
            if (page.Bars.Count == 0 || IsStaleDelivery(page))
            {
                // Nothing to repair behind an empty window, and an already
                // stale kick (a rebase landed between the delivery and this
                // start) belongs to the replaced series.
                return;
            }

            // The candidate is the pure working series: the delivered page
            // plus the cached coverage behind its head. It never touches the
            // UI collection cross-thread — additions publish through
            // <see cref="_repairedAdds"/> and the marshalled subscriber
            // merges them (the displayed bar wins every equal open).
            var candidate = page.Bars;
            var floored = false;

            for (var pass = 1; pass <= MaxRepairPasses; pass++)
            {
                // The probe reaches strictly BELOW the candidate head: the
                // repair works behind the delivered window, never in front.
                var cachedOlder = await ReadOlderCoverageAsync(page, candidate[0].Time, epoch.Token);
                var grew = false;
                if (cachedOlder.Count > 0 &&
                    BarSeriesMerge.UnionAscending(candidate, cachedOlder) is { } grown)
                {
                    candidate = grown;
                    grew = true;
                }

                var gaps = CandleGapDetector.DetectGaps(
                    candidate,
                    page.TimeFrame,
                    page.Symbol.AssetClass,
                    DateTimeOffset.UtcNow);
                if (gaps is null)
                {
                    _logger.LogError(
                        "Chart repair rejected: the candidate series is not strictly ascending ({Symbol} {TimeFrame} on {Exchange})",
                        page.Symbol.Normalized,
                        page.TimeFrame,
                        page.Adapter.Id);
                    return;
                }

                List<Bar> fetched = [];
                foreach (var gap in gaps)
                {
                    if (gap.IsTruncated)
                    {
                        // The enumeration stopped at the detector's bound:
                        // the hole continues wider than one bounded pass can
                        // fetch — the paging machinery's own passes continue.
                        _logger.LogInformation(
                            "Chart repair skips a truncated gap ({Missing}+ bars missing until {Until}) for {Symbol} {TimeFrame}",
                            gap.MissingBars,
                            gap.LastMissingOpen,
                            page.Symbol.Normalized,
                            page.TimeFrame);
                        continue;
                    }

                    // The fetch window carries the hole exactly: the lower
                    // edge is the first missing OPEN, the upper edge is the
                    // open right above the hole (exclusive — the band filter
                    // keeps only genuinely missing opens).
                    var upper = TimeFrameCalendar.NextOpenAfter(
                        page.TimeFrame,
                        page.Symbol.AssetClass,
                        gap.LastMissingOpen);
                    var request = new CandleBackfillRequest(
                        page.Adapter,
                        page.Symbol,
                        page.TimeFrame,
                        gap.FirstMissingOpen,
                        upper);

                    await foreach (var batch in _backfill.FetchAsync(request, epoch.Token))
                    {
                        if (batch.EndOfHistory)
                        {
                            floored = true;
                        }

                        fetched.AddRange(batch.Bars);
                    }
                }

                if (fetched.Count == 0)
                {
                    // The pass produced nothing: every fillable gap was
                    // already served or the feed answered empty in them.
                    // The pass that GREW the candidate may have more cache
                    // behind the new head — one more pass probes it; a pass
                    // that grew nothing ends the chain here.
                    if (!grew)
                    {
                        break;
                    }

                    continue;
                }

                // One open, one bar: the segments' band filter already keeps
                // the window exclusive at the top; the compact pass is the
                // belt-and-braces against any adapter boundary quirk.
                fetched.Sort(static (left, right) => left.Time.CompareTo(right.Time));
                List<Bar> additions = [];
                foreach (var bar in fetched)
                {
                    if (additions.Count == 0 || additions[^1].Time < bar.Time)
                    {
                        additions.Add(bar);
                    }
                }

                if (BarSeriesMerge.UnionAscending(candidate, additions) is not { } healed)
                {
                    _logger.LogError(
                        "Chart repair rejected: the fetched additions are not strictly ascending ({Symbol} {TimeFrame} on {Exchange})",
                        page.Symbol.Normalized,
                        page.TimeFrame,
                        page.Adapter.Id);
                    return;
                }

                candidate = healed;
                _repairedAdds.OnNext(new RepairedBars(page, additions));
            }

            // The floor claims live at the series' CURRENT depth: the note
            // composes at chain end so the boundary names the deepest bar the
            // merged series reaches (a mid-chain floor before more coverage
            // landed would understate it). The page stamp makes a note
            // racing a rebase drop at the marshalled apply.
            if (floored)
            {
                _historyNotes.OnNext(
                    new HistoryNote(page, BuildHistoryLimitText(candidate[0].Time)));
            }
        }
        catch (OperationCanceledException)
        {
            // The epoch was cancelled: a rebase owns the series now, and the
            // merge subscriber would drop anything this chain publishes.
        }
        finally
        {
            // The chain clears exactly its OWN epoch: a compare-exchange only
            // ever matches this chain's source, so a newer delivery's epoch
            // is never destroyed by this finishing chain. The cancel and
            // dispose of a REPLACED epoch belong to the delivery that took
            // it; a chain that finishes while its epoch is still claimed
            // (no newer delivery) leaves the field clear for the next kick.
            Interlocked.CompareExchange(ref _repairEpoch, null, epoch);
        }
    }

    /// <summary>
    /// One best-effort probe of the cached coverage strictly older than the
    /// given open (the pass's detection context behind the delivered head):
    /// a fault is bookkeeping noise (logged, the pass continues on the
    /// fetched coverage), while a cancellation propagates (the epoch dropped
    /// this chain; its bookkeeping must stop).
    /// </summary>
    private async Task<IReadOnlyList<Bar>> ReadOlderCoverageAsync(
        ChartBarsPage page,
        DateTimeOffset before,
        CancellationToken ct)
    {
        try
        {
            return await _candleCache.GetOlderThanAsync(
                page.Adapter.Id,
                page.Symbol,
                page.TimeFrame,
                before,
                RepairOlderProbeBars,
                ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Candle cache older-coverage probe failed for {Symbol} {TimeFrame} on {Exchange}; the pass continues on the fetched coverage",
                page.Symbol.Normalized,
                page.TimeFrame,
                page.Adapter.Id);
            return [];
        }
    }

    /// <summary>
    /// The localized text of the deep-history end note for one boundary: the
    /// paging request path runs marshalled end-to-end and assigns directly;
    /// the repair chain composes the same text and stamps it for the
    /// marshalled apply.
    /// </summary>
    private string BuildHistoryLimitText(DateTimeOffset boundary) =>
        _localization.GetString(
            HistoryLimitKey,
            boundary.ToString(UtcTimestampFormat, CultureInfo.InvariantCulture));

    #endregion Deep-history Repair
}

