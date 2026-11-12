namespace ZettaTrade.Infrastructure.PnL;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Livery.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// The headless PnL accounting core: average-price bookkeeping over the
/// shipped paper/fill ledger (the SQLite order journal, where an order with
/// a positive filled quantity carries its execution), realized PnL per
/// closing fill, unrealized PnL from the latest observed market price, the
/// position-flip path, and a state-changes observable for consumers.
/// Persisted realized history (EF canon) makes the numbers reconstruct
/// EXACTLY across restarts: the boot replay rebuilds the books from the
/// persisted rows, then ingests whatever ledger fills are still missing.
/// <para>
/// READ-ONLY bookkeeping by construction: the engine never places, amends
/// or cancels anything; it appends rows only to its own realized-history
/// table and treats the order journal as a read source.
/// </para>
/// </summary>
/// <remarks>
/// Concurrency: one <see cref="SemaphoreSlim"/> serializes every accounting
/// mutation (fills, prices, syncs); state mutates only after a successful
/// persist, so a failed write leaves both memory and disk consistent and a
/// retry re-detects the fill. The ledger carries no change notifications,
/// so <see cref="SyncWithLedgerAsync"/> is an idempotent diff the feed
/// wrapper may drive on a cadence (it is the boot backfill as well).
/// </remarks>
public sealed class PnlAccountingService(
    IOrderHistoryStore orderHistory,
    DbContextOptions<PnlDbContext> options,
    ILogger<PnlAccountingService> logger) : IAsyncInitializable
{
    #region Private Fields

    /// <summary>
    /// The ledger scan depth for the drive-in diff. Mirrors the journal
    /// store's own recent-history cap: ledger fills beyond it stay
    /// invisible to the sync — acceptable while the engine accounts fills
    /// live; a persisted row is never re-read as a new fill anyway.
    /// </summary>
    private const int LedgerScanLimit = 1000;

    /// <summary>Serializes every accounting mutation; see the class remarks.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Position books keyed by (exchange, normalized symbol).</summary>
    private readonly Dictionary<(string ExchangeId, string Symbol), PnlPositionState> _accounts = new();

    /// <summary>Ledger fills already accounted (each one lands exactly once).</summary>
    private readonly HashSet<(string ExchangeId, string OrderId)> _accountedFills = new();

    /// <summary>The latest observed market price per normalized symbol (real prices only — see <see cref="UpdateMarketPriceAsync"/>).</summary>
    private readonly Dictionary<string, decimal> _marketPrices = new();

    /// <summary>The behavioral state feed; late subscribers bootstrap from the current snapshot.</summary>
    private readonly BehaviorSubject<PnlSnapshot> _state = new(PnlSnapshot.Empty);

    #endregion Private Fields

    #region Properties

    /// <summary>
    /// The state feed: every accounting change (an ingested fill, a new
    /// market price) ships the complete immutable snapshot. The current
    /// snapshot is replayed to every late subscriber.
    /// </summary>
    public IObservable<PnlSnapshot> StateChanges => _state;

    #endregion Properties

    #region Public Methods

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var bootContext = new PnlDbContext(options);
        var file = bootContext.Database.GetConnectionString()
            ?? throw new InvalidOperationException("The PnL database has no connection string.");

        // Self-contained startup (the order journal's canon): the store must
        // not assume someone else created the data directory.
        var directory = Path.GetDirectoryName(
            file.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase)
                ? file["Data Source=".Length..]
                : file);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await bootContext.Database.MigrateAsync(ct);

        await _gate.WaitAsync(ct);
        try
        {
            var replayed = await ReplayRealizedRowsLockedAsync(ct);
            var ingested = await SyncWithLedgerLockedAsync(ct);
            EmitStateLocked();

            logger.LogInformation(
                "PnL accounting ready: {PersistedRows} realized rows replayed, {LedgerFills} ledger fills ingested, {Accounts} accounts",
                replayed,
                ingested,
                _accounts.Count);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Ingest one ledger fill into the accounting. Exactly-once by
    /// (ExchangeId, OrderId): an already accounted fill is ignored (null
    /// outcome), as is a ledger row without an execution. The fill is
    /// persisted into the realized history BEFORE the books move, so a
    /// restart always reconstructs the identical state.
    /// </summary>
    /// <param name="fill">The fill as the ledger carries it: an order whose
    /// filled quantity is the executed size and whose price the execution
    /// price.</param>
    /// <param name="commission">Optional per-fill commission in the
    /// symbol's quote currency. The shipped ledger carries no fee data, so
    /// the fee enters ONLY through this explicit parameter: callers with a
    /// real fee schedule (T-080) pass the amount, and the journal-driven
    /// path passes the commission the ledger row carries (stamped on the
    /// fill at execution time; a row without fee data passes null). Null
    /// means absence and is arithmetically identical
    /// to zero (the persisted row keeps a zero commission so no fee is
    /// fabricated). A commission is a realized cost: it is subtracted from
    /// the fill's own realized contribution, making a pure opening fill
    /// contribute exactly −commission.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>What the fill did to the book and what it realized net of
    /// its commission — or null when nothing was accounted (non-fill row or
    /// already accounted fill).</returns>
    public async Task<PnlFillOutcome?> IngestFillAsync(
        Order fill,
        decimal? commission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fill);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await IngestFillLockedAsync(fill, commission, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Ingest every ledger fill the books do not know yet (an idempotent
    /// diff over the order journal). This is the boot backfill and the one
    /// push-free way realized state can follow live fills: the shipped
    /// ledger has no change notifications. Rows without an execution, rows
    /// already accounted, and rows the ledger never finished (cancelled or
    /// still pending) are skipped; exactly-once accounting is keyed by
    /// (ExchangeId, OrderId) against the persisted realized history.
    /// </summary>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    public async Task SyncWithLedgerAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var ingested = await SyncWithLedgerLockedAsync(cancellationToken);
            if (ingested > 0)
            {
                EmitStateLocked();
                logger.LogInformation("PnL ledger sync: {LedgerFills} new fills ingested", ingested);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The realized net sums per quote currency for one exchange over a UTC
    /// window ([fromUtc, toUtc)): the daily loss governor's facts (T-189).
    /// Each row's stored amount is already net of its commission (the
    /// ingest math nets them at row-build time), so a bucket's sum is the
    /// plain window sum — never a second commission subtraction. Buckets
    /// never mix quote currencies: a quoteless ledger symbol owns the
    /// empty-string bucket (the consumers never attribute a quoteless
    /// order against it — see the governor's own remarks).
    /// </summary>
    /// <param name="exchangeId">The exchange the rows belong to (part of the realized history's key).</param>
    /// <param name="fromUtc">Inclusive window start (UTC).</param>
    /// <param name="toUtc">Exclusive window end (UTC).</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    public async Task<IReadOnlyDictionary<string, decimal>> GetRealizedBucketsAsync(
        string exchangeId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exchangeId);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var context = new PnlDbContext(options);
            var rows = await context.Realized
                .Where(entry => entry.ExchangeId == exchangeId
                    && entry.RealizedAt >= fromUtc
                    && entry.RealizedAt < toUtc)
                .Select(entry => new { entry.Symbol, entry.RealizedAmount })
                .ToListAsync(cancellationToken);

            var buckets = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var quote = Symbol.Parse(row.Symbol).QuoteAsset ?? string.Empty;
                buckets[quote] = buckets.GetValueOrDefault(quote) + row.RealizedAmount;
            }

            return buckets;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Feed the latest market price for a symbol into the unrealized side.
    /// Who observes the feed decides what is real: a non-positive price is
    /// garbage data and changes nothing (no fabrication, no exception) —
    /// the unrealized side stays at its last honest value.
    /// </summary>
    /// <param name="symbol">The symbol the price belongs to (the account
    /// matching is by symbol across exchanges: the facade serves the active
    /// exchange's feed, and paper positions ride the same prices).</param>
    /// <param name="price">The latest traded price.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    public async Task UpdateMarketPriceAsync(
        Symbol symbol,
        decimal price,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(symbol);

        if (price <= 0m)
        {
            logger.LogWarning(
                "PnL feed: rejected a non-positive price {Price} for {Symbol}; the unrealized side keeps its last value",
                price,
                symbol.Normalized);
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_marketPrices.TryGetValue(symbol.Normalized, out var current) && current == price)
            {
                return;
            }

            _marketPrices[symbol.Normalized] = price;
            EmitStateLocked();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The engine owns no running work: subscribers wind down with their
    /// own subscriptions.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        _state.Dispose();
        return ValueTask.CompletedTask;
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>
    /// The ingest body; its callers already hold the gate. The math runs
    /// first, the row persists second, and the books move third — a persist
    /// failure leaves both memory and disk exactly where they were.
    /// </summary>
    private async Task<PnlFillOutcome?> IngestFillLockedAsync(
        Order fill,
        decimal? commission,
        CancellationToken ct)
    {
        if (fill.FilledQuantity.Value <= 0m)
        {
            return null;
        }

        var fillKey = (fill.ExchangeId, fill.Id);
        if (_accountedFills.Contains(fillKey))
        {
            return null;
        }

        var accountKey = (fill.ExchangeId, fill.Symbol.Normalized);
        var state = _accounts.GetValueOrDefault(accountKey);
        var (newPosition, effect, closedQuantity, gross) = PnlPositionMath.ApplyFill(
            state,
            fill.Side,
            fill.FilledQuantity.Value,
            fill.Price.Value);

        // Null commission means absence (the journal-driven path); it is
        // arithmetically zero and stores as zero — no fee is fabricated.
        var applied = commission ?? 0m;
        var realized = gross - applied;
        var newState = newPosition with { CumulativeRealized = state.CumulativeRealized + realized };

        var row = new PnlRealizedEntry
        {
            ExchangeId = fill.ExchangeId,
            OrderId = fill.Id,
            Symbol = fill.Symbol.Normalized,
            Side = fill.Side,
            Quantity = fill.FilledQuantity.Value,
            Price = fill.Price.Value,
            Commission = applied,
            RealizedAmount = realized,
            Effect = effect,
            RealizedAt = fill.CreatedAt.ToUniversalTime(),
        };

        await using (var context = new PnlDbContext(options))
        {
            context.Add(row);
            await context.SaveChangesAsync(ct);
        }

        _accounts[accountKey] = newState;
        _accountedFills.Add(fillKey);
        EmitStateLocked();

        logger.LogInformation(
            "PnL accounting: {Symbol} {Side} {Quantity} at {Price} {Effect} realized {Realized} (commission {Commission})",
            fill.Symbol.Normalized,
            fill.Side,
            fill.FilledQuantity.Value,
            fill.Price.Value,
            effect,
            realized,
            applied);

        return new PnlFillOutcome(effect, closedQuantity, realized);
    }

    /// <summary>
    /// The ledger diff body; its callers already hold the gate. Walked
    /// oldest-first so a recovered batch keeps chronological order.
    /// </summary>
    private async Task<int> SyncWithLedgerLockedAsync(CancellationToken ct)
    {
        var rows = await orderHistory.GetRecentAsync(LedgerScanLimit, ct);
        var ingested = 0;

        foreach (var order in rows.Reverse())
        {
            if (!IsLedgerFill(order))
            {
                continue;
            }

            if (_accountedFills.Contains((order.ExchangeId, order.Id)))
            {
                continue;
            }

            try
            {
                // The journal-driven adapter path: the commission the row
                // carries rides the same per-fill parameter — a row stamped
                // at execution (T-080) prices its fee, a row without fee
                // data carries null (absence, identical to zero).
                await IngestFillLockedAsync(order, commission: order.Commission, ct);
                ingested++;
            }
            catch (InvalidOperationException ex)
            {
                // One malformed ledger row never wedges the diff; the next
                // tick retries the batch and skips the same bad row the same
                // honest way.
                logger.LogWarning(
                    ex,
                    "PnL ledger sync: skipping order {OrderId} on {ExchangeId} — invalid fill",
                    order.Id,
                    order.ExchangeId);
            }
        }

        return ingested;
    }

    /// <summary>
    /// Whether a ledger order is a fill worth accounting: an execution
    /// exists (positive filled quantity) and the order has reached a filled
    /// state (the paper engine fills whole or not at all; partial rows are
    /// honored the same way).
    /// </summary>
    private static bool IsLedgerFill(Order order) =>
        order.FilledQuantity.Value > 0m
        && order.Status is OrderStatus.Filled or OrderStatus.PartiallyFilled;

    /// <summary>
    /// Rebuild the books from the persisted realized history, oldest first
    /// (deterministic even across equal timestamps). The rows carry the
    /// commission each fill was ingested with — exactly what a replay needs;
    /// the recomputed effect is not read back (the row's stored value stays
    /// the journal of record). Returns the replayed row count.
    /// </summary>
    private async Task<int> ReplayRealizedRowsLockedAsync(CancellationToken ct)
    {
        await using var context = new PnlDbContext(options);
        // Without comparers (EF cannot translate them): SQLite sorts TEXT
        // bytes — a deterministic (exchange, realized time, order id) order.
        var rows = await context.Realized
            .OrderBy(entry => entry.RealizedAt)
            .ThenBy(entry => entry.OrderId)
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            _accountedFills.Add((row.ExchangeId, row.OrderId));

            var accountKey = (row.ExchangeId, row.Symbol);
            var state = _accounts.GetValueOrDefault(accountKey);
            var (newPosition, _, _, gross) = PnlPositionMath.ApplyFill(
                state,
                row.Side,
                row.Quantity,
                row.Price);

            _accounts[accountKey] = newPosition with { CumulativeRealized = state.CumulativeRealized + gross - row.Commission };
        }

        return rows.Count;
    }

    /// <summary>
    /// Build and publish the complete immutable snapshot: accounts ordered
    /// by (exchange, symbol) so the feed is deterministic across
    /// reconstructions. Callers already hold the gate.
    /// </summary>
    private void EmitStateLocked() => _state.OnNext(new PnlSnapshot
    {
        Positions = _accounts
            .OrderBy(entry => entry.Key.ExchangeId, StringComparer.Ordinal)
            .ThenBy(entry => entry.Key.Symbol, StringComparer.Ordinal)
            .Select(CreatePositionSnapshot)
            .ToArray(),
    });

    /// <summary>Build the snapshot row for one account.</summary>
    private PnlPositionSnapshot CreatePositionSnapshot(KeyValuePair<(string ExchangeId, string Symbol), PnlPositionState> account)
    {
        var (key, state) = (account.Key, account.Value);
        var quote = Symbol.Parse(key.Symbol).QuoteAsset ?? string.Empty;
        var lastPrice = _marketPrices.TryGetValue(key.Symbol, out var price) ? price : (decimal?)null;

        var unrealized = lastPrice is { } observed && state.SignedQuantity != 0m
            ? (observed - state.AverageEntryPrice) * state.SignedQuantity
            : 0m;

        return new PnlPositionSnapshot
        {
            ExchangeId = key.ExchangeId,
            Symbol = key.Symbol,
            QuoteCurrency = quote,
            SignedQuantity = state.SignedQuantity,
            AverageEntryPrice = state.AverageEntryPrice,
            RealizedPnl = new Money(state.CumulativeRealized, quote),
            LastPrice = lastPrice,
            UnrealizedPnl = new Money(unrealized, quote),
        };
    }

    #endregion Private Methods
}
