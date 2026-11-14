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
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Interfaces;
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// ViewModel for status bar.
/// Simple strings use direct XAML binding via {loc:Localized Key}.
/// ViewModel properties only for dynamic data (exchange, connection status,
/// the message channel).
/// </summary>
/// <remarks>
/// The status bar is registered as an application-lifetime singleton, so the
/// message channel, the language-driven re-compositions and the exchange chips
/// are wired in the constructor: WhenActivated would never fire for it. The
/// theme and language chips left the strip in T-153 (both switch in the
/// settings, the version lives in Help/About) — the localization manager's
/// language stream here serves only the re-derivations of the localized chip
/// texts. The exchange chips render only when more than one adapter is
/// registered — with a single exchange the toolbar looks exactly as before
/// the selector existed. The adapter health strip rides the same pattern: the
/// fold's snapshots arrive through the injected stream and re-render the dot
/// rows on the UI thread.
/// </remarks>
public sealed class StatusBarViewModel : ReactiveObject
{
    #region Properties

    /// <summary>Default length of the loud fallback notice in the connection chip.</summary>
    private const double DefaultFallbackNoticeSeconds = 5;

    /// <summary>Default display time of one message the channel shows over the idle text.</summary>
    private const double DefaultStatusMessageSeconds = 5;

    /// <summary>Localized fallback notice: the persisted choice was unavailable.</summary>
    private const string FallbackNoticeKey = "Status.ExchangeFallback";

    /// <summary>The message channel's idle text: what the first strip cell shows between events.</summary>
    private const string ReadyStatusKey = "Status.Ready";

    /// <summary>Localized strip state labels: the connected/disconnected pair rides the connection chip's existing keys (<see cref="ConnectedStatusKey"/>, <see cref="DisconnectedStatusKey"/>).</summary>
    private const string ReconnectingStateKey = "Status.Health.Reconnecting";
    private const string UnsupervisedStateKey = "Status.Health.Unsupervised";

    /// <summary>Localized strip tooltip templates: the supervised one carries name, state label, last event time and the failure counter.</summary>
    private const string TooltipFormatKey = "Status.Health.Tooltip";
    private const string TooltipUnsupervisedFormatKey = "Status.Health.TooltipUnsupervised";

    /// <summary>Compact clock format of the tooltip's last-event member (the tape's pinned precedent).</summary>
    private const string TimeFormat = "HH:mm:ss";

    private string _connectionStatus = string.Empty;
    private string _selectedExchangeName = string.Empty;
    private string _activeExchangeName = string.Empty;
    private readonly ILocalizationManager _localizationManager;
    private readonly ActiveExchangeService _exchanges;
    private readonly TimeSpan _fallbackNoticeDuration;
    private readonly TimeSpan _statusMessageDuration;
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
    /// Until the displayed message owns the first strip cell: the idle
    /// re-derivations (a language switch, a stray expiry callback) skip
    /// while a message holds, so a live message never silently reverts to
    /// the Ready text.
    /// </summary>
    private DateTimeOffset _messageHeldUntil = DateTimeOffset.MinValue;

    /// <summary>
    /// The last health snapshot the strip rows were built from (empty until
    /// the first stream emission); a language switch re-composes the
    /// tooltips from it rather than from a stale collection.
    /// </summary>
    private IReadOnlyList<AdapterHealth> _lastAdapterHealth = [];

    /// <summary>Connection status text (or the temporary fallback notice).</summary>
    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => this.RaiseAndSetIfChanged(ref _connectionStatus, value);
    }

    /// <summary>
    /// The first strip cell's text (T-153): the localized Ready idle text
    /// between events, a transient event message while the channel holds
    /// one. Messages compose at arrival (the notification row's snapshot
    /// discipline) and vanish back to the idle text after the quiet
    /// period.
    /// </summary>
    public string StatusMessage
    {
        get => _statusMessage;
        private set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    private string _statusMessage = string.Empty;

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
        set
        {
            if (value is null)
            {
                // The bind/init race's null write (T-225): a null is never
                // a user choice — the standing exchange name stays (the
                // switch pipeline is null-guarded anyway) and the deferred
                // raise re-asserts it into the picker once its items exist.
                SelectionReAssert.Later(this, nameof(SelectedExchangeName));
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedExchangeName, value);
        }
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

    private string _tradingLaneLabel = string.Empty;
    private string _tradingLaneTooltip = string.Empty;
    private bool _isPaperLane;
    private bool _isTestnetLane;
    private bool _isLiveLane;
    private readonly TradingModeService _tradingMode;

    /// <summary>Localized lane labels: the chip's caption per resolved lane.</summary>
    private const string LaneLabelPaperKey = "Trading.Lane.Paper";
    private const string LaneLabelTestnetKey = "Trading.Lane.Testnet";
    private const string LaneLabelLiveKey = "Trading.Lane.Live";

    /// <summary>Localized lane tooltips: what each lane means at one glance.</summary>
    private const string LaneTooltipPaperKey = "Trading.Lane.TooltipPaper";
    private const string LaneTooltipTestnetKey = "Trading.Lane.TooltipTestnet";
    private const string LaneTooltipLiveKey = "Trading.Lane.TooltipLive";

    /// <summary>The chip's caption for the resolved lane (localized; re-derived on language changes).</summary>
    public string TradingLaneLabel
    {
        get => _tradingLaneLabel;
        private set => this.RaiseAndSetIfChanged(ref _tradingLaneLabel, value);
    }

    /// <summary>The chip's tooltip for the resolved lane (localized; re-derived on language changes).</summary>
    public string TradingLaneTooltip
    {
        get => _tradingLaneTooltip;
        private set => this.RaiseAndSetIfChanged(ref _tradingLaneTooltip, value);
    }

    /// <summary>Class flags the chip's per-state styles bind to (the lane's calm/prompt/danger tint).</summary>
    public bool IsPaperLane
    {
        get => _isPaperLane;
        private set => this.RaiseAndSetIfChanged(ref _isPaperLane, value);
    }

    /// <inheritdoc cref="IsPaperLane"/>
    public bool IsTestnetLane
    {
        get => _isTestnetLane;
        private set => this.RaiseAndSetIfChanged(ref _isTestnetLane, value);
    }

    /// <inheritdoc cref="IsPaperLane"/>
    public bool IsLiveLane
    {
        get => _isLiveLane;
        private set => this.RaiseAndSetIfChanged(ref _isLiveLane, value);
    }

    /// <summary>Localized update tooltip: the available release's version interpolates in.</summary>
    private const string UpdateTooltipKey = "Status.UpdateAvailable.Tooltip";

    /// <summary>
    /// The newest release the checker found above the running version
    /// (null when the last completed check found nothing newer): the
    /// update chip's state and its tooltip's version ride it (T-213).
    /// </summary>
    private UpdateRelease? _lastUpdateRelease;

    private bool _hasUpdate;

    /// <summary>
    /// Whether the last completed update check found a newer release:
    /// the update chip's visibility. A failed check never touches it
    /// (the checker emits nothing on a failure), so the chip never
    /// flaps on an unreachable network.
    /// </summary>
    public bool HasUpdate
    {
        get => _hasUpdate;
        private set => this.RaiseAndSetIfChanged(ref _hasUpdate, value);
    }

    /// <summary>The chip's tooltip: the localized announcement with the available version (empty while hidden).</summary>
    public string UpdateTooltip
    {
        get => _updateTooltip;
        private set => this.RaiseAndSetIfChanged(ref _updateTooltip, value);
    }

    private string _updateTooltip = string.Empty;

    /// <summary>Compose the update tooltip from the standing release (empty while none stands).</summary>
    private string ComposeUpdateTooltip() => _lastUpdateRelease is { } release
        ? _localizationManager.GetString(UpdateTooltipKey, release.Version)
        : string.Empty;

    #endregion Properties

    #region Constructor

    /// <summary>
    /// Create a new StatusBarViewModel instance.
    /// </summary>
    /// <param name="localizationManager">Localization manager providing the localized chip texts.</param>
    /// <param name="activeExchanges">The active-exchange facade providing the picker and the exchange/mode chip.</param>
    /// <param name="tradingMode">The trading-mode service providing the lane chip's resolved lane stream (T-123).</param>
    /// <param name="statusEvents">
    /// The notification center's raw event lane (T-153): the message
    /// channel's source. Each event surfaces its localized summary over the
    /// Ready idle text for the bounded display time; a later event replaces
    /// the displayed one (the Switch-ed expiry timer of the replaced
    /// message unsubscribes with it).
    /// </param>
    /// <param name="adapterHealth">
    /// The adapter health fold's snapshot stream (T-098): the strip's rows
    /// re-render on every health emission; the subscription lives as long
    /// as the singleton view model.
    /// </param>
    /// <param name="fallbackNoticeDuration">
    /// How long the fallback notice stays in the connection chip; defaults to
    /// five seconds (the parameter exists so tests can shorten the wait).
    /// </param>
    /// <param name="statusMessageDuration">
    /// How long one message stays over the Ready idle text; defaults to
    /// five seconds (the parameter exists so tests can shorten the wait).
    /// </param>
    /// <param name="logger">Logger.</param>
    /// <param name="updateReleases">
    /// The update checker's verdict stream (T-213): the update chip's
    /// state. Each completed check's emission shows (or clears) the chip;
    /// a failed check emits nothing, so the standing chip never flaps.
    /// Null in tests that construct the bar without the checker.
    /// </param>
    public StatusBarViewModel(
        ILocalizationManager localizationManager,
        ActiveExchangeService activeExchanges,
        TradingModeService tradingMode,
        IObservable<Notification> statusEvents,
        IObservable<IReadOnlyList<AdapterHealth>> adapterHealth,
        TimeSpan? fallbackNoticeDuration = null,
        TimeSpan? statusMessageDuration = null,
        ILogger<StatusBarViewModel>? logger = null,
        IObservable<UpdateRelease?>? updateReleases = null)
    {
        _localizationManager = localizationManager;
        _exchanges = activeExchanges;
        _tradingMode = tradingMode
            ?? throw new ArgumentNullException(nameof(tradingMode));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<StatusBarViewModel>.Instance;
        _fallbackNoticeDuration = fallbackNoticeDuration ?? TimeSpan.FromSeconds(DefaultFallbackNoticeSeconds);
        _statusMessageDuration = statusMessageDuration ?? TimeSpan.FromSeconds(DefaultStatusMessageSeconds);
        StatusMessage = _localizationManager.GetString(ReadyStatusKey);

        ExchangeNames = [.. activeExchanges.Registered.Select(descriptor => descriptor.Name)];
        HasExchangeChoice = ExchangeNames.Count > 1;

        // The ViewModel lives as long as the application (singleton), so these
        // subscriptions mirror it and need no explicit disposal.
        localizationManager
            .LanguageChanged
            .Subscribe(_ => RefreshConnectionStatus());

        // The message channel (T-153): each notification surfaces its
        // localized one-line summary over the Ready idle text for the
        // bounded display time. The Switch-ed expiry timer implements the
        // replace semantics — a later event unsubscribes the message it
        // replaces along with that message's pending expiry — and every
        // write lands on the user interface thread through the shared
        // marshal (events may publish from any thread).
        statusEvents
            .ObserveOnUiThread()
            .Do(ShowStatusMessage)
            .Select(_ => Observable
                .Timer(_statusMessageDuration)
                .Select(_ => RxVoid.Default)
                .ObserveOnUiThread())
            .Switch()
            .Subscribe(
                _ => RefreshIdleStatusMessage(),
                ex => _logger.LogError(ex, "The status message channel stopped unexpectedly"));

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

        // A language switch re-composes the strip's localized texts from the
        // stored facts: the health rows from their snapshot and the message
        // channel's idle text from the catalog (a DISPLAYED message keeps
        // its arrival-composed text — the row snapshot discipline — only
        // the idle text follows the language). The writes must stay
        // user-interface-thread writes, so this subscription marshals like
        // the strip's own (the language broadcasts may be off-thread).
        localizationManager
            .LanguageChanged
            .Select(_ => RxVoid.Default)
            .ObserveOnUiThread()
            .Subscribe(
                _ =>
                {
                    RefreshAdapterHealthRows(_lastAdapterHealth);
                    RefreshIdleStatusMessage();
                    UpdateTooltip = ComposeUpdateTooltip();
                },
                ex => _logger.LogError(ex, "The strip's language re-composition stream stopped unexpectedly"));

        // The trading lane chip's captions and tooltips are localized data:
        // the same language switch re-derives the chip from its standing
        // lane, so the chip never renders a stale-culture caption.
        localizationManager
            .LanguageChanged
            .Select(_ => RxVoid.Default)
            .ObserveOnUiThread()
            .Subscribe(
                _ => RefreshTradingLaneChip(_tradingMode.CurrentLane),
                ex => _logger.LogError(ex, "The lane chip's language re-composition stream stopped unexpectedly"));

        // The trading lane chip (T-123): the mode service's behavioral
        // stream delivers the standing lane on subscribe (the chip starts
        // rendered from the same resolution the pipeline used at rise) and
        // every later flip after that. The status bar is an
        // application-lifetime singleton, so the subscription mirrors its
        // lifetime; a future flip site may emit off the user-interface
        // thread, so the chip writes marshal like the other chip writers
        // (BUG-018's shape).
        tradingMode
            .Lane
            .ObserveOnUiThread()
            .Subscribe(
                RefreshTradingLaneChip,
                ex => _logger.LogError(ex, "The trading lane chip stream stopped unexpectedly"));

        // The update chip (T-213): the checker's verdict stream shows (or
        // clears) the chip on every COMPLETED check; a failed check emits
        // nothing, so the standing chip never flaps on an unreachable
        // network. The tooltip's version rides the standing release, so a
        // language switch re-derives it from the kept snapshot (the strip's
        // own re-composition discipline).
        updateReleases?
            .ObserveOnUiThread()
            .Subscribe(
                release =>
                {
                    _lastUpdateRelease = release;
                    HasUpdate = release is not null;
                    UpdateTooltip = ComposeUpdateTooltip();
                },
                ex => _logger.LogError(ex, "The update chip stream stopped unexpectedly"));
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
    /// Show one event's message over the Ready idle text: the localized
    /// summary composes at arrival (the notification row's snapshot
    /// discipline — a later language switch never re-renders a displayed
    /// message) and the hold stamps the moment the idle re-derivations
    /// must skip until.
    /// </summary>
    /// <remarks>
    /// The caller delivers this method on the user interface thread (the
    /// channel's subscription marshals upstream), so the cell write here is
    /// UI-thread-backed.
    /// </remarks>
    private void ShowStatusMessage(Notification notification)
    {
        _messageHeldUntil = DateTimeOffset.UtcNow + _statusMessageDuration;
        StatusMessage = NotificationRow.ForDisplay(notification, _localizationManager).Summary;
    }

    /// <summary>
    /// Re-derive the first strip cell's idle text from the catalog: the
    /// localized Ready value. While a message holds the cell, the
    /// derivation is skipped — the message owns the cell until its quiet
    /// period expires (a language switch must not silently revert a live
    /// message to the idle text).
    /// </summary>
    private void RefreshIdleStatusMessage()
    {
        if (DateTimeOffset.UtcNow < _messageHeldUntil)
        {
            return;
        }

        StatusMessage = _localizationManager.GetString(ReadyStatusKey);
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
    /// Re-render the trading lane chip from one resolved lane: the class
    /// flags (the per-state tint) and the localized caption/tooltip swap
    /// together, so a lane change and a language change can never leave the
    /// chip with a mismatched pair. Callers deliver this on the
    /// user-interface thread (both chip subscriptions marshal upstream).
    /// </summary>
    private void RefreshTradingLaneChip(TradingLane lane)
    {
        IsPaperLane = lane == TradingLane.Paper;
        IsTestnetLane = lane == TradingLane.Testnet;
        IsLiveLane = lane == TradingLane.Live;
        TradingLaneLabel = _localizationManager.GetString(ResolveLaneLabelKey(lane));
        TradingLaneTooltip = _localizationManager.GetString(ResolveLaneTooltipKey(lane));
    }

    /// <summary>The chip caption's resx key for one lane (an unknown lane is an error — never a substituted label).</summary>
    private static string ResolveLaneLabelKey(TradingLane lane) => lane switch
    {
        TradingLane.Paper => LaneLabelPaperKey,
        TradingLane.Testnet => LaneLabelTestnetKey,
        TradingLane.Live => LaneLabelLiveKey,
        _ => throw new ArgumentOutOfRangeException(nameof(lane), lane, "The trading lane has no localized label."),
    };

    /// <summary>The chip tooltip's resx key for one lane (an unknown lane is an error — never a substituted tooltip).</summary>
    private static string ResolveLaneTooltipKey(TradingLane lane) => lane switch
    {
        TradingLane.Paper => LaneTooltipPaperKey,
        TradingLane.Testnet => LaneTooltipTestnetKey,
        TradingLane.Live => LaneTooltipLiveKey,
        _ => throw new ArgumentOutOfRangeException(nameof(lane), lane, "The trading lane has no localized tooltip."),
    };

    #endregion Private Methods
}

