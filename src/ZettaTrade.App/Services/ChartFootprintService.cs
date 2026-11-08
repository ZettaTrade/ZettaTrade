namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Livery.Contracts;
using Microsoft.Extensions.Logging;
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Persistence;
using ZettaTrade.Infrastructure.PnL;

/// <summary>
/// The chart footprint feed (T-122): MY OWN trading activity composed as
/// drawing payloads — the position entries, the working-price-bearing
/// orders and the executed fills of THE trading lane the panels already
/// read — published keyed by symbol for the chart overlays to pick from.
/// Every input is an existing stream or read (nothing new is subscribed,
/// nothing new is called, nothing leaves the process):
/// <list type="bullet">
/// <item>the PnL engine's state feed (the positions stream the T-082 feed
/// exposes) — the entry lines and the PnL tag values ride it push-wise;</item>
/// <item>the trading lane's open orders (the same read the positions
/// panel performs on demand) — the dashed working lines;</item>
/// <item>the shipped SQLite order journal — the executed fills ride the
/// ledger rows, exactly what the trade statistics and the PnL engine
/// account (the ledger of record);</item>
/// <item>the session journal's placement stream (one enqueue per
/// accepted placement — the dialog, the amend service's replacement and
/// the position actions all record here AFTER the adapter accepted) and
/// the notification center's journaled outcomes (fills, accepted
/// cancels, rejections) — the re-pull TRIGGERS.</item>
/// </list>
/// <para>
/// Refresh economy: a price-only PnL update recomposes from the cached
/// order and fill reads (no adapter surface is touched — the feed stays
/// light while a position is live-priced); an adapter/ledger pull runs
/// when the accounts' shaped state changed (a ledger fill ingested) or a
/// placement/terminal outcome arrived. Known honest gaps (NONE invented
/// around): the paper venue's late unjournaled fills and OCO sibling
/// teardowns surface on the NEXT trade-state trigger (the documented
/// journal-of-record fact; the adapter events seam is a future task's).
/// </para>
/// <para>
/// Read-only bookkeeping end to end like the PnL feed itself: this
/// service never places, amends or cancels anything.
/// </para>
/// </summary>
public sealed class ChartFootprintService(
    ITradingAdapter trading,
    IOrderHistoryStore journal,
    IOrderJournal sessionPlacements,
    IObservable<PnlSnapshot> pnlPositions,
    NotificationCenter notifications,
    ILogger<ChartFootprintService> logger) : IAsyncInitializable
{
    #region Constants

    /// <summary>The journal page the fill scan reads (the store's own recent-history size class).</summary>
    private const int FillScanLimit = 1000;

    #endregion Constants

    #region Private Fields

    /// <summary>How much of a mark batch one loop pass folds: Refresh wins over Recompose.</summary>
    private enum RefreshMark
    {
        /// <summary>Re-compose from the caches (a price-only positions update).</summary>
        Recompose = 0,

        /// <summary>A trade-state change in an input: re-read the orders and the ledger fills.</summary>
        Refresh = 1,
    }

    /// <summary>
    /// The lane's position overlays cached from the last handled PnL
    /// snapshot: only the engine's emit path writes it (serialized by the
    /// engine's own gate), the loop thread reads it — a one-batch-stale
    /// read is healed by the next recompose, never persisted.
    /// </summary>
    private IReadOnlyList<PnlPositionSnapshot> _latestPositions = PnlSnapshot.Empty.Positions;

    /// <summary>The accounts' shape stamp the change detection compares against (the same single-writer discipline as <see cref="_latestPositions"/>).</summary>
    private string _positionShapeStamp = string.Empty;

    /// <summary>The cached open-order read (loop-thread-owned).</summary>
    private IReadOnlyList<Order> _latestOrders = [];

    /// <summary>The cached journal page (loop-thread-owned).</summary>
    private IReadOnlyList<Order> _latestJournalRows = [];

    /// <summary>The coalescing marks channel: triggers enqueue, the drain loop folds and acts.</summary>
    private readonly Channel<RefreshMark> _marks =
        Channel.CreateUnbounded<RefreshMark>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });

    /// <summary>The publish surface (replays the current snapshot to late subscribers).</summary>
    private readonly BehaviorSubject<ChartFootprintSnapshot> _updates =
        new(ChartFootprintSnapshot.Empty);

    /// <summary>Tears down the input subscriptions at disposal.</summary>
    private readonly CancellationTokenSource _lifetimeCts = new();

    /// <summary>The drain loop task, tracked and awaited at disposal (never abandoned).</summary>
    private Task? _drainLoop;

    /// <summary>The combined input subscription (set at initialization).</summary>
    private IDisposable? _inputSubscription;

    /// <summary>The journal-outcome inlet: set out of the combined branch (distinct dispose).</summary>
    private IDisposable? _placementSubscription;

    #endregion Private Fields

    #region Properties

    /// <summary>
    /// The footprint updates: every state change publishes the complete
    /// immutable snapshot; subscribing replays the current state — the
    /// chart resolves THIS.
    /// </summary>
    public IObservable<ChartFootprintSnapshot> Updates => _updates;

    #endregion Properties

    #region Public Methods

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        // The trading lane's identities stay fixed; the drains only ever
        // read the adapter, the ledger and the placement stream.
        var laneJournals = Observable.Merge(
            notifications.Events
                .Where(static n => n.Kind is NotificationKind.OrderFill
                    or NotificationKind.OrderCancel
                    or NotificationKind.OrderReject)
                .Select(static _ => RefreshMark.Refresh),
            sessionPlacements.Placements.Select(static _ => RefreshMark.Refresh));

        _placementSubscription = laneJournals.Subscribe(
            mark => Enqueue(mark),
            error => logger.LogError(error, "The footprint trigger stream failed"));
        _inputSubscription = pnlPositions.Subscribe(
            HandlePnlSnapshot,
            error => logger.LogError(error, "The footprint positions stream failed"));

        _marks.Writer.TryWrite(RefreshMark.Refresh); // the standing inventory is read once at boot.
        _drainLoop = RunDrainLoopAsync(_lifetimeCts.Token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stop the drain loop and the input subscriptions; nothing else owns
    /// state worth tearing down (the lane's services outlive this feed).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _lifetimeCts.Cancel();

        if (_drainLoop is { } loop)
        {
            try
            {
                await loop;
            }
            catch (OperationCanceledException)
            {
                // The loop is expected to die through cancellation.
            }
        }

        _placementSubscription?.Dispose();
        _inputSubscription?.Dispose();
        _marks.Writer.TryComplete();
        _lifetimeCts.Dispose();
    }

    #endregion Public Methods

    #region Inputs

    /// <summary>
    /// Handle one PnL state emission (single-writer: the engine's emit
    /// path is serialized): cache the accounts and enqueue a Recompose
    /// when only the bell pushed price, a Refresh when the shaped state
    /// (quantities or entries) moved — a ledger ingest that changed the
    /// books deserves a fresh order/fill read with the very next pass.
    /// </summary>
    private void HandlePnlSnapshot(PnlSnapshot snapshot)
    {
        Volatile.Write(ref _latestPositions, snapshot.Positions);
        var stamp = BuildShapeStamp(snapshot.Positions);
        var shapedChanged = stamp != _positionShapeStamp;
        _positionShapeStamp = stamp;

        Enqueue(shapedChanged ? RefreshMark.Refresh : RefreshMark.Recompose);
    }

    /// <summary>Enqueue one mark; a completed channel at disposal swallows the late marks silently.</summary>
    private void Enqueue(RefreshMark mark) => _marks.Writer.TryWrite(mark);

    /// <summary>
    /// The accounts' shape fingerprint: per account, the exchange, the
    /// symbol, the signed quantity and the entry — everything a fill can
    /// move; the price-derived fields (last price, unrealized PnL) stay
    /// OUT so their ticking costs no adapter pull.
    /// </summary>
    private static string BuildShapeStamp(IReadOnlyList<PnlPositionSnapshot> positions) => string.Join(
        ";",
        positions.Select(static p => FormattableString.Invariant(
            $"{p.ExchangeId}|{p.Symbol}|{p.SignedQuantity}|{p.AverageEntryPrice}")));

    #endregion Inputs

    #region Drain Loop

    /// <summary>
    /// The single drain: marks fold per batch (a Refresh wins), the
    /// requested reads run once and the composed snapshot publishes —
    /// one emission per batch, ordered, no wall-clock involved. A failed
    /// read keeps the previous caches and the loop lives (the next
    /// trigger re-syncs; a dead lane never kills the chart).
    /// </summary>
    private async Task RunDrainLoopAsync(CancellationToken ct)
    {
        while (await _marks.Reader.WaitToReadAsync(ct))
        {
            var wantsRefresh = false;
            while (_marks.Reader.TryRead(out var mark))
            {
                wantsRefresh |= mark == RefreshMark.Refresh;
            }

            try
            {
                if (wantsRefresh)
                {
                    _latestOrders = await trading.GetOpenOrdersAsync(ct);
                    _latestJournalRows = await journal.GetRecentAsync(FillScanLimit, ct);
                }

                _updates.OnNext(Compose(
                    trading.Id,
                    Volatile.Read(ref _latestPositions),
                    _latestOrders,
                    _latestJournalRows));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The footprint refresh failed to read the lane's state");
            }
        }
    }

    #endregion Drain Loop

    #region Composition

    /// <summary>
    /// The pure composition the loop publishes (internal static so the
    /// goldens pin it without a service instance): the lane's non-flat
    /// accounts map to entry overlays (the quantity's sign IS the
    /// direction), the lane's LIMIT orders — the price-bearing working
    /// legs — map to dashed lines (a stop or a trailing leg carries no
    /// standing limit level and owns no line), and the journal's Filled
    /// rows map to fill markers
    /// (ascending by their own times — the resolution contract). Rows the
    /// lane never owned (another exchange's journal entries) and
    /// non-limit orders stay out — an honest absence anyway.
    /// </summary>
    /// <param name="laneId">The trading lane's exchange id the overlays qualify to.</param>
    /// <param name="positions">The engine's accounts (all lanes; qual'd by id here).</param>
    /// <param name="openOrders">The lane's open orders as the adapter reports them.</param>
    /// <param name="journalRows">The persistent journal page (all lanes; qual'd by id here).</param>
    /// <returns>The complete keyed snapshot.</returns>
    internal static ChartFootprintSnapshot Compose(
        string laneId,
        IReadOnlyList<PnlPositionSnapshot> positions,
        IReadOnlyList<Order> openOrders,
        IReadOnlyList<Order> journalRows)
    {
        return new ChartFootprintSnapshot(
            positions
                .Where(p => p.ExchangeId == laneId && !p.IsFlat)
                .Select(static p => new FootprintPosition(
                    p.Symbol,
                    new PositionOverlay
                    {
                        Direction = p.SignedQuantity > 0m
                            ? PositionDirection.Long
                            : PositionDirection.Short,
                        EntryPrice = p.AverageEntryPrice,
                        LatestPrice = p.LastPrice,
                        UnrealizedPnl = p.UnrealizedPnl,
                    }))
                .ToArray(),
            openOrders
                .Where(static o => o.Type == OrderType.Limit) // price-bearing only: stops and trailing legs carry no standing limit level (an honest absence).
                .Select(static o => new FootprintOrder(
                    o.Symbol.Normalized,
                    new WorkingOrderLine
                    {
                        Side = o.Side,
                        Price = o.Price.Value,
                    }))
                .ToArray(),
            journalRows
                .Where(r => r.ExchangeId == laneId &&
                            r.Status == OrderStatus.Filled &&
                            r.FilledQuantity.Value > 0m)
                .Select(static r => new FootprintFill(
                    r.Symbol.Normalized,
                    new FillMarker
                    {
                        Time = r.UpdatedAt ?? r.CreatedAt,
                        Side = r.Side,
                        Price = r.Price.Value,
                    }))
                .OrderBy(static f => f.Marker.Time)
                .ToArray());
    }

    #endregion Composition
}
