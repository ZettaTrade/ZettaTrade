namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// The liquidation feed's one front (T-242): the panels resolve the venue's
/// forced-liquidation flow through this service, never through the adapter
/// directly. The service resolves the ACTIVE adapter the same facade the
/// tape's trades ride, probes its liquidation capability (an adapter
/// without the venue's forced-order wire answers the honest absence — an
/// empty stream and an unavailable flag, never a guess), and aggregates the
/// flow per symbol: the raw events stream for the tape-style row display,
/// and a pulse-paced window state (the recent events, their rolled-up
/// totals and the open large-notional cascade the panel badges).
/// <para>
/// The aggregation lane rides a bounded channel (the log tail's
/// backpressure leg, the T-052/T-105 transport lessons): the wire pushes
/// into the channel and NEVER blocks on the consumer — beyond the
/// capacity the OLDEST events drop (display cargo, never order flow), the
/// drops stay counted, and the single reader owns all the tracker
/// mutations and the publishes on one lane. The window state publishes per
/// pulse tick (a venue burst never storms the panel) — the badge lags at
/// most one tick behind the wire, and the raw events stream stays
/// wire-paced for the row tape.
/// </para>
/// </summary>
public sealed class LiquidationFeedService
{
    #region Constants

    /// <summary>
    /// The window lane's backpressure capacity (the log tail's shape):
    /// enough queue for the worst venue burst without the reader starving
    /// the wire, bounded so a stalled consumer can never grow it.
    /// </summary>
    public const int LaneChannelCapacity = 1_024;

    /// <summary>
    /// The lane's recent-events bound: the ring the window state is
    /// computed over. Liquidations are rare next to trades — this carries
    /// minutes of a storm without growing.
    /// </summary>
    internal const int LaneRingCapacity = 256;

    /// <summary>How long an event stays in the lane's ring (the window's look-back).</summary>
    public static readonly TimeSpan RecentLookback = TimeSpan.FromMinutes(2);

    /// <summary>The window state's publish cadence (the pulse the lane samples).</summary>
    private static readonly TimeSpan PulseInterval = TimeSpan.FromSeconds(1);

    #endregion Constants

    #region Private Fields

    private readonly ActiveExchangeService _activeExchanges;
    private readonly TimeProvider _clock;
    private readonly ILogger<LiquidationFeedService> _logger;
    private readonly IObservable<long> _pulse;

    #endregion Private Fields

    #region Properties

    /// <summary>
    /// The active adapter's liquidation capability, as a stream: true while
    /// the venue publishes forced-liquidation events, false while it does
    /// not (an honest absence the panel renders as its unavailable state —
    /// never a dead panel pretending to wait for data).
    /// </summary>
    public IObservable<bool> Availability { get; }

    #endregion Properties

    #region Constructors

    /// <summary>Create the front over the active-exchange facade, the clock and the pulse source (the test seam; the default is the plain Rx interval clock).</summary>
    public LiquidationFeedService(
        ActiveExchangeService activeExchanges,
        TimeProvider clock,
        ILogger<LiquidationFeedService> logger,
        IObservable<long>? pulse = null)
    {
        _activeExchanges = activeExchanges ?? throw new ArgumentNullException(nameof(activeExchanges));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _pulse = pulse ?? Observable.Interval(PulseInterval);

        // AdapterChanges emits the current adapter on subscribe, so the
        // probe answers immediately and re-answers on every exchange
        // switch; DistinctUntilChanged keeps a same-capability switch
        // silent.
        Availability = _activeExchanges
            .AdapterChanges
            .Select(adapter => adapter is ILiquidationFeedProvider)
            .DistinctUntilChanged();
    }

    #endregion Constructors

    #region Public Methods

    /// <summary>
    /// The symbol's raw forced-liquidation events, live from subscription:
    /// wire-paced (the row tape's widget owns its own buffering, the trade
    /// tape's frame discipline), capability-honest (an adapter without the
    /// wire answers an empty stream — the widget renders its empty state),
    /// and switched per adapter change (an exchange switch atomically
    /// cancels the previous exchange's stream — the facade's Attach rule).
    /// A stream failure ends the data for its symbol (the caller logs it,
    /// never a default value).
    /// </summary>
    public IObservable<LiquidationEvent> SubscribeEvents(Symbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        return _activeExchanges
            .AdapterChanges
            .Select(adapter => adapter is ILiquidationFeedProvider provider
                ? provider.SubscribeLiquidations(symbol)
                : Observable.Empty<LiquidationEvent>())
            .Switch();
    }

    /// <summary>
    /// The symbol's aggregated window state, pulse-paced: the lane's bounded
    /// channel decouples the wire from the single reader (a stalled consumer
    /// drops the oldest events, never backpressures the venue stream), the
    /// reader owns the tracker's mutations, and each pulse tick publishes
    /// the state the pure aggregator computes — the recent events, the
    /// rolled-up totals and the open cascade. An adapter switch rebuilds the
    /// lane for the new exchange's stream (the facade's Attach rule).
    /// </summary>
    public IObservable<LiquidationsWindowState> SubscribeWindow(Symbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        return Observable.Create<LiquidationsWindowState>(async (observer, ct) =>
        {
            var channel = Channel.CreateBounded<LaneItem>(
                new BoundedChannelOptions(LaneChannelCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                });
            var dropped = 0;
            var tracker = new WindowTracker(this, symbol);

            void Enqueue(LaneItem item)
            {
                if (!channel.Writer.TryWrite(item))
                {
                    // DropOldest evicted silently to fit: count the loss —
                    // the window's totals are a floor, and the log keeps the
                    // under-count honest.
                    Interlocked.Increment(ref dropped);
                }
            }

            var source = SubscribeEvents(symbol)
                .Subscribe(
                    ev => Enqueue(new LaneItem(ev)),
                    ex =>
                    {
                        _logger.LogWarning(
                            ex,
                            "The liquidation window lane for {Symbol} ended with an error",
                            symbol.Normalized);
                        channel.Writer.TryComplete();
                    },
                    () => channel.Writer.TryComplete());
            var pulseSubscription = _pulse.Subscribe(_ => Enqueue(LaneItem.Tick()));

            try
            {
                // The single reader: everything the tracker mutates and the
                // observer sees happens on this loop, serialized. The await
                // keeps the subscription body inside the reader's lifetime —
                // disposing cancels it, the loop exits, nothing is
                // abandoned.
                while (await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    while (channel.Reader.TryRead(out var item))
                    {
                        if (item.Value is { } ev)
                        {
                            tracker.Push(ev);
                        }
                        else
                        {
                            observer.OnNext(tracker.Snapshot());
                        }
                    }
                }

                observer.OnCompleted();
                return;
            }
            catch (OperationCanceledException)
            {
                // Disposal: the subscription's own exit.
            }
            finally
            {
                pulseSubscription.Dispose();
                source.Dispose();
                channel.Writer.TryComplete();

                if (Volatile.Read(ref dropped) > 0)
                {
                    _logger.LogWarning(
                        "{Dropped} of the {Symbol} liquidation events were dropped by the window lane's backpressure",
                        Volatile.Read(ref dropped),
                        symbol.Normalized);
                }
            }
        });
    }

    #endregion Public Methods

    #region Lane Item

    /// <summary>One lane item: a wire event, or the pulse's tick (no event).</summary>
    private readonly record struct LaneItem(LiquidationEvent? Value)
    {
        /// <summary>The pulse's tick marker.</summary>
        public static LaneItem Tick() => default;
    }

    #endregion Lane Item

    #region Window Tracker

    /// <summary>
    /// The lane's per-subscription aggregate state: the recent events'
    /// bounded chronological ring (the look-back and the capacity both
    /// bound it) and the snapshot the pulse publishes. Single-threaded by
    /// construction — only the lane's reader touches it.
    /// </summary>
    private sealed class WindowTracker
    {
        private readonly LiquidationFeedService _owner;
        private readonly Symbol _symbol;
        private readonly List<LiquidationEvent> _chronological = [];

        public WindowTracker(LiquidationFeedService owner, Symbol symbol)
        {
            _owner = owner;
            _symbol = symbol;
        }

        /// <summary>Append one event and trim the ring (the look-back evicts first, the capacity guards the rest).</summary>
        public void Push(LiquidationEvent ev)
        {
            _chronological.Add(ev);
            var now = _owner._clock.GetUtcNow();
            var oldest = now - RecentLookback;
            while (_chronological.Count > 0 && _chronological[0].Timestamp < oldest)
            {
                _chronological.RemoveAt(0);
            }

            while (_chronological.Count > LaneRingCapacity)
            {
                _chronological.RemoveAt(0);
            }
        }

        /// <summary>The pulse's state: the pure aggregator over a COPY of the ring (the published record must never hold the mutable list), at the clock's now.</summary>
        public LiquidationsWindowState Snapshot() =>
            LiquidationCascadeAggregator.WindowState(
                _chronological.ToArray(),
                _owner._clock.GetUtcNow(),
                LiquidationCascadeAggregator.DefaultCascadeWindow,
                LiquidationCascadeAggregator.DefaultCascadeNotionalThreshold);
    }

    #endregion Window Tracker
}
