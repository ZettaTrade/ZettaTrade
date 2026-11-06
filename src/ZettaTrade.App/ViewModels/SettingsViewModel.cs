namespace ZettaTrade.App.ViewModels;

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Reactive.Linq;
using Livery.Localization;
using Livery.Themes;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.AI.Mcp;
using ZettaTrade.App.Services;
using ZettaTrade.Charting;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Configuration;
using ZettaTrade.Infrastructure.Shell;
/// <summary>The half-day-agnostic status byte ReactiveUI.Primitives names RxVoid.</summary>
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// ViewModel for the settings window (theme, language, etc.).
/// Delegates available options to ThemeManager and LocalizationManager (DRY, SoC).
/// Changes take effect only on the explicit <see cref="SaveAsync"/> call: the
/// window starts from selections synced to the applied state
/// (<see cref="ResetSelectionsAsync"/>) and only a real difference is applied and
/// persisted, so closing without saving never leaves a half-applied state.
/// The pairs tab manages both workspace marks: the all-pairs list and the
/// hidden list form the hidden draft (a hide move or a reveal lands in it
/// at once), and every all-pairs row carries a star button feeding the
/// favorites draft. A starred pair pins to the list head and is never
/// hidden until unstarred (the marks are mutually exclusive by rule: a
/// favorite beats a stored hidden entry). Only Save applies and persists
/// the real difference.
/// Theme diagnostics are read-only: broken files are listed
/// (<see cref="ThemeFileErrors"/>) and the user themes folder opens through
/// the injected <see cref="IShellUtils"/> (platform code stays in
/// Infrastructure).
/// </summary>
public sealed class SettingsViewModel : ReactiveObject
{
    #region Constants

    /// <summary>Combo box index meaning "nothing selected".</summary>
    private const int NoSelection = -1;

    /// <summary>Combo box index of the default 24-hour clock option.</summary>
    private const int Hours24Index = 0;

    /// <summary>Combo box index of the 12-hour clock option.</summary>
    private const int Hours12Index = 1;

    /// <summary>The SMA row name the indicator overlay settings hold (the catalog resolves it).</summary>
    private const string SmaIndicatorName = "SMA";

    /// <summary>The persisted SMA period sentinel meaning "no readable SMA row is cached yet" (the selection shows empty).</summary>
    private const int NoSmaPeriod = 0;

    #endregion Constants

    #region Properties

    private int _selectedThemeIndex = NoSelection;
    private int _selectedLanguageIndex = NoSelection;

    /// <summary>
    /// The applied clock format, synced from the settings service (the
    /// source of truth): the async initial read seeds it at construction,
    /// and the changes stream keeps it current — the selections sync from
    /// this cache on every window open.
    /// </summary>
    private bool _persistedUse12HourClock = UiSettingsKeys.DefaultUse12HourClock;

    /// <summary>Selected time format index (0 = 24-hour, 1 = 12-hour); <see cref="NoSelection"/> while the initial read has not landed.</summary>
    private int _selectedTimeFormatIndex = NoSelection;
    private IReadOnlyList<ThemeFileLoadError> _themeFileErrors = Array.Empty<ThemeFileLoadError>();

    private bool _isSmaEnabled;

    private string _smaPeriodText = string.Empty;

    /// <summary>Selected theme index (combo box); <see cref="NoSelection"/> when nothing is selected.</summary>
    public int SelectedThemeIndex
    {
        get => _selectedThemeIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedThemeIndex, value);
    }

    /// <summary>Selected language index (combo box); <see cref="NoSelection"/> when nothing is selected.</summary>
    public int SelectedLanguageIndex
    {
        get => _selectedLanguageIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedLanguageIndex, value);
    }

    /// <summary>Selected time format index (0 = 24-hour, 1 = 12-hour); <see cref="NoSelection"/> when nothing is selected.</summary>
    public int SelectedTimeFormatIndex
    {
        get => _selectedTimeFormatIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedTimeFormatIndex, value);
    }

    /// <summary>
    /// Whether the SMA overlay row is enabled. A selection - like every other
    /// one here - applies only on the explicit Save: the checkbox is the
    /// selection, the persisted row is the applied state.
    /// </summary>
    public bool IsSmaEnabled
    {
        get => _isSmaEnabled;
        set => this.RaiseAndSetIfChanged(ref _isSmaEnabled, value);
    }

    /// <summary>
    /// The SMA period as typed. Free text on purpose: an unparsable value
    /// simply fails the save (the cached config stays in charge - nothing
    /// defaults or clamps), the invariant culture parses it.
    /// </summary>
    public string SmaPeriodText
    {
        get => _smaPeriodText;
        set => this.RaiseAndSetIfChanged(ref _smaPeriodText, value);
    }

    /// <summary>
    /// Whether the scanned plugin catalog actually registers the SMA
    /// indicator: the indicators tab renders the SMA row only for a plugin
    /// the scan loaded (a missing or skipped-by-scan plugin assembly would
    /// otherwise render a dead row). The plugin set is fixed for the
    /// session (the scan runs at startup only), so the flag is settled at
    /// construction.
    /// </summary>
    public bool IsSmaPluginLoaded { get; }

    /// <summary>
    /// Whether the scanned plugin catalog registered no indicators at all:
    /// the indicators tab shows the honest empty state (nothing was found
    /// beside the application's plugin folder) instead of any row. Mutually
    /// exclusive with <see cref="IsSmaPluginLoaded"/> by construction.
    /// </summary>
    public bool IsIndicatorCatalogEmpty { get; }

    /// <summary>
    /// Whether the local MCP server is enabled. A selection - like every
    /// other one here - applies only on the explicit Save: the checkbox is
    /// the selection, the persisted row is the applied state, and the saved
    /// match starts/stops the host live (the state below renders what stuck).
    /// </summary>
    public bool IsMcpServerEnabled
    {
        get => _isMcpServerEnabled;
        set => this.RaiseAndSetIfChanged(ref _isMcpServerEnabled, value);
    }

    /// <summary>The MCP tab's honest running row: the server is live.</summary>
    public bool IsMcpRunning => _mcpHostState == McpHostState.Running;

    /// <summary>The MCP tab's honest stopped row (the default state until the server runs).</summary>
    public bool IsMcpStopped => _mcpHostState == McpHostState.Stopped;

    /// <summary>The MCP tab's honest failed row: the apply did not reach the server, the stored choice applies next launch.</summary>
    public bool IsMcpFailed => _mcpHostState == McpHostState.Failed;

    /// <summary>
    /// The external-servers block's draft rows: the editor surface for the
    /// terminal's external MCP client entries (T-068). The rows live only in
    /// memory — the save maps them into the persisted list and replays the
    /// manager's apply; a window open re-seeds them from the settings.
    /// </summary>
    public ObservableCollection<McpServerRow> McpServers { get; } = [];

    private PairRow? _selectedAllPair;

    /// <summary>The row picked in the all-pairs list (the hide command's
    /// input); null while nothing is picked — a fresh reset clears both
    /// picks.</summary>
    public PairRow? SelectedAllPair
    {
        get => _selectedAllPair;
        set => this.RaiseAndSetIfChanged(ref _selectedAllPair, value);
    }

    private string? _selectedHiddenPair;

    /// <summary>The pair picked in the hidden list (the reveal command's input);
    /// null while nothing is picked — a fresh reset clears both picks.</summary>
    public string? SelectedHiddenPair
    {
        get => _selectedHiddenPair;
        set => this.RaiseAndSetIfChanged(ref _selectedHiddenPair, value);
    }

    /// <summary>
    /// The workspace exchange's all pairs as selectable rows, starred pairs
    /// pinned to the head (the exchange's own order inside each group; the
    /// hidden names are not here — they form the other list).
    /// </summary>
    public ObservableCollection<PairRow> AllPairs => _allPairs;

    /// <summary>The workspace exchange's hidden pairs: the listed names in the
    /// exchange's order, then the stale ones (still hidden yet no longer
    /// listed) in their normalized order.</summary>
    public ObservableCollection<string> HiddenPairs => _hiddenPairs;

    /// <summary>Available themes from ThemeManager (no duplication).</summary>
    public IReadOnlyList<ThemeInfo> AvailableThemes => _themeManager.AvailableThemes;

    /// <summary>Available languages from LocalizationManager (no duplication).</summary>
    public IReadOnlyList<LanguageInfo> AvailableLanguages => _localization.AvailableLanguages;

    /// <summary>Theme files that failed to load, with the file name shown per entry.</summary>
    public IReadOnlyList<ThemeFileLoadError> ThemeFileErrors
    {
        get => _themeFileErrors;
        private set
        {
            if (ReferenceEquals(_themeFileErrors, value))
            {
                return;
            }

            _themeFileErrors = value;
            this.RaisePropertyChanged(nameof(ThemeFileErrors));
            this.RaisePropertyChanged(nameof(HasThemeFileErrors));
        }
    }

    /// <summary>True while at least one theme file failed to load; drives the warning list visibility.</summary>
    public bool HasThemeFileErrors => _themeFileErrors.Count > 0;

    #endregion Properties

    #region Commands

    /// <summary>Command to open the user themes folder in the OS file manager.</summary>
    public ReactiveCommand<RxVoid, RxVoid> OpenThemesFolderCommand { get; }

    /// <summary>
    /// Hide the pair picked in the all-pairs list: the pair moves into the
    /// hidden draft (the lists repaint at once; only Save applies and
    /// persists the map). A starred pick never hides — the canExecute gate
    /// refuses it until the star is removed.
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid> HidePairCommand { get; }

    /// <summary>
    /// Reveal the pair picked in the hidden list: the pair leaves the hidden
    /// draft (the lists repaint at once; only Save applies and persists the
    /// map).
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid> RevealPairCommand { get; }

    /// <summary>
    /// Star or unstar the given all-pairs row: the favorites draft updates,
    /// the lists repaint (a starred pair pins to the head) and only Save
    /// applies and persists the map — the same draft contract the moves
    /// follow.
    /// </summary>
    public ReactiveCommand<PairRow, RxVoid> TogglePairFavoriteCommand { get; }

    /// <summary>
    /// Add a fresh external-server draft row: the empty draft stays local
    /// until Save (a blank-named row simply drops out of the persisted list
    /// with a logged warning).
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid> AddMcpServerCommand { get; }

    /// <summary>Remove one external-server draft row: local until Save (the persisted list and its connections apply on Save only).</summary>
    public ReactiveCommand<McpServerRow, RxVoid> RemoveMcpServerCommand { get; }

    #endregion Commands

    #region Private Fields

    private readonly ISettingsService _settings;
    private readonly IThemeManager _themeManager;
    private readonly ILocalizationManager _localization;
    private readonly IShellUtils _shellUtils;
    private readonly ActiveExchangeService _activeExchanges;
    private readonly SymbolListProvider _symbolList;
    private readonly ILogger<SettingsViewModel> _logger;

    /// <summary>
    /// The scanned indicator catalog (shared with the chart pipeline): its
    /// loaded-name set decides whether the overlay rows render at all.
    /// </summary>
    private readonly IIndicatorCatalog _indicatorCatalog;

    /// <summary>
    /// The persisted hidden-pairs map (all exchanges) as last read: the
    /// pairs tab's drafts seed from it and the save rewrites it with the
    /// current exchange's entry replaced (the other exchanges' entries
    /// survive untouched).
    /// </summary>
    private IReadOnlyDictionary<string, IReadOnlySet<string>> _persistedHiddenMarks =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

    /// <summary>
    /// The persisted favorites map (all exchanges) as last read: the same
    /// seed-and-save contract as the hidden map (the star drafts seed from
    /// it, the save rewrites the draft exchange's entry).
    /// </summary>
    private IReadOnlyDictionary<string, IReadOnlySet<string>> _persistedFavoriteMarks =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

    /// <summary>
    /// The exchange the pair lists and drafts were built for, captured at
    /// every reset (the modal window cannot race an exchange switch). Empty
    /// until the first reset — the save never writes a draft with no
    /// exchange context (nothing would be behind it).
    /// </summary>
    private string _draftExchangeId = string.Empty;

    /// <summary>The hidden-names draft: re-seeded from the persisted map at
    /// every reset, mutated by the hide and reveal commands, consumed by the
    /// save's diff.</summary>
    private HashSet<string> _draftHiddenNames = new(StringComparer.Ordinal);

    /// <summary>The favorites draft: re-seeded from the persisted map at
    /// every reset, mutated by the row star buttons, consumed by the save's
    /// diff.</summary>
    private HashSet<string> _draftFavoriteNames = new(StringComparer.Ordinal);

    /// <summary>The workspace exchange's listing (normalized names, exchange
    /// order): the lists repaint from it and the draft edits.</summary>
    private IReadOnlyList<string> _listingNames = [];

    /// <summary>The all-pairs list backing (rebuilt wholesale at every reset
    /// and draft move; the identity never changes for the binding).</summary>
    private readonly ObservableCollection<PairRow> _allPairs = new();

    /// <summary>The hidden list backing (the same rebuild contract).</summary>
    private readonly ObservableCollection<string> _hiddenPairs = new();

    /// <summary>
    /// The persisted indicator config as last read (seed + changes stream):
    /// the SMA row's own cache derives from it and the save rewrites the
    /// whole row array (future rows survive a save untouched).
    /// </summary>
    private string _persistedIndicatorConfig = UiSettingsKeys.DefaultChartIndicators;

    /// <summary>The cached SMA row pieces (the sentinel period means "no row cached").</summary>
    private bool _persistedSmaEnabled;
    private int _persistedSmaPeriod = NoSmaPeriod;

    /// <summary>
    /// The persisted MCP server enable choice as last read: the checkbox
    /// drafts seed from it and the save persists only a real difference
    /// (the same seed + changes pattern as the clock choice).
    /// </summary>
    private bool _persistedMcpServerEnabled = UiSettingsKeys.DefaultMcpServerEnabled;

    /// <summary>The unsaved MCP enable checkbox (the draft the save applies).</summary>
    private bool _isMcpServerEnabled;

    /// <summary>
    /// The MCP host's lifecycle state as the runner surfaces it: the three
    /// honest rows derive from it and every transition raises the bools.
    /// </summary>
    private McpHostState _mcpHostState = McpHostState.Stopped;

    /// <summary>The host runner (start/stop live on save, state surfacing).</summary>
    private readonly McpHostService _mcpHosts;

    /// <summary>
    /// The external MCP client connections' owner (T-068): the save applies
    /// the editor's list live through its reconcile and the state snapshots
    /// feed the per-entry rows.
    /// </summary>
    private readonly IMcpClientManager _mcpClients;

    /// <summary>
    /// The persisted external-server list as last read: the drafts seed from
    /// it and the save compares against it, so an unchanged save stays
    /// silent (the same no-op discipline every other row keeps).
    /// </summary>
    private string _persistedServerJson = McpClientManager.DefaultServersJson;

    #endregion Private Fields

    #region Constructor

    /// <summary>
    /// Create a new SettingsViewModel instance. The managers must already be
    /// initialized (app startup applies the stored preferences before the
    /// settings window can open), so their current state defines the selection.
    /// </summary>
    /// <param name="settings">Stored UI preferences (applied on Save).</param>
    /// <param name="themeManager">Theme manager providing applied/available themes.</param>
    /// <param name="localization">Localization manager providing applied/available languages.</param>
    /// <param name="shellUtils">File manager launcher for the themes folder button.</param>
    /// <param name="mcpHosts">The MCP host runner: the save applies the enable choice live and the state stream renders the honest rows.</param>
    /// <param name="indicatorCatalog">The scanned plugin catalog: the indicators tab renders its rows only for plugins the set actually loaded.</param>
    /// <param name="mcpClients">
    /// The external MCP client connections' owner: the save applies the
    /// external-server list live and the state snapshots render the per-entry
    /// connection rows honestly.
    /// </param>
    /// <param name="logger">Injected application logger.</param>
    /// <param name="themeLoadErrors">
    /// Stream of theme file load errors; when omitted (tests without the
    /// diagnostics stream), the warning list simply stays empty.
    /// </param>
    public SettingsViewModel(
        ISettingsService settings,
        IThemeManager themeManager,
        ILocalizationManager localization,
        IShellUtils shellUtils,
        McpHostService mcpHosts,
        ActiveExchangeService activeExchanges,
        SymbolListProvider symbolList,
        IIndicatorCatalog indicatorCatalog,
        IMcpClientManager mcpClients,
        ILogger<SettingsViewModel> logger,
        IObservable<IReadOnlyList<ThemeFileLoadError>>? themeLoadErrors = null)
    {
        _settings = settings;
        _themeManager = themeManager;
        _localization = localization;
        _shellUtils = shellUtils;
        _mcpHosts = mcpHosts;
        _activeExchanges = activeExchanges;
        _symbolList = symbolList;
        _indicatorCatalog = indicatorCatalog;
        _mcpClients = mcpClients;
        _logger = logger;

        // The plugin surface is settled at construction (the scan runs at
        // startup only): the SMA row renders only when the catalog holds
        // the plugin; a nameless (null) set reads as an empty one. A
        // catalog that registered other plugins but not SMA renders
        // neither the row nor the empty state — the two flags answer the
        // two independent questions of the tab's honesty contract.
        var loadedNames = indicatorCatalog.LoadedNames ?? Array.Empty<string>();
        IsSmaPluginLoaded = loadedNames.Contains(SmaIndicatorName, StringComparer.Ordinal);
        IsIndicatorCatalogEmpty = loadedNames.Count == 0;

        OpenThemesFolderCommand = ReactiveCommand.Create(HandleOpenThemesFolder);

        if (themeLoadErrors is not null)
        {
            // App-lifetime singleton: the subscription mirrors the ViewModel's
            // lifetime (same contract as the status bar chips). The manager
            // publishes on the UI thread when Avalonia runs, so a plain
            // subscription is UI-thread safe; in tests it publishes directly.
            themeLoadErrors.Subscribe(
                errors => ThemeFileErrors = errors,
                ex => _logger.LogError(ex, "Theme load error stream failed"));
        }

        // The clock format lives in the settings service (no manager owns its
        // applied state, unlike the theme and the language): one async read
        // seeds the cache the selections sync from, the changes stream keeps
        // it current, and both subscriptions mirror the app-lifetime
        // singleton. Non-bool values are dropped — the cached choice stays
        // in charge until a real change arrives.
        Observable.FromAsync(() => _settings.GetAsync<bool>(
                UiSettingsKeys.Section,
                UiSettingsKeys.ChartUse12HourClock,
                UiSettingsKeys.DefaultUse12HourClock))
            .Subscribe(
                use12HourClock => _persistedUse12HourClock = use12HourClock,
                ex => _logger.LogError(ex, "Failed to read the chart clock format"));

        // The changes stream keeps the cached choice current — non-bool
        // values are dropped, the cached choice stays in charge until a real
        // change arrives. Both subscriptions mirror the app-lifetime
        // singleton.
        _settings.Changes
            .Where(change => change.Section == UiSettingsKeys.Section &&
                             change.Key == UiSettingsKeys.ChartUse12HourClock)
            .Select(change => change.NewValue)
            .OfType<bool>()
            .Subscribe(
                use12HourClock => _persistedUse12HourClock = use12HourClock,
                ex => _logger.LogError(ex, "The chart clock format stream stopped unexpectedly"));

        // The indicator overlay config: the same seed + changes pattern as
        // the clock. The SMA row's cache derives from the whole array (the
        // first SMA row wins), the save rewrites the array keeping the
        // other rows.
        Observable.FromAsync(() => _settings.GetAsync<string>(
                UiSettingsKeys.Section,
                UiSettingsKeys.ChartIndicators,
                UiSettingsKeys.DefaultChartIndicators))
            .Subscribe(
                CacheIndicatorConfig,
                ex => _logger.LogError(ex, "Failed to read the chart indicator config"));

        _settings.Changes
            .Where(change => change.Section == UiSettingsKeys.Section &&
                             change.Key == UiSettingsKeys.ChartIndicators)
            .Select(change => change.NewValue)
            .OfType<string>()
            .Subscribe(
                CacheIndicatorConfig,
                ex => _logger.LogError(ex, "The chart indicator config stream stopped unexpectedly"));

        // The hidden-pairs map: the pairs tab reads it (the drafts seed from
        // it) and writes it on Save. The same seed + changes pattern as the
        // clock: one async read fills the cache, the stream keeps it current
        // and non-string payloads drop — the cached document stays in
        // charge until a real change arrives.
        Observable.FromAsync(() => _settings.GetAsync<string>(
                UiSettingsKeys.WorkspaceSection,
                UiSettingsKeys.WorkspaceHiddenSymbols))
            .Subscribe(
                CacheHiddenMarks,
                ex => _logger.LogError(ex, "Failed to read the hidden-pairs map"));

        _settings.Changes
            .Where(change => change.Section == UiSettingsKeys.WorkspaceSection &&
                             change.Key == UiSettingsKeys.WorkspaceHiddenSymbols)
            .Select(change => change.NewValue)
            .OfType<string>()
            .Subscribe(
                CacheHiddenMarks,
                ex => _logger.LogError(ex, "The hidden-pairs map stream stopped unexpectedly"));

        // The favorites map: the pairs tab reads it (the star drafts seed
        // from it) and writes it on Save. The same seed + changes pattern as
        // the hidden map.
        Observable.FromAsync(() => _settings.GetAsync<string>(
                UiSettingsKeys.WorkspaceSection,
                UiSettingsKeys.WorkspaceFavoriteSymbols))
            .Subscribe(
                CacheFavoriteMarks,
                ex => _logger.LogError(ex, "Failed to read the favorites map"));

        _settings.Changes
            .Where(change => change.Section == UiSettingsKeys.WorkspaceSection &&
                             change.Key == UiSettingsKeys.WorkspaceFavoriteSymbols)
            .Select(change => change.NewValue)
            .OfType<string>()
            .Subscribe(
                CacheFavoriteMarks,
                ex => _logger.LogError(ex, "The favorites map stream stopped unexpectedly"));

        // The MCP server: the same seed + changes pattern as the clock; the
        // enable choice drafts the checkbox and the save applies it live.
        Observable.FromAsync(() => _settings.GetAsync<bool>(
                UiSettingsKeys.McpSection,
                UiSettingsKeys.McpServerEnabled,
                UiSettingsKeys.DefaultMcpServerEnabled))
            .Subscribe(
                enabled => _persistedMcpServerEnabled = enabled,
                ex => _logger.LogError(ex, "Failed to read the MCP server enable state"));

        _settings.Changes
            .Where(change => change.Section == UiSettingsKeys.McpSection &&
                             change.Key == UiSettingsKeys.McpServerEnabled)
            .Select(change => change.NewValue)
            .OfType<bool>()
            .Subscribe(
                enabled => _persistedMcpServerEnabled = enabled,
                ex => _logger.LogError(ex, "The MCP server enable stream stopped unexpectedly"));

        // The host's honest state (running / stopped / failed): the three
        // rows derive and rise on every transition; both subscriptions
        // mirror the app-lifetime singleton.
        _mcpHosts.State
            .Subscribe(
                state =>
                {
                    _mcpHostState = state;
                    this.RaisePropertyChanged(nameof(IsMcpRunning));
                    this.RaisePropertyChanged(nameof(IsMcpStopped));
                    this.RaisePropertyChanged(nameof(IsMcpFailed));
                },
                ex => _logger.LogError(ex, "The MCP host state stream stopped unexpectedly"));

        // The pairs tab's commands act on a pick: no pick, no move. A hide
        // move needs an unstarred pick (a starred pair never hides — that is
        // the point of the star) and more than one visible pair — the
        // workspace keeps at least one tradable context (the '>' button
        // grays out on a starred pick and on the last row). The lists
        // repaint through CollectionChanged events, so the count rides the
        // same observable.
        var allPairCount = Observable
            .FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                handler => _allPairs.CollectionChanged += handler,
                handler => _allPairs.CollectionChanged -= handler)
            .Select(_ => _allPairs.Count)
            .StartWith(_allPairs.Count);
        var hasHideablePick = this
            .WhenAnyValue(x => x.SelectedAllPair)
            .Select(static pick => pick is not null && !pick.IsFavorite);
        var hasHiddenPairPick = this.WhenAnyValue(x => x.SelectedHiddenPair).Select(static name => name is not null);
        HidePairCommand = ReactiveCommand.Create(
            HideSelectedPair,
            hasHideablePick.CombineLatest(allPairCount, static (hideable, count) => hideable && count > 1));
        RevealPairCommand = ReactiveCommand.Create(RevealSelectedPair, hasHiddenPairPick);
        TogglePairFavoriteCommand = ReactiveCommand.Create<PairRow>(TogglePairFavorite);

        AddMcpServerCommand = ReactiveCommand.Create(AddServerDraftRow);
        RemoveMcpServerCommand = ReactiveCommand.Create<McpServerRow>(row => McpServers.Remove(row));

        // The external servers' honest per-entry states: the manager's
        // snapshots repaint the matching draft rows by name (the name is the
        // key both sides share). The subscription mirrors the app-lifetime
        // singleton, the same contract the theme errors carry.
        _mcpClients.StatesChanged.Subscribe(
            ApplyMcpClientStates,
            ex => _logger.LogError(ex, "The MCP client state stream stopped unexpectedly"));

        ResetAppearanceSelections();
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>
    /// Sync the selections with the currently applied theme and language,
    /// then repaint the pairs tab for the current exchange. The ViewModel is
    /// an app-lifetime singleton, so the window refreshes the selections on
    /// every open; otherwise an abandoned (unsaved) selection would show up
    /// as already-applied state on the next open.
    /// </summary>
    public async Task ResetSelectionsAsync(CancellationToken ct = default)
    {
        ResetAppearanceSelections();
        await RefreshSymbolPairsAsync(ct);
        await RefreshMcpClientRowsAsync(ct);
    }

    /// <summary>
    /// Apply and persist the selected theme and language. A selection equal to
    /// the applied state is skipped (no redundant events, no settings rewrite).
    /// </summary>
    public async Task SaveAsync()
    {
        var theme = PickAt(_themeManager.AvailableThemes, _selectedThemeIndex);
        if (theme is not null && !IsThemeApplied(theme))
        {
            await _themeManager.ApplyThemeAsync(theme.Name);
            await _settings.SetAsync(
                UiSettingsKeys.Section,
                UiSettingsKeys.Theme,
                theme.Name);
        }

        var language = PickAt(_localization.AvailableLanguages, _selectedLanguageIndex);
        if (language is not null && !IsLanguageApplied(language))
        {
            await _localization.SetLanguageAsync(language.Code);
            await _settings.SetAsync(
                UiSettingsKeys.Section,
                UiSettingsKeys.Language,
                language.Code);
        }

        // The chart clock format: only a real difference applies (the same
        // no-op discipline the theme and language choices follow).
        if (_selectedTimeFormatIndex != NoSelection)
        {
            var use12HourClock = _selectedTimeFormatIndex == Hours12Index;
            if (use12HourClock != _persistedUse12HourClock)
            {
                await _settings.SetAsync(
                    UiSettingsKeys.Section,
                    UiSettingsKeys.ChartUse12HourClock,
                    use12HourClock);
                _persistedUse12HourClock = use12HourClock;
            }
        }

        // The indicator overlay row: the period must read as an integer for
        // the row to save at all (an unparsable text skips the row with a
        // logged warning - nothing defaults, the cached config stays in
        // charge). A real difference rewrites the row array: the SMA row is
        // replaced, every other (future) row rides untouched. The block only
        // runs when the catalog actually loaded the plugin: the save never
        // fabricates a config row for a plugin the session does not have
        // (the persisted config rides untouched otherwise — the chart
        // pipeline skips unknown names on its own seam).
        if (IsSmaPluginLoaded)
        {
            if (TryReadSmaSelection(out var smaEnabled, out var smaPeriod))
            {
                if (smaEnabled != _persistedSmaEnabled || smaPeriod != _persistedSmaPeriod)
                {
                    var (configs, _) = ChartIndicatorConfig.ParseList(_persistedIndicatorConfig);
                    var updated = configs
                        .Where(config => !string.Equals(config.Name, SmaIndicatorName, StringComparison.Ordinal))
                        .ToList();
                    updated.Add(new ChartIndicatorConfig(SmaIndicatorName, smaEnabled, smaPeriod));

                    var written = ChartIndicatorConfig.Write(updated);
                    await _settings.SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartIndicators, written);
                    CacheIndicatorConfig(written);
                }
            }
            else
            {
                _logger.LogWarning(
                    "The SMA period '{PeriodText}' is not a readable integer; the indicator selection is not saved",
                    SmaPeriodText);
            }
        }

        // The MCP server enable: only a real difference applies (the same
        // no-op discipline every other row keeps). The persisted write lands
        // first, the host apply follows: the saved choice starts or stops
        // the live server; the runner surfaces anything it could not reach.
        if (_isMcpServerEnabled != _persistedMcpServerEnabled)
        {
            await _settings.SetAsync(
                UiSettingsKeys.McpSection,
                UiSettingsKeys.McpServerEnabled,
                _isMcpServerEnabled);
            _persistedMcpServerEnabled = _isMcpServerEnabled;
            await _mcpHosts.ApplyAsync(_isMcpServerEnabled);
        }

        // The external-servers drafts: the rows map into the persisted list
        // verbatim — nothing coerced, an unusable value rides the entry and
        // the transport boundary fails it loudly — and only a real
        // difference applies (the same no-op discipline every row keeps);
        // the apply reconciles the live connections with the new list.
        List<McpServerConfig> serverConfigs = [];
        List<string> serverProblems = [];
        foreach (var row in McpServers)
        {
            var config = row.ToConfig(serverProblems);
            if (config is null)
            {
                serverProblems.Add("A draft row is dropped: no name — a nameless entry keys nothing");
                continue;
            }

            serverConfigs.Add(config);
        }

        foreach (var problem in serverProblems)
        {
            _logger.LogWarning("The external MCP server drafts problem: {Problem}", problem);
        }

        var writtenServers = McpServerConfigList.Write(serverConfigs);

        // The no-op discipline rides the semantic lists, not the wire bytes:
        // a hand-edited persisted file (a different key order) that describes
        // the same rows never triggers a rewrite or a live reconcile.
        var (persistedServers, _) = McpServerConfigList.Parse(_persistedServerJson);
        if (!persistedServers.SequenceEqual(serverConfigs))
        {
            await _settings.SetAsync(
                McpClientManager.ServersSection,
                McpClientManager.ServersKey,
                writtenServers);
            _persistedServerJson = writtenServers;
            await _mcpClients.ApplyAsync(serverConfigs);
        }

        // The hidden-pairs draft: only a real difference applies (the same
        // no-op discipline every other row keeps) and only with a real
        // exchange context behind it (a failed listing reset never wipes the
        // applied map). The map is rewritten with the draft exchange's entry
        // replaced by the draft — the other exchanges' entries ride
        // untouched.
        if (_draftExchangeId.Length > 0)
        {
            var appliedHidden = (IReadOnlySet<string>?)(_persistedHiddenMarks.GetValueOrDefault(_draftExchangeId));
            if (!_draftHiddenNames.SetEquals(appliedHidden ?? new HashSet<string>(StringComparer.Ordinal)))
            {
                var updatedMap = SymbolMarkMap.WithSet(_persistedHiddenMarks, _draftExchangeId, _draftHiddenNames);
                var written = SymbolMarkMap.Serialize(updatedMap);
                await _settings.SetAsync(
                    UiSettingsKeys.WorkspaceSection,
                    UiSettingsKeys.WorkspaceHiddenSymbols,
                    written);
                CacheHiddenMarks(written);
            }

            // The favorites draft: the same no-op discipline as the hidden
            // one (the other exchanges' entries ride untouched).
            var appliedFavorites = (IReadOnlySet<string>?)(_persistedFavoriteMarks.GetValueOrDefault(_draftExchangeId));
            if (!_draftFavoriteNames.SetEquals(appliedFavorites ?? new HashSet<string>(StringComparer.Ordinal)))
            {
                var updatedMap = SymbolMarkMap.WithSet(_persistedFavoriteMarks, _draftExchangeId, _draftFavoriteNames);
                var written = SymbolMarkMap.Serialize(updatedMap);
                await _settings.SetAsync(
                    UiSettingsKeys.WorkspaceSection,
                    UiSettingsKeys.WorkspaceFavoriteSymbols,
                    written);
                CacheFavoriteMarks(written);
            }
        }
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>
    /// Sync the appearance selections (theme, language, clock, indicator row,
    /// the MCP enable draft) with the currently applied state. A pure
    /// in-memory sync — no I/O.
    /// </summary>
    private void ResetAppearanceSelections()
    {
        SelectedThemeIndex = IndexOfAppliedTheme();
        SelectedLanguageIndex = IndexOfAppliedLanguage();
        SelectedTimeFormatIndex = _persistedUse12HourClock ? Hours12Index : Hours24Index;
        IsSmaEnabled = _persistedSmaEnabled;
        SmaPeriodText = _persistedSmaPeriod > NoSmaPeriod
            ? _persistedSmaPeriod.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        IsMcpServerEnabled = _persistedMcpServerEnabled;
    }

    /// <summary>
    /// Repaint the pairs tab for the current exchange: the workspace
    /// exchange's listing seeds the all-pairs list and the persisted hidden
    /// and favorites maps seed the drafts (a draft re-derives from the
    /// applied state, so an unsaved draft dies at the next open). A failed
    /// listing fetch logs and leaves the lists stale — the theme rows keep
    /// working, the window must not fail to open over a listing hiccup.
    /// </summary>
    private async Task RefreshSymbolPairsAsync(CancellationToken ct)
    {
        try
        {
            var exchangeId = _activeExchanges.Id;
            var names = await _symbolList.GetSymbolsAsync(ct);
            var listing = names.Select(Symbol.Parse).ToList();

            _draftExchangeId = exchangeId;
            _draftHiddenNames = new HashSet<string>(
                _persistedHiddenMarks.GetValueOrDefault(exchangeId) ?? new HashSet<string>(StringComparer.Ordinal),
                StringComparer.Ordinal);
            _draftFavoriteNames = new HashSet<string>(
                _persistedFavoriteMarks.GetValueOrDefault(exchangeId) ?? new HashSet<string>(StringComparer.Ordinal),
                StringComparer.Ordinal);
            _listingNames = listing.Select(symbol => symbol.Normalized).ToList();

            RebuildPairCollections();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh the symbol pairs; the lists stay stale");
        }
    }

    /// <summary>
    /// Repaint the two pair lists from the listing and the drafts: the
    /// all-pairs list pins the starred names to the head (the exchange's own
    /// order inside each group), the rest follows that order; the hidden
    /// list keeps the hidden names in the exchange's order and appends the
    /// stale ones (still hidden, no longer listed) in their normalized
    /// order. The marks are mutually exclusive by rule: a favorite beats a
    /// stored hidden entry (the legacy-collision decision — a starred name
    /// never shows as hidden; the moves and the save repair the document).
    /// Both picks reset — the lists repainted, the inputs died.
    /// </summary>
    private void RebuildPairCollections()
    {
        _allPairs.Clear();
        foreach (var name in _listingNames.Where(_draftFavoriteNames.Contains))
        {
            _allPairs.Add(new PairRow(name, IsFavorite: true));
        }

        foreach (var name in _listingNames.Where(name =>
            !_draftHiddenNames.Contains(name) && !_draftFavoriteNames.Contains(name)))
        {
            _allPairs.Add(new PairRow(name, IsFavorite: false));
        }

        _hiddenPairs.Clear();
        foreach (var name in _listingNames.Where(name =>
            _draftHiddenNames.Contains(name) && !_draftFavoriteNames.Contains(name)))
        {
            _hiddenPairs.Add(name);
        }

        foreach (var name in _draftHiddenNames
            .Except(_listingNames)
            .Where(name => !_draftFavoriteNames.Contains(name))
            .OrderBy(static name => name, StringComparer.Ordinal))
        {
            _hiddenPairs.Add(name);
        }

        SelectedAllPair = null;
        SelectedHiddenPair = null;
    }

    /// <summary>
    /// The all-pairs pick joins the hidden draft; the lists repaint and the
    /// pick lands on the pair that took the moved row's slot (the next row
    /// heading up, the new last one when the list tail moved). A starred
    /// pick never reaches this code — the canExecute gate refuses it.
    /// </summary>
    private void HideSelectedPair()
    {
        if (SelectedAllPair is not { } picked)
        {
            return;
        }

        var movedIndex = _allPairs.IndexOf(picked);
        _draftHiddenNames.Add(picked.Name);
        RebuildPairCollections();
        if (_allPairs.Count > 0)
        {
            SelectedAllPair = _allPairs[Math.Min(Math.Max(movedIndex, 0), _allPairs.Count - 1)];
        }
    }

    /// <summary>
    /// The hidden pick leaves the hidden draft; the lists repaint and the
    /// pick lands on the pair that took the moved row's slot (the next row
    /// heading up, the new last one when the list tail moved).
    /// </summary>
    private void RevealSelectedPair()
    {
        if (SelectedHiddenPair is not { } name)
        {
            return;
        }

        var movedIndex = _hiddenPairs.IndexOf(name);
        _draftHiddenNames.Remove(name);
        RebuildPairCollections();
        if (_hiddenPairs.Count > 0)
        {
            SelectedHiddenPair = _hiddenPairs[Math.Min(Math.Max(movedIndex, 0), _hiddenPairs.Count - 1)];
        }
    }

    /// <summary>
    /// Star or unstar the given row's pair: the favorites draft updates, the
    /// lists repaint (a starred pair pins to the head) and the pick lands on
    /// the toggled row's fresh slot — the reassignment raises the pick
    /// observable, so the move buttons' gates recompute against the new star
    /// state. Nothing persists here: the save applies the draft.
    /// </summary>
    private void TogglePairFavorite(PairRow row)
    {
        var name = row.Name;
        if (!_draftFavoriteNames.Contains(name))
        {
            _draftFavoriteNames.Add(name);
        }
        else
        {
            _draftFavoriteNames.Remove(name);
        }

        RebuildPairCollections();
        var fresh = _allPairs.FirstOrDefault(candidate => candidate.Name == name);
        if (fresh is not null)
        {
            SelectedAllPair = fresh;
        }
    }

    /// <summary>
    /// Cache the persisted hidden-pairs document (fail-safe parse, garbage
    /// drops — see <see cref="SymbolMarkMap"/>).
    /// </summary>
    private void CacheHiddenMarks(string? json)
    {
        _persistedHiddenMarks = SymbolMarkMap.Parse(json);
    }

    /// <summary>
    /// Cache the persisted favorites document (fail-safe parse, garbage
    /// drops — see <see cref="SymbolMarkMap"/>).
    /// </summary>
    private void CacheFavoriteMarks(string? json)
    {
        _persistedFavoriteMarks = SymbolMarkMap.Parse(json);
    }

    /// <summary>
    /// Cache the persisted indicator config and derive the SMA row's pieces
    /// (the first SMA row in the array wins; no row caches the sentinel
    /// period, the selections show an empty box).
    /// </summary>
    private void CacheIndicatorConfig(string indicatorSettingsJson)
    {
        _persistedIndicatorConfig = indicatorSettingsJson;

        var (configs, _) = ChartIndicatorConfig.ParseList(indicatorSettingsJson);
        var sma = configs.FirstOrDefault(config =>
            string.Equals(config.Name, SmaIndicatorName, StringComparison.Ordinal));

        _persistedSmaEnabled = sma?.Enabled ?? false;
        _persistedSmaPeriod = sma?.Period ?? NoSmaPeriod;
    }

    /// <summary>
    /// Parse the SMA selection: the period text must read as an integer
    /// (invariant culture, trimmed). Invalid text returns false - the save
    /// skips the row, nothing substitutes a default.
    /// </summary>
    private bool TryReadSmaSelection(out bool enabled, out int period)
    {
        enabled = IsSmaEnabled;
        return int.TryParse(
            SmaPeriodText.Trim(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out period);
    }

    /// <summary>
    /// Open the user themes folder in the OS file manager; the platform
    /// selection (explorer.exe / open / xdg-open) lives in Infrastructure.
    /// </summary>
    private void HandleOpenThemesFolder()
    {
        try
        {
            _shellUtils.OpenInFileManager(ConfigPathResolver.ThemesDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open the themes folder {Path}", ConfigPathResolver.ThemesDirectory);
        }
    }

    /// <summary>Index of the applied theme (matched by canonical Name) in AvailableThemes.</summary>
    private int IndexOfAppliedTheme()
    {
        var themes = _themeManager.AvailableThemes;
        for (var i = 0; i < themes.Count; i++)
        {
            if (IsThemeApplied(themes[i]))
            {
                return i;
            }
        }

        _logger.LogWarning("Applied theme {Theme} is not among available themes", _themeManager.CurrentTheme);
        return NoSelection;
    }

    /// <summary>Index of the applied language in AvailableLanguages: exact code, then two-letter fallback ("ru-RU" -> "ru").</summary>
    private int IndexOfAppliedLanguage()
    {
        var languages = _localization.AvailableLanguages;
        var current = _localization.CurrentLanguage;
        for (var i = 0; i < languages.Count; i++)
        {
            if (IsLanguageApplied(languages[i]))
            {
                return i;
            }
        }

        try
        {
            var shortCode = new CultureInfo(current).TwoLetterISOLanguageName;
            for (var i = 0; i < languages.Count; i++)
            {
                if (languages[i].Code.Equals(shortCode, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }
        catch (CultureNotFoundException ex)
        {
            _logger.LogWarning(ex, "Unknown current culture {Language}", current);
        }

        _logger.LogWarning("Applied language {Language} is not among available languages", current);
        return NoSelection;
    }

    private bool IsThemeApplied(ThemeInfo theme) =>
        _themeManager.CurrentTheme.Equals(theme.Name, StringComparison.OrdinalIgnoreCase);

    private bool IsLanguageApplied(LanguageInfo language) =>
        _localization.CurrentLanguage.Equals(language.Code, StringComparison.OrdinalIgnoreCase);

    private static T? PickAt<T>(IReadOnlyList<T> items, int index)
        where T : class
    {
        return index >= 0 && index < items.Count ? items[index] : null;
    }

    /// <summary>
    /// Seed the external-servers drafts from the persisted list: the rows
    /// carry the configs' fields verbatim and their connection states ride
    /// the manager's latest snapshot (a name the manager has no state for —
    /// an unsaved brand-new row — keeps the honest "not known" dash). The
    /// window open calls this, so abandoned (unsaved) drafts are discarded
    /// the same way every other selection discards.
    /// </summary>
    private async Task RefreshMcpClientRowsAsync(CancellationToken ct)
    {
        var persisted = await _settings.GetAsync<string>(
            McpClientManager.ServersSection,
            McpClientManager.ServersKey,
            McpClientManager.DefaultServersJson);
        ct.ThrowIfCancellationRequested();

        var (servers, problems) = McpServerConfigList.Parse(persisted);
        foreach (var problem in problems)
        {
            _logger.LogWarning("The persisted MCP client list skipped a row: {Problem}", problem);
        }

        var states = _mcpClients.States;
        McpServers.Clear();
        foreach (var server in servers)
        {
            var row = McpServerRow.FromConfig(server);
            row.SetState(states.FirstOrDefault(state => state.Name.Equals(server.Name, StringComparison.Ordinal)));
            McpServers.Add(row);
        }

        _persistedServerJson = persisted;
    }

    /// <summary>
    /// Repaint the draft rows with the manager's fresh snapshot: the states
    /// key by entry name (the key both surfaces share), a row the snapshot
    /// has no entry for keeps the honest "not known" dash.
    /// </summary>
    private void ApplyMcpClientStates(IReadOnlyList<McpClientState> states)
    {
        foreach (var row in McpServers)
        {
            row.SetState(states.FirstOrDefault(state => state.Name.Equals(row.Name, StringComparison.Ordinal)));
        }
    }

    /// <summary>Append one blank external-server draft row; Add stays local, only Save persists.</summary>
    private void AddServerDraftRow() =>
        McpServers.Add(new McpServerRow());

    #endregion Private Methods
}

