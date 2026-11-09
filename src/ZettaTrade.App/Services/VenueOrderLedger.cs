namespace ZettaTrade.App.Services;

using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Entities;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// The venue order transitions' persistent reflector (T-127): every order
/// update the Bybit testnet adapter's private stream publishes is recorded
/// into the persistent order journal as an insert-or-update upsert, so the
/// venue lifecycle (working → partial → filled/cancelled) lands in the
/// history panel without re-writing the session list (the dialog and the
/// position actions already journal their own placements; this ledger only
/// closes the gap the VENUE transitions carry, and the upsert contract
/// keeps the two writers collision-free).
/// </summary>
/// <remarks>
/// Best-effort by contract: a failed record is logged and never re-raised —
/// the journal's own corruption must not poison the live trading loop. The
/// update pump is a serialized Channel consumer (one ordered record at a
/// time), tracked and awaited on disposal — no fire-and-forget.
/// </remarks>
public sealed class VenueOrderLedger : IAsyncDisposable
{
    #region Properties

    private readonly IOrderHistoryStore _history;
    private readonly ILogger<VenueOrderLedger> _logger;
    private readonly IDisposable _updatesSubscription;

    /// <summary>The ordered update queue: the one consumer serves records FIFO.</summary>
    private readonly Channel<Order> _updates = Channel.CreateUnbounded<Order>(
        new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Ledger-owned lifetime; the pump is awaited on disposal.</summary>
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Task _recordPump;

    #endregion Properties

    #region Constructor

    /// <summary>
    /// Create the ledger over the Bybit testnet adapter's order-update
    /// stream and the persistent journal. Subscribing is construction-time
    /// — the adapter is an application-lifetime singleton, so the ledger's
    /// tap lives and dies with the application.
    /// </summary>
    /// <param name="venueOrderUpdates">
    /// The venue order transitions' stream (the Bybit testnet adapter's
    /// <c>OrderUpdates</c>; wired at composition so the ledger owns no
    /// adapter reference — the seam tests stand up with a subject).
    /// </param>
    /// <param name="history">The persistent order journal.</param>
    /// <param name="logger">Logger.</param>
    public VenueOrderLedger(
        IObservable<Order> venueOrderUpdates,
        IOrderHistoryStore history,
        ILogger<VenueOrderLedger> logger)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        venueOrderUpdates = venueOrderUpdates ?? throw new ArgumentNullException(nameof(venueOrderUpdates));

        _updatesSubscription = venueOrderUpdates.Subscribe(
            order => _updates.Writer.TryWrite(order),
            ex => _logger.LogError(ex, "The venue order-updates stream faulted; the standing lifecycle holds"));
        _recordPump = RunRecordPumpAsync(_lifetimeCts.Token);
    }

    #endregion Constructor

    #region Private Methods

    /// <summary>
    /// The ONE consumer of the update queue: each venue transition is
    /// recorded (upsert) serially and never re-raised — the record failure
    /// is the journal's fault surface, logged and left behind.
    /// </summary>
    private async Task RunRecordPumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var order in _updates.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await _history.RecordPlacedAsync(order, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "The venue order journal record failed for {OrderId} ({Status}) — the live surface is unaffected",
                        order.Id,
                        order.Status);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal: the only exit of the record pump.
        }
    }

    #endregion Private Methods

    #region IAsyncDisposable

    /// <summary>Stop the tap, drain the pump, release the lifetime.</summary>
    public async ValueTask DisposeAsync()
    {
        _updatesSubscription.Dispose();
        _lifetimeCts.Cancel();
        await _recordPump;
        _lifetimeCts.Dispose();
    }

    #endregion IAsyncDisposable
}
