namespace ZettaTrade.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Livery.Extensions;
using Livery.Localization;
using Livery.Themes;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Interfaces;
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// ViewModel for status bar.
/// Simple strings use direct XAML binding via {loc:Localized Key}.
/// ViewModel properties only for dynamic data (theme, language, exchange, connection status).
/// </summary>
/// <remarks>
/// The status bar is registered as an application-lifetime singleton, so the theme,
/// language and exchange chips are wired to their managers in the constructor: WhenActivated
/// would never fire for it. Both managers broadcast their current value on
/// subscribe, so the chips start with the state applied at startup. The exchange
/// chips render only when more than one adapter is registered — with a single
/// exchange the toolbar looks exactly as before the selector existed. The
/// adapter health strip rides the same pattern: the fold's snapshots arrive
/// through the injected stream and re-render the dot rows on the UI thread.
/// </remarks>
public sealed class StatusBarViewModel : ReactiveObject
{
    #region Properties

    /// <summary>Default length of the loud fallback notice in the connection chip.</summary>
    private const double DefaultFallbackNoticeSeconds = 5;

    /// <summary>Localized fallback notice: the persisted choice was unavailable.</summary>
    private const string FallbackNoticeKey = "Status.ExchangeFallback";

    /// <summary>Localized strip state labels: the connected/disconnected pair rides the connection chip's existing keys (<see cref="ConnectedStatusKey"/>, <see cref="DisconnectedStatusKey"/>).</summary>
    private const string ReconnectingStateKey = "Status.Health.Reconnecting";
    private const string UnsupervisedStateKey = "Status.Health.Unsupervised";

    /// <summary>Localized strip tooltip templates: the supervised one carries name, state label, last event time and the failure counter.</summary>
    private const string TooltipFormatKey = "Status.Health.Tooltip";
    private const string TooltipUnsupervisedFormatKey = "Status.Health.TooltipUnsupervised";

    /// <summary>Compact clock format of the tooltip's last-event member (the tape's pinned precedent).</summary>
    private const string TimeFormat = "HH:mm:ss";

    private string _currentThemeName = string.Empty;
    private string _currentLanguage = string.Empty;
    private string _connectionStatus = string.Empty;
    private string _selectedExchangeName = string.Empty;
    private string _activeExchangeName = string.Empty;
    private readonly ILocalizationManager _localizationManager;
    private readonly ActiveExchangeService _exchanges;
    private readonly TimeSpan _fallbackNoticeDuration;
    private readonly ILogger<StatusBarViewModel> _logger;

    /// <summary>
    /// Last adapter connection state reported to
    /// <see cref="UpdateConnectionStatus"/>; null before the first report.
    /// </summary>
    private bool? _lastConnectionState;

    /// <summary>
    /// Until the fallback notice owns the connection chip: a regular status
    /// refresh during the notice window is skipped, so the startup poll's
    /// first state report and a language switch cannot silently overwrite
    /// the loud warning.
    /// </summary>
    private DateTimeOffset _noticeHeldUntil = DateTimeOffset.MinValue;

    /// <summary>
    /// The last health snapshot the strip rows were built from (empty until
    /// the first stream emission); a language switch re-composes the
    /// tooltips from it rather than from a stale collection.
    /// </summary>
    private IReadOnlyList<AdapterHealth> _lastAdapterHealth = [];

    /// <summary>Running application version for the status bar chip.</summary>
    public string AppVersion
    {
        get;
    }

    /// <summary>Current theme name.</summary>
    public string CurrentThemeName
    {
        get => _currentThemeName;
        private set => this.RaiseAndSetIfChanged(ref _currentThemeName, value);
    }

    /// <summary>Current language code.</summary>
    public string CurrentLanguage
    {
        get => _currentLanguage;
        private set => this.RaiseAndSetIfChanged(ref _currentLanguage, value);
    }

    /// <summary>Connection status text (or the temporary fallback notice).</summary>
    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => this.RaiseAndSetIfChanged(ref _connectionStatus, value);
    }

    /// <summary>
    /// The exchange picker renders only when more than one adapter is
    /// registered — a selector over one entry is meaningless by design.
    /// </summary>
    public bool HasExchangeChoice
    {
        get;
    }

    /// <summary>Exchange display names in registration order (the picker's source).</summary>
    public IReadOnlyList<string> ExchangeNames
    {
        get;
    }

    /// <summary>
    /// Picker selection (two-way binding). Echoes of facade switches re-set
    /// the same value, which <c>RaiseAndSetIfChanged</c> dedupes; a real
    /// change drives the switch pipeline.
    /// </summary>
    public string SelectedExchangeName
    {
        get => _selectedExchangeName;
        set => this.RaiseAndSetIfChanged(ref _selectedExchangeName, value);
    }

    /// <summary>Display name of the active exchange (the chip's dynamic half).</summary>
    public string ActiveExchangeName
    {
        get => _activeExchangeName;
        private set => this.RaiseAndSetIfChanged(ref _activeExchangeName, value);
    }

    /// <summary>
    /// The adapter health strip's rows (T-098): one display dot per
    /// registered adapter, refreshed wholesale on every health snapshot.
    /// The collection is plain (no per-row change machinery) and the
    /// refreshes land on the user-interface thread through the shared
    /// marshal.
    /// </summary>
    public ObservableCollection<AdapterHealthRow> AdapterHealthRows { get; } = new();

    #endregion Properties

    #region Constructor

    /// <summary>
    /// Create a new StatusBarViewModel instance.
    /// </summary>
    /// <param name="themeManager">Theme manager providing the theme chip state.</param>
    /// <param name="localizationManager">Localization manager providing the language chip state.</param>
    /// <param name="activeExchanges">The active-exchange facade providing the picker and the exchange/mode chip.</param>
    /// <param name="appVersion">Running version for the version chip.</param>
    /// <param name="adapterHealth">
    /// The adapter health fold's snapshot stream (T-098): the strip's rows
    /// re-render on every health emission; the subscription lives as long
    /// as the singleton view model.
    /// </param>
    /// <param name="fallbackNoticeDuration">
    /// How long the fallback notice stays in the connection chip; defaults to
    /// five seconds (the parameter exists so tests can shorten the wait).
    /// </param>
    /// <param name="logger">Logger.</param>
    public StatusBarViewModel(
        IThemeManager themeManager,
        ILocalizationManager localizationManager,
        ActiveExchangeService activeExchanges,
        string appVersion,
        IObservable<IReadOnlyList<AdapterHealth>> adapterHealth,
        TimeSpan? fallbackNoticeDuration = null,
        ILogger<StatusBarViewModel>? logger = null)
    {
        _localizationManager = localizationManager;
        _exchanges = activeExchanges;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<StatusBarViewModel>.Instance;
        AppVersion = appVersion;
        _fallbackNoticeDuration = fallbackNoticeDuration ?? TimeSpan.FromSeconds(DefaultFallbackNoticeSeconds);

        ExchangeNames = [.. activeExchanges.Registered.Select(descriptor => descriptor.Name)];
        HasExchangeChoice = ExchangeNames.Count > 1;

        // The ViewModel lives as long as the application (singleton), so these
        // subscriptions mirror it and need no explicit disposal.
        themeManager
            .ThemeChanged
            .Subscribe(name => CurrentThemeName = ResolveChipName(themeManager, name));

        localizationManager
            .LanguageChanged
            .Subscribe(code =>
            {
                CurrentLanguage = code;
                RefreshConnectionStatus();
            });

        // The behavioral exchange stream: the chip and the picker mirror the
        // facade. Emissions arrive on the starter walk for the restore and on
        // the UI thread for user switches (both are assignment-safe here).
        activeExchanges
            .AdapterChanges
            .Subscribe(
                adapter =>
                {
                    ActiveExchangeName = adapter.Name;
                    SelectedExchangeName = adapter.Name;
                    RefreshConnectionStatus();
                },
                ex => _logger.LogError(ex, "The exchange chip stream stopped unexpectedly"));

        // User-driven picker changes: resolve the name to the registered id
        // and switch (persist-then-activate inside the facade). A failed
        // switch is logged and the machine survives: the next selection
        // proceeds normally, and every successful facade switch echoes back
        // into the picker (an equal re-write is deduped by
        // RaiseAndSetIfChanged, so the pipeline is not re-entered).
        this
            .WhenAnyValue(x => x.SelectedExchangeName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => Observable
                .FromAsync(_ => SwitchAsync(name!))
                .Catch<RxVoid, Exception>(ex =>
                {
                    _logger.LogError(ex, "Switching to exchange '{ExchangeName}' failed", name);
                    return Observable.Empty<RxVoid>();
                }))
            .Switch()
            .Subscribe(
                _ => { },
                ex => _logger.LogError(ex, "The exchange switch pipeline stopped unexpectedly"));

        // The loud fallback warning: the connection chip shows the localized
        // notice for a bounded moment, then returns to the regular state.
        // The chip is UI-bound: the notice signal can arrive from the
        // starter walk and the expiry callback runs on a .NET timer thread,
        // so BOTH land on the UI thread through the shared marshal instead
        // of writing the chip from their own (off-UI) thread (BUG-018).
        activeExchanges
            .UnavailablePersistedChoices
            .Select(_ => RxVoid.Default)
            .ObserveOnUiThread()
            .Subscribe(
                _ => ShowFallbackNotice(),
                ex => _logger.LogError(ex, "The fallback warning stream stopped unexpectedly"));

        // The adapter health strip (T-098): the fold's snapshots re-render
        // the dot rows — every write rides the shared marshal (the fold
        // emits from the supervisor's pump thread), so a state flip renders
        // within one posted beat and never from a foreign thread.
        adapterHealth
            .ObserveOnUiThread()
            .Subscribe(
                RefreshAdapterHealthRows,
                ex => _logger.LogError(ex, "The adapter health stream stopped unexpectedly"));

        // A language switch re-composes the strip's localized tooltips from
        // the stored snapshot: the rows survive the switch, only their
        // composed texts change. The rows rebuild must stay a
        // user-interface-thread write, so this subscription marshals like
        // the strip's own (the language broadcasts may be off-thread).
        localizationManager
            .LanguageChanged
            .Select(_ => RxVoid.Default)
            .ObserveOnUiThread()
            .Subscribe(
                _ => RefreshAdapterHealthRows(_lastAdapterHealth),
                ex => _logger.LogError(ex, "The strip's language re-composition stream stopped unexpectedly"));
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>
    /// Store the adapter connection state and refresh the checkpoint chip
    /// (also invoked on language changes).
    /// </summary>
    /// <param name="isConnected">Current adapter connection state.</param>
    public void UpdateConnectionStatus(bool isConnected)
    {
        _lastConnectionState = isConnected;
        RefreshConnectionStatus();
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>
    /// Localized connection chip text keys.
    /// </summary>
    private const string ConnectedStatusKey = "Status.Connected";
    private const string DisconnectedStatusKey = "Status.Disconnected";

    /// <summary>
    /// Re-render the strip's rows from one health snapshot: the rows
    /// replace wholesale (no per-row machinery), each carrying its
    /// composed localized tooltip. Callers deliver this on the user
    /// interface thread (both strip subscriptions marshal upstream); the
    /// snapshot is stored before the rebuild so a language switch re-composes
    /// from the live values.
    /// </summary>
    private void RefreshAdapterHealthRows(IReadOnlyList<AdapterHealth> snapshot)
    {
        _lastAdapterHealth = snapshot;

        AdapterHealthRows.Clear();
        foreach (var health in snapshot)
        {
            AdapterHealthRows.Add(new AdapterHealthRow(
                health.AdapterName,
                health.State,
                ComposeHealthTooltip(_localizationManager, health)));
        }
    }

    /// <summary>
    /// Compose one row's localized tooltip: the supervised states ride the
    /// four-slot template (name, state label, last event clock, consecutive
    /// failures), the unsupervised state — the two-slot one (an adapter
    /// without supervised transport has no event moment to timestamp). The
    /// state labels reuse the connection chip's keys where they exist.
    /// </summary>
    /// <remarks>
    /// The supervised fold always timestamps its state (<see cref="AdapterHealth.LastEventUtc"/>
    /// is null only for the unsupervised branch below), so the
    /// <c>??</c> slot feed only guards the impossible shape — it degrades
    /// the one slot instead of crashing a paint callback.
    /// </remarks>
    internal static string ComposeHealthTooltip(ILocalizationManager localization, AdapterHealth health)
    {
        var stateLabel = localization.GetString(ResolveStateKey(health.State));

        return health.State == AdapterHealthState.Unsupervised
            ? localization.GetString(TooltipUnsupervisedFormatKey, health.AdapterName, stateLabel)
            : localization.GetString(
                TooltipFormatKey,
                health.AdapterName,
                stateLabel,
                health.LastEventUtc?.LocalDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture) ?? string.Empty,
                health.ConsecutiveFailures);
    }

    /// <summary>The state label's resx key for one strip state (an unknown state is an error — never a substituted label).</summary>
    private static string ResolveStateKey(AdapterHealthState state) => state switch
    {
        AdapterHealthState.Connected => ConnectedStatusKey,
        AdapterHealthState.Reconnecting => ReconnectingStateKey,
        AdapterHealthState.Down => DisconnectedStatusKey,
        AdapterHealthState.Unsupervised => UnsupervisedStateKey,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "The strip state has no localized label."),
    };

    /// <summary>
    /// Resolve the picker's display name to the registered exchange id
    /// (display names are unique among the registered adapters); an unknown
    /// name is logged, never silently substituted.
    /// </summary>
    private string? ResolveExchangeId(string displayName)
    {
        var match = _exchanges.Registered.FirstOrDefault(descriptor =>
            string.Equals(descriptor.Name, displayName, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            _logger.LogWarning(
                "The picker sent an unknown exchange name '{ExchangeName}'; no switch performed",
                displayName);
            return null;
        }

        return match.Id;
    }

    /// <summary>Switch the active exchange to the picker's current display name.</summary>
    private async Task<RxVoid> SwitchAsync(string displayName)
    {
        var exchangeId = ResolveExchangeId(displayName);
        if (exchangeId is null)
        {
            return RxVoid.Default;
        }

        await _exchanges.SwitchToAsync(exchangeId);
        return RxVoid.Default;
    }

    /// <summary>
    /// Show the loud fallback notice in the connection chip for the bounded
    /// duration, then re-derive the regular chip text.
    /// </summary>
    /// <remarks>
    /// The caller delivers this method on the UI thread (the notice
    /// subscription marshals upstream), so the chip write here is
    /// UI-thread-backed. The expiry refresh is a .NET timer callback — a
    /// thread-pool thread — and marshals onto the UI thread before it may
    /// touch the chip (BUG-018).
    /// </remarks>
    private void ShowFallbackNotice()
    {
        _noticeHeldUntil = DateTimeOffset.UtcNow + _fallbackNoticeDuration;
        ConnectionStatus = _localizationManager.GetString(FallbackNoticeKey);

        Observable
            .Timer(_fallbackNoticeDuration)
            .Select(_ => RxVoid.Default)
            .ObserveOnUiThread()
            .Subscribe(
                _ => RefreshConnectionStatus(),
                ex => _logger.LogError(ex, "The fallback notice timer stopped unexpectedly"));
    }

    /// <summary>
    /// Derive the connection chip text from the stored adapter state: empty
    /// before the first report, otherwise the localized
    /// <see cref="ConnectedStatusKey"/> / <see cref="DisconnectedStatusKey"/>
    /// value (re-derived on every language change so a runtime switch keeps
    /// the chip localized). While the fallback notice is on the chip, the
    /// regular derivation is skipped — the warning owns the chip until it
    /// expires.
    /// </summary>
    private void RefreshConnectionStatus()
    {
        if (DateTimeOffset.UtcNow < _noticeHeldUntil)
        {
            return;
        }

        ConnectionStatus = _lastConnectionState is not { } isConnected
            ? string.Empty
            : _localizationManager.GetString(
                isConnected ? ConnectedStatusKey : DisconnectedStatusKey);
    }

    /// <summary>
    /// Chip display name for a canonical theme Name: the matching theme's
    /// DisplayName, or the raw name when the theme is not (yet) registered
    /// (subscribe can run before the manager populated its list).
    /// </summary>
    private static string ResolveChipName(IThemeManager themeManager, string themeName) =>
        themeManager.AvailableThemes
            .FirstOrDefault(t => t.Name.Equals(themeName, StringComparison.OrdinalIgnoreCase))
            ?.DisplayName ?? themeName;

    #endregion Private Methods
}

