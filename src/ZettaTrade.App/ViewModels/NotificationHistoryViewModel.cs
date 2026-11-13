namespace ZettaTrade.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Livery.Extensions;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Interfaces;

/// <summary>The half-day-agnostic status byte ReactiveUI.Primitives names RxVoid.</summary>
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// The notification center window's view model (T-097, transient): every
/// open seeds the session history snapshot once, then rides the live event
/// lane while the window stays (the attach/stopper pair the window's
/// activation scope disposes; a duplicate publish between the attach and
/// the seed lands once — the reference-identity guard). The Do-Not-Disturb
/// flag and the per-source mutes (five since T-204's alert bridge) mirror
/// the reactive settings state
/// read-only; their toggles write through the settings service and every
/// re-application (drawer or external settings edit) rides the change
/// stream without a restart.
/// </summary>
public sealed class NotificationHistoryViewModel : ReactiveObject
{
    #region Resx Keys

    /// <summary>The fills source label.</summary>
    private const string SourceFillsKey = "Notif.SourceFills";

    /// <summary>The cancellations source label.</summary>
    private const string SourceCancelsKey = "Notif.SourceCancels";

    /// <summary>The rejections source label.</summary>
    private const string SourceRejectionsKey = "Notif.SourceRejections";

    /// <summary>The adapter states source label.</summary>
    private const string SourceAdapterStatesKey = "Notif.SourceAdapterStates";

    /// <summary>The alert fires source label (T-204).</summary>
    private const string SourceAlertsKey = "Notif.SourceAlerts";

    /// <summary>The persisted Do-Not-Disturb flip fault (the rows and the toggles stay usable, the flag holds its state).</summary>
    private const string DndSaveFailedKey = "Notif.SaveFailed";

    /// <summary>The persisted source-mute flip fault.</summary>
    private const string MuteSaveFailedKey = "Notif.SaveFailed";

    #endregion Resx Keys

    #region Private Fields

    private readonly NotificationCenter _center;
    private readonly ISettingsService _settings;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<NotificationHistoryViewModel> _logger;

    /// <summary>
    /// The event references the seed already rendered: a publish that
    /// lands between the live attach and the seed read is IN the snapshot
    /// the live lane would prepend again — the guard keeps it once.
    /// </summary>
    private readonly HashSet<Notification> _seeded =
        new(ReferenceEqualityComparer.Instance);

    private string? _errorMessage;
    private bool _isDoNotDisturb;
    private bool _hasRows;

    #endregion Private Fields

    #region Properties

    /// <summary>The history rows, newest first; the live lane prepends while the window stays.</summary>
    public ObservableCollection<NotificationRow> Rows { get; } = new();

    /// <summary>The per-source mute rows (a fixed set, built once per open).</summary>
    public IReadOnlyList<NotificationMuteRow> MuteRows { get; }

    /// <summary>Whether the history carries anything to show (the empty state's flag).</summary>
    public bool HasRows
    {
        get => _hasRows;
        private set => this.RaiseAndSetIfChanged(ref _hasRows, value);
    }

    /// <summary>The Do-Not-Disturb state the settings stream mirrors (read-only; the toggle command writes).</summary>
    public bool IsDoNotDisturb
    {
        get => _isDoNotDisturb;
        private set => this.RaiseAndSetIfChanged(ref _isDoNotDisturb, value);
    }

    /// <summary>Last toggle failure, for the error line in the window footer.</summary>
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

    /// <summary>Flip the Do-Not-Disturb flag (the persisted value is the toggle's negation of the mirrored state).</summary>
    public ReactiveCommand<RxVoid, RxVoid> ToggleDoNotDisturbCommand { get; }

    /// <summary>Clear the session history (the rows and the ring drop together).</summary>
    public ReactiveCommand<RxVoid, RxVoid> ClearHistoryCommand { get; }

    #endregion Properties

    #region Constructor

    /// <summary>
    /// Create the view model: the mute rows and the mirrored streams wire
    /// here. The window's activation does the seeding (the attach-then-seed
    /// pair the guard keeps honest).
    /// </summary>
    /// <param name="center">The notification center the history and the display surfaces read.</param>
    /// <param name="settings">The reactive settings service the flag and the mutes ride.</param>
    /// <param name="localization">The localization catalog the mute labels compose from.</param>
    /// <param name="logger">The view model's own logger.</param>
    public NotificationHistoryViewModel(
        NotificationCenter center,
        ISettingsService settings,
        ILocalizationManager localization,
        ILogger<NotificationHistoryViewModel> logger)
    {
        _center = center ?? throw new ArgumentNullException(nameof(center));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        MuteRows =
        [
            BuildMuteRow(NotificationKind.OrderFill, SourceFillsKey),
            BuildMuteRow(NotificationKind.OrderCancel, SourceCancelsKey),
            BuildMuteRow(NotificationKind.OrderReject, SourceRejectionsKey),
            BuildMuteRow(NotificationKind.AdapterState, SourceAdapterStatesKey),
            BuildMuteRow(NotificationKind.AlertFired, SourceAlertsKey),
        ];

        // The mirrored streams: the app reads the reactive settings state
        // live (the T-053 canon), a language switch re-landed labels ride
        // the composed-once rows (the window rebuilds per open anyway),
        // and each stream's failure lands in the log — the toggles stay
        // usable, the stale mirrors hold their last applied state.
        _center.MutedKinds
            .ObserveOnUiThread()
            .Subscribe(
                ApplyMutedSet,
                ex => _logger.LogError(ex, "The notification mute mirror stopped unexpectedly"));

        _center.DoNotDisturb
            .ObserveOnUiThread()
            .Subscribe(
                flag => IsDoNotDisturb = flag,
                ex => _logger.LogError(ex, "The Do-Not-Disturb mirror stopped unexpectedly"));

        ToggleDoNotDisturbCommand = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(
            (_, cancellationToken) => ToggleDoNotDisturbAsync(cancellationToken));
        ClearHistoryCommand = ReactiveCommand.CreateFromObservable(
            () => Observable.Return(RxVoid.Default).Do(_ => ClearHistorySession()));
    }

    #endregion Constructor

    #region Lifecycle

    /// <summary>
    /// Attach the live history lane; return the stopper the window's
    /// activation scope disposes. Call BEFORE the seed read (the seed
    /// arrives right after — the reference-identity guard deduplicates the
    /// crossing publishes).
    /// </summary>
    /// <returns>The lane subscription; disposing stops the live prepend.</returns>
    public IDisposable AttachLiveHistory() => _center.Events
        .ObserveOnUiThread()
        .Subscribe(
            notification => PrependLiveEvent(notification),
            ex => _logger.LogError(ex, "The notification history live lane stopped unexpectedly"));

    /// <summary>Seed the rows from the session history snapshot (newest first), then reconcile the ring's truth.</summary>
    public void LoadHistory()
    {
        Rows.Clear();
        _seeded.Clear();
        var snapshot = _center.HistorySnapshot;
        foreach (var notification in snapshot)
        {
            _seeded.Add(notification);
            Rows.Add(NotificationRow.ForDisplay(notification, _localization));
        }

        HasRows = Rows.Count > 0;
    }

    /// <summary>Prepend one live event unless the seed already rendered it (the attach/seed crossing guard).</summary>
    private void PrependLiveEvent(Notification notification)
    {
        if (!_seeded.Add(notification))
        {
            return;
        }

        Rows.Insert(0, NotificationRow.ForDisplay(notification, _localization));
        while (Rows.Count > NotificationCenter.MaxHistoryEntries)
        {
            Rows.RemoveAt(Rows.Count - 1);
        }

        HasRows = Rows.Count > 0;
    }

    #endregion Lifecycle

    #region Toggles

    /// <summary>Write the flipped Do-Not-Disturb truth; the mirrored streams re-apply it (a failed write surfaces on the error line, the flag holds its state).</summary>
    private async Task<RxVoid> ToggleDoNotDisturbAsync(CancellationToken cancellationToken)
    {
        ErrorMessage = null;
        try
        {
            await _settings.SetAsync(
                NotificationSettingsKeys.Section,
                NotificationSettingsKeys.DoNotDisturb,
                !IsDoNotDisturb);
        }
        catch (Exception ex)
        {
            // Broader than the settings flows: the persisted flip rides the
            // settings file's own IO (a corrupt file, a locked path); the
            // window stays usable with its error line and the flag keeps
            // its mirrored state (never substituted).
            _logger.LogError(ex, "Notification history window: the Do-Not-Disturb flip failed to persist");
            ErrorMessage = _localization.GetString(DndSaveFailedKey, ex.Message);
        }

        return RxVoid.Default;
    }

    /// <summary>Write the row's flipped mute truth: the whole persisted flip rides the reactive keys.</summary>
    private async Task<RxVoid> ToggleMuteAsync(
        NotificationKind kind,
        CancellationToken cancellationToken)
    {
        ErrorMessage = null;
        var row = MuteRows.First(candidate => candidate.Kind == kind);
        try
        {
            await _settings.SetAsync(
                NotificationSettingsKeys.Section,
                ResolveMuteKey(kind),
                !row.IsMuted);
        }
        catch (Exception ex)
        {
            // Broader than the settings flows (see the DND flip): the mute
            // holds its mirrored state on a failed write and the error
            // line surfaces the fault.
            _logger.LogError(ex, "Notification history window: the {Kind} mute flip failed to persist", kind);
            ErrorMessage = _localization.GetString(MuteSaveFailedKey, ex.Message);
        }

        return RxVoid.Default;
    }

    private NotificationMuteRow BuildMuteRow(NotificationKind kind, string labelKey)
    {
        var label = _localization.GetString(labelKey);
        var toggle = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(
            (_, cancellationToken) => ToggleMuteAsync(kind, cancellationToken));
        return new NotificationMuteRow(kind, label, toggle);
    }

    /// <summary>The settings key one kind's mute rides; an unseen kind is a fault, never a silently written foreign key.</summary>
    private static string ResolveMuteKey(NotificationKind kind) => kind switch
    {
        NotificationKind.OrderFill => NotificationSettingsKeys.MuteFill,
        NotificationKind.OrderCancel => NotificationSettingsKeys.MuteCancel,
        NotificationKind.OrderReject => NotificationSettingsKeys.MuteReject,
        NotificationKind.AdapterState => NotificationSettingsKeys.MuteAdapterState,
        NotificationKind.AlertFired => NotificationSettingsKeys.MuteAlerts,
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind),
            kind,
            "The notification kind has no mute key."),
    };

    /// <summary>Apply one muted-set snapshot onto the fixed mute rows (only real flips re-render).</summary>
    private void ApplyMutedSet(IReadOnlySet<NotificationKind> muted)
    {
        foreach (var row in MuteRows)
        {
            row.IsMuted = muted.Contains(row.Kind);
        }
    }

    #endregion Toggles

    #region Clearing

    /// <summary>Drop the ring and the shown rows together; the live lane re-seeds from the publishes that follow.</summary>
    private void ClearHistorySession()
    {
        _center.ClearHistory();
        Rows.Clear();
        _seeded.Clear();
        HasRows = false;
    }

    #endregion Clearing
}
