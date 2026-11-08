namespace ZettaTrade.App.ViewModels;

using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Livery.Extensions;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.App.Services;

/// <summary>The half-day-agnostic status byte ReactiveUI.Primitives names RxVoid.</summary>
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// The toast host (T-097): the app-lifetime stack the display lane feeds,
/// rendered top-right over the workspace through the main window's overlay.
/// Every arrival composes a row and schedules its own expiry (the timing
/// lives on the center's publishing threads — the host only marshals the
/// writes), a burst beyond the visible cap drops the oldest row slotted at
/// the tail, and a click routes through the main window's panel plumbing
/// (the T-012 routing discipline: the host poses the route, the window
/// opens the panel).
/// </summary>
public sealed class NotificationToastHostViewModel : ReactiveObject, INotificationRouteSink
{
    #region Constants

    /// <summary>How many toasts the stack shows at once; the overflow leaves the tail first (newest on top).</summary>
    internal const int MaxVisibleToasts = 5;

    /// <summary>How long one toast stays visible before it leaves on its own (seconds).</summary>
    internal const int ToastLifetimeSeconds = 5;

    #endregion Constants

    #region Private Fields

    private readonly NotificationCenter _center;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<NotificationToastHostViewModel> _logger;

    /// <summary>The per-row expiry subscriptions; disposal rides the row's own removal.</summary>
    private readonly ConcurrentDictionary<NotificationRow, IDisposable> _expiries = new();

    private bool _hasToasts;

    #endregion Private Fields

    #region Properties

    /// <summary>The rows on display, newest at the head (the stack writes stay on the user-interface thread).</summary>
    public ObservableCollection<NotificationRow> Toasts { get; } = new();

    /// <summary>Whether the stack carries anything to show (the overlay's visibility flag).</summary>
    public bool HasToasts
    {
        get => _hasToasts;
        private set => this.RaiseAndSetIfChanged(ref _hasToasts, value);
    }

    /// <summary>
    /// The panel route the main window answers: the stats/journal/history
    /// panel a clicked row opens (the host dismisses first; the window
    /// plumbing stays out of the ViewModel).
    /// </summary>
    public Interaction<NotificationRoute, RxVoid> OpenPanelRequested { get; }

    #endregion Properties

    #region Constructor

    /// <summary>
    /// Create the host and pin the lane: one subscription of the center's
    /// display pipeline for the whole application lifetime — the host is an
    /// application-lifetime singleton, so its subscriptions mirror that
    /// lifetime and need no explicit disposal (the palette's singleton
    /// subscription contract).
    /// </summary>
    /// <param name="center">The notification center the display lane rides.</param>
    /// <param name="localization">The catalog the composed rows' summaries ride.</param>
    /// <param name="logger">The host's own logger.</param>
    public NotificationToastHostViewModel(
        NotificationCenter center,
        ILocalizationManager localization,
        ILogger<NotificationToastHostViewModel> logger)
    {
        _center = center ?? throw new ArgumentNullException(nameof(center));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        OpenPanelRequested = new Interaction<NotificationRoute, RxVoid>();

        _center.Toasts
            .ObserveOnUiThread()
            .Subscribe(
                ShowToast,
                ex => _logger.LogError(ex, "The notification toast stream stopped unexpectedly"));
    }

    #endregion Constructor

    #region Toast Lifecycle

    /// <summary>Show one digest or raw event: the row leads the stack, the tail trims to the cap, the expiry is scheduled.</summary>
    /// <param name="notification">The digest or raw event the display lane delivered.</param>
    private void ShowToast(Notification notification)
    {
        var row = NotificationRow.ForToasting(notification, this, _localization);
        Toasts.Insert(0, row);
        while (Toasts.Count > MaxVisibleToasts)
        {
            RemoveToast(Toasts[^1]);
        }

        _expiries[row] = Observable
            .Timer(TimeSpan.FromSeconds(ToastLifetimeSeconds))
            .ObserveOnUiThread()
            .Subscribe(
                _ => RemoveToast(row),
                ex => _logger.LogError(ex, "A toast expiry timer failed"));

        HasToasts = Toasts.Count > 0;
    }

    /// <summary>Remove one row from the stack and tear its expiry down (a click and the timer both land here).</summary>
    /// <param name="row">The row leaving.</param>
    private void RemoveToast(NotificationRow row)
    {
        if (_expiries.TryRemove(row, out var expiry))
        {
            expiry.Dispose();
        }

        Toasts.Remove(row);
        HasToasts = Toasts.Count > 0;
    }

    #endregion Toast Lifecycle

    #region INotificationRouteSink

    /// <summary>
    /// Activate one row: dismiss the toast first (a routed click takes the
    /// row off the stack either way), then pose the route; a no-route
    /// toast's dismissal is the whole action.
    /// </summary>
    public async Task OpenAsync(NotificationRow row, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);

        RemoveToast(row);
        if (row.Source.Route == NotificationRoute.None)
        {
            return;
        }

        await OpenPanelRequested.Handle(row.Source.Route);
    }

    #endregion INotificationRouteSink
}
