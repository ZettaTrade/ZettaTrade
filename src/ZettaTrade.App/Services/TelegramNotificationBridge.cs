namespace ZettaTrade.App.Services;

using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Livery.Contracts;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Enums;
using ZettaTrade.Infrastructure.Notifications;

/// <summary>
/// The Telegram trade-notification bridge (T-041): it rides the notification
/// center's trade events (the journal tap's fills, accepted cancels and
/// rejections — the same raw lane the T-122 footprint feed triggers from),
/// composes each event's message in the standing trading lane and dispatches
/// the best-effort send. The bridge is a pure observer end to end: it never
/// places, amends, cancels or validates anything, it never retries, and a
/// failed send stays inside the notifier's own degrade lane — the trading
/// flow never notices a push happened at all.
/// <para>
/// The lane tag reads at event time: the lane stream is subscribed first
/// (its standing value seeds the field synchronously — the resolved lane is
/// the behavioral stream's own subscribe-time emission), so every event is
/// tagged with the lane that was standing when it arrived. An unseen kind
/// never reaches the composer (the filter keeps the three journaled
/// outcomes; the raw lane carries no digests), and one event's compose fault
/// degrades into this bridge's own lane — the event stream never dies over
/// a notification.
/// </para>
/// </summary>
public sealed class TelegramNotificationBridge : IAsyncInitializable, IDisposable
{
    #region Private Fields

    private readonly IObservable<Notification> _tradeEvents;
    private readonly IObservable<TradingLane> _laneStream;
    private readonly TelegramNotifierService _notifier;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<TelegramNotificationBridge> _logger;

    /// <summary>
    /// The lane standing when the newest event arrived (the message's
    /// paper/live tag). Seeded synchronously at initialization by the lane
    /// stream's own standing emission; the shipped stand-up's paper lane is
    /// the honest default the resolver starts from.
    /// </summary>
    private TradingLane _lane = TradingLane.Paper;

    /// <summary>
    /// The newest dispatched send's handle (the fire-and-forget canon: the
    /// send rides a field, never a naked discard). The send's own body
    /// degrades every fault, so the tracked handles can never carry an
    /// unobserved fault; each dispatch replaces the handle once its own
    /// attempt started (an in-flight send's lifecycle is the notifier's own,
    /// the bridge never blocks an event on it).
    /// </summary>
    private Task _lastSend = Task.CompletedTask;

    /// <summary>The event subscription (disposed at teardown).</summary>
    private IDisposable? _eventSubscription;

    /// <summary>The lane subscription (disposed at teardown).</summary>
    private IDisposable? _laneSubscription;

    #endregion Private Fields

    #region Constructor

    /// <summary>Create the bridge over the notification center's trade events, the lane stream and the sink.</summary>
    /// <param name="tradeEvents">The notification center's raw event lane (the journaled trade outcomes ride it).</param>
    /// <param name="laneStream">The trading-mode service's behavioral lane stream (the tag source).</param>
    /// <param name="notifier">The best-effort Telegram sink.</param>
    /// <param name="localization">The catalog the message lines compose from.</param>
    /// <param name="logger">The bridge's own degrade lane.</param>
    public TelegramNotificationBridge(
        IObservable<Notification> tradeEvents,
        IObservable<TradingLane> laneStream,
        TelegramNotifierService notifier,
        ILocalizationManager localization,
        ILogger<TelegramNotificationBridge> logger)
    {
        _tradeEvents = tradeEvents ?? throw new ArgumentNullException(nameof(tradeEvents));
        _laneStream = laneStream ?? throw new ArgumentNullException(nameof(laneStream));
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #endregion Constructor

    #region Teardown

    /// <summary>Stop observing the lane and the trade events (the application teardown's subscription release).</summary>
    public void Dispose()
    {
        _eventSubscription?.Dispose();
        _laneSubscription?.Dispose();
    }

    #endregion Teardown

    #region Initialization

    /// <summary>
    /// Attach the subscriptions: the lane stream seeds the standing tag
    /// first (the behavioral stream emits synchronously on subscribe), then
    /// the filtered trade events start dispatching — no event published
    /// before the walk reaches this bridge is observable anyway.
    /// </summary>
    /// <inheritdoc cref="IAsyncInitializable.InitializeAsync"/>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        _laneSubscription = _laneStream.Subscribe(
            lane => _lane = lane,
            error => _logger.LogError(error, "The trading-lane stream stopped; the tag holds the last standing lane"));

        // The journal tap's three trade outcomes only; the other kinds are
        // the toast/adapter/alert surfaces' business, not the sink's.
        _eventSubscription = _tradeEvents
            .Where(static notification => notification.Kind is
                NotificationKind.OrderFill or
                NotificationKind.OrderCancel or
                NotificationKind.OrderReject)
            .Subscribe(
                HandleTradeEvent,
                error => _logger.LogError(error, "The trade-event stream stopped; the Telegram sink goes quiet"));

        return Task.CompletedTask;
    }

    #endregion Initialization

    #region Test Seam

    /// <summary>The newest dispatched send's handle: the e2e probe awaits the push deterministically instead of racing the stub.</summary>
    internal Task LastSendForTests => _lastSend;

    /// <summary>The lane tag standing right now (the tag-source fact the goldens pin).</summary>
    internal TradingLane LaneForTests => _lane;

    #endregion Test Seam

    #region Dispatch

    /// <summary>
    /// One trade event's whole dispatch: compose, then send. The compose
    /// fault degrades into this bridge's own lane (the event stream never
    /// dies over a notification); the send dispatches and the call returns
    /// promptly — the send's own body degrades every fault (the never-faults
    /// contract), so the tracked handle never carries an unobserved fault
    /// and a firing burst keeps every push running to its own logged end.
    /// </summary>
    private void HandleTradeEvent(Notification notification)
    {
        try
        {
            var message = TelegramMessageComposer.Compose(notification, _lane, _localization);
            var kind = notification.Kind switch
            {
                NotificationKind.OrderFill => TelegramTradeKind.Fill,
                NotificationKind.OrderCancel => TelegramTradeKind.Cancel,
                NotificationKind.OrderReject => TelegramTradeKind.Reject,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(notification),
                    notification.Kind,
                    "The trade-event filter passed a kind the sink has no vocabulary for."),
            };
            _lastSend = _notifier.SendAsync(message, kind);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "The Telegram bridge could not dispatch the {Kind} event; the trading flow is untouched",
                notification.Kind);
        }
    }

    #endregion Dispatch
}
