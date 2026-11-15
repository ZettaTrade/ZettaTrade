namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Livery.Contracts;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Enums;
using ZettaTrade.Infrastructure.Notifications;

/// <summary>
/// The trade-notification bridge (T-260): it rides the notification
/// center's trade events (the journal tap's fills, accepted cancels and
/// rejections — the same raw lane the T-122 footprint feed triggers from),
/// maps each event ONCE into the sink vocabulary's structured record and
/// dispatches it to every registered <see cref="INotificationSink"/>. The
/// bridge is a pure observer end to end: it never places, amends, cancels
/// or validates anything, it never retries, and a failed delivery stays
/// inside the sink's own degrade lane — the trading flow never notices a
/// push happened at all.
/// <para>
/// The lane tag reads at event time: the lane stream is subscribed first
/// (its standing value seeds the field synchronously — the resolved lane
/// is the behavioral stream's own subscribe-time emission), so every event
/// is tagged with the lane that was standing when it arrived. An unseen
/// kind never reaches the mapping (the filter keeps the three journaled
/// outcomes; the raw lane carries no digests), and one event's mapping
/// fault degrades into this bridge's own lane — the event stream never
/// dies over a notification. The dispatch fans out concurrently
/// (<c>Task.WhenAll</c>) and the fire-and-forget canon holds: the tracked
/// dispatch rides a field, never a naked discard, and each sink's
/// never-faults contract keeps the aggregate quiet — a contract-breaking
/// sink still degrades here without touching the stream.
/// </para>
/// </summary>
public sealed class TradeNotificationBridge : IAsyncInitializable, IDisposable
{
    #region Private Fields

    private readonly IObservable<Notification> _tradeEvents;
    private readonly IObservable<TradingLane> _laneStream;
    private readonly IReadOnlyList<INotificationSink> _sinks;
    private readonly ILogger<TradeNotificationBridge> _logger;

    /// <summary>
    /// The lane standing when the newest event arrived (the message's
    /// paper/live tag). Seeded synchronously at initialization by the lane
    /// stream's own standing emission; the shipped stand-up's paper lane is
    /// the honest default the resolver starts from.
    /// </summary>
    private TradingLane _lane = TradingLane.Paper;

    /// <summary>
    /// The newest dispatched fan-out's handle (the fire-and-forget canon:
    /// the dispatch rides a field, never a naked discard). Every sink's
    /// HandleAsync never faults by contract, so the tracked handle can
    /// never carry an unobserved fault; each dispatch replaces the handle
    /// once its own attempt started (an in-flight dispatch's lifecycle is
    /// the sinks' own, the bridge never blocks an event on it).
    /// </summary>
    private Task _lastDispatch = Task.CompletedTask;

    /// <summary>The event subscription (disposed at teardown).</summary>
    private IDisposable? _eventSubscription;

    /// <summary>The lane subscription (disposed at teardown).</summary>
    private IDisposable? _laneSubscription;

    #endregion Private Fields

    #region Constructor

    /// <summary>Create the bridge over the notification center's trade events, the lane stream and the registered sink fleet.</summary>
    /// <param name="tradeEvents">The notification center's raw event lane (the journaled trade outcomes ride it).</param>
    /// <param name="laneStream">The trading-mode service's behavioral lane stream (the tag source).</param>
    /// <param name="sinks">The registered sink fleet (every INotificationSink the composition has registered by construction time).</param>
    /// <param name="logger">The bridge's own degrade lane.</param>
    public TradeNotificationBridge(
        IObservable<Notification> tradeEvents,
        IObservable<TradingLane> laneStream,
        IEnumerable<INotificationSink> sinks,
        ILogger<TradeNotificationBridge> logger)
    {
        _tradeEvents = tradeEvents ?? throw new ArgumentNullException(nameof(tradeEvents));
        _laneStream = laneStream ?? throw new ArgumentNullException(nameof(laneStream));
        _sinks = (sinks ?? throw new ArgumentNullException(nameof(sinks))).ToList();
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
        // the toast/adapter/alert surfaces' business, not the sinks'.
        _eventSubscription = _tradeEvents
            .Where(static notification => notification.Kind is
                NotificationKind.OrderFill or
                NotificationKind.OrderCancel or
                NotificationKind.OrderReject)
            .Subscribe(
                HandleTradeEvent,
                error => _logger.LogError(error, "The trade-event stream stopped; the notification sinks go quiet"));

        return Task.CompletedTask;
    }

    #endregion Initialization

    #region Test Seam

    /// <summary>The newest dispatched fan-out's handle: the e2e probe awaits the dispatch deterministically instead of racing the sinks.</summary>
    internal Task LastDispatchForTests => _lastDispatch;

    /// <summary>The lane tag standing right now (the tag-source fact the goldens pin).</summary>
    internal TradingLane LaneForTests => _lane;

    #endregion Test Seam

    #region Dispatch

    /// <summary>
    /// One trade event's whole dispatch: map, then fan out. The mapping
    /// fault degrades into this bridge's own lane (the event stream never
    /// dies over a notification); the dispatch starts and the call returns
    /// promptly — every sink's own body degrades its faults (the
    /// never-faults contract), so the tracked handle carries no unobserved
    /// fault and a firing burst keeps every push running to its own logged
    /// end.
    /// </summary>
    private void HandleTradeEvent(Notification notification)
    {
        try
        {
            var tradeEvent = MapEvent(notification, MapLane(_lane));
            _lastDispatch = DispatchAsync(tradeEvent);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "The trade-notification bridge could not dispatch the {Kind} event; the trading flow is untouched",
                notification.Kind);
        }
    }

    /// <summary>
    /// The fan-out: every registered sink rides the same mapped event
    /// concurrently, and the aggregate still degrades — a sink that breaks
    /// the never-faults contract (a synchronous throw, a faulting task)
    /// lands in this bridge's own lane as one warning while the sinks that
    /// kept the contract delivered their event anyway. Each call starts in
    /// its own guard on purpose: one member's synchronous throw can never
    /// starve the rest of the fleet.
    /// </summary>
    private async Task DispatchAsync(TradeNotificationEvent tradeEvent)
    {
        var handles = new List<Task>(_sinks.Count);
        foreach (var sink in _sinks)
        {
            try
            {
                handles.Add(sink.HandleAsync(tradeEvent));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "A trade-notification sink threw before its dispatch task existed; the fleet keeps serving");
            }
        }

        try
        {
            await Task.WhenAll(handles).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "A trade-notification sink broke the never-faults contract; the trading flow is untouched");
        }
    }

    /// <summary>
    /// The one translation from the notification center's raw event to the
    /// sink vocabulary: exactly the three journaled outcomes map (the
    /// filter guarantees the kind, the mapping still refuses any other
    /// value loudly — never a substituted default), and the row's own facts
    /// ride unchanged.
    /// </summary>
    private static TradeNotificationEvent MapEvent(Notification notification, TradeNotificationLane lane) => new(
        Kind: notification.Kind switch
        {
            NotificationKind.OrderFill => TradeNotificationKind.Fill,
            NotificationKind.OrderCancel => TradeNotificationKind.Cancel,
            NotificationKind.OrderReject => TradeNotificationKind.Reject,
            _ => throw new ArgumentOutOfRangeException(
                nameof(notification),
                notification.Kind,
                "The trade-event filter passed a kind the sink vocabulary has no member for."),
        },
        Symbol: notification.SymbolName,
        Side: notification.Side,
        Quantity: notification.Quantity,
        Price: notification.Price,
        Exchange: notification.Exchange,
        Reason: notification.Reason,
        Lane: lane);

    /// <summary>The lane's sink-domain parallel (the App's own enum maps explicitly; an unmapped value is a design fault, never a substituted lane).</summary>
    private static TradeNotificationLane MapLane(TradingLane lane) => lane switch
    {
        TradingLane.Paper => TradeNotificationLane.Paper,
        TradingLane.Testnet => TradeNotificationLane.Testnet,
        TradingLane.Live => TradeNotificationLane.Live,
        _ => throw new ArgumentOutOfRangeException(nameof(lane), lane, "The trading lane has no sink-domain parallel."),
    };

    #endregion Dispatch
}
