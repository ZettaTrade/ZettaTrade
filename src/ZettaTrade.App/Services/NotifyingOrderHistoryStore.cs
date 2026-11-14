namespace ZettaTrade.App.Services;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Livery.Contracts;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// The notification tap over the order journal (T-097): every journaled
/// terminal outcome rides the notification center, exactly once per
/// journal write. The wrapper mirrors the discipline the T-133 MCP lane
/// set: it records nothing itself and never alters a trade decision — the
/// placements and cancellations happen in the flows, the journal stays
/// the journal of record, and this tap only ANNOUNCES what the record
/// holds. Every accepted placement and every adapter-confirmed
/// cancellation in the app journals through this store, so fills (the
/// Filled rows), accepted cancels and any journal rejection row land
/// here. A refusal the venue raised BEFORE acceptance journals nothing —
/// those rejections reach the center through the flows' own publish
/// (nothing is fabricated from an outcome the journal never saw).
/// <para>
/// A cancellation toast carries the symbol recovered from the journal of
/// record (the same lookup the store's own query serves); a cancel whose
/// row was never recorded (the store's logged no-op) still announces the
/// adapter-confirmed cancel — the flows confirm the adapter accepted it
/// BEFORE journaling — with the symbol left off (nothing is invented).
/// The journal-vs-adapter sim reach: the paper venue's pending-limit
/// fills happen inside the adapter and the journal is not re-written for
/// them, so they surface exactly when their placement journaled them —
/// the adapter events stream (a future task's seam) is where those
/// belong, this tap stays journal-true.
/// </para>
/// </summary>
public sealed class NotifyingOrderHistoryStore(
    IOrderHistoryStore inner,
    NotificationCenter notifications,
    ILogger<NotifyingOrderHistoryStore> logger) : IOrderHistoryStore, IAsyncInitializable
{
    #region Private Fields

    /// <summary>The journal window a cancel's symbol lookup scans (the store's own recent page).</summary>
    private const int SymbolLookupPage = 1000;

    private readonly IOrderHistoryStore _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly NotificationCenter _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
    private readonly ILogger<NotifyingOrderHistoryStore> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// The inner store's initializer when it carries one (the composition
    /// path constructs the SQLite store — migrations ride its walk). An
    /// inner without one (a test double) contributes nothing here.
    /// </summary>
    private readonly IAsyncInitializable? _innerInitialization =
        inner is IAsyncInitializable initializable ? initializable : null;

    #endregion Private Fields

    #region Journal Writes

    /// <inheritdoc/>
    public async Task RecordPlacedAsync(Order order, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        await _inner.RecordPlacedAsync(order, cancellationToken);

        // The notification rides the journal's success: a journal fault is
        // the caller's rethrown error (the flows already hold that
        // contract), and a toast for a NOT journaled outcome would
        // fabricate provenance. Nothing is announced on a fault.
        _notifications.PublishTradeOutcome(order);
        _logger.LogInformation(
            "Order journal tap: announced {OrderId} ({Status}) on {Symbol}",
            order.Id,
            order.Status,
            order.Symbol.Normalized);
    }

    /// <inheritdoc/>
    public async Task MarkCancelledAsync(
        string exchangeId,
        string orderId,
        DateTimeOffset cancelledAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exchangeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);
        await _inner.MarkCancelledAsync(exchangeId, orderId, cancelledAt, cancellationToken);

        // The symbol recovers from the journal of record; a row the
        // journal never had (the store's logged no-op) leaves the symbol
        // off rather than inventing one. The lookup is a read-only page
        // scan and never suppresses the announcement of the
        // adapter-confirmed cancel itself.
        var symbolName = await TryResolveCancelledSymbolAsync(orderId, cancellationToken);
        _notifications.Publish(Notification.ForJournalOutcome(
            NotificationKind.OrderCancel,
            cancelledAt,
            symbolName,
            OrderSide.Buy,
            0m,
            exchange: exchangeId));
        _logger.LogInformation(
            "Order journal tap: announced the cancel of {ExchangeId}/{OrderId}",
            exchangeId,
            orderId);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Order>> GetRecentAsync(int limit, CancellationToken cancellationToken = default) =>
        _inner.GetRecentAsync(limit, cancellationToken);

    /// <inheritdoc/>
    public Task<int> DeleteExchangeAsync(string exchangeId, CancellationToken cancellationToken = default) =>
        _inner.DeleteExchangeAsync(exchangeId, cancellationToken);

    /// <inheritdoc/>
    public Task<bool> SetTradeMetaAsync(
        string exchangeId,
        string orderId,
        string? note,
        string? tags,
        CancellationToken cancellationToken = default) =>
        _inner.SetTradeMetaAsync(exchangeId, orderId, note, tags, cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<OrderTradeMeta>> GetTradeMetaAsync(
        IReadOnlyList<(string ExchangeId, string OrderId)> keys,
        CancellationToken cancellationToken = default) =>
        _inner.GetTradeMetaAsync(keys, cancellationToken);

    #endregion Journal Writes

    #region Initialization

    /// <summary>
    /// Delegate the inner store's own initialization (migrations included):
    /// the tap adds no initialization of its own — the notification lanes
    /// run per subscription.
    /// </summary>
    /// <inheritdoc cref="IAsyncInitializable.InitializeAsync"/>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_innerInitialization is { } initializable)
        {
            await initializable.InitializeAsync(ct);
        }
    }

    #endregion Initialization

    #region Symbol Lookup

    /// <summary>
    /// Recover the cancelled order's symbol from the journal of record:
    /// the best-effort scan the reconciliation needs — empty when the
    /// row is missing or the read faults (a logging fault must never turn
    /// a confirmed cancel into a failed flow).
    /// </summary>
    private async Task<string> TryResolveCancelledSymbolAsync(string orderId, CancellationToken cancellationToken)
    {
        try
        {
            var orders = await _inner.GetRecentAsync(SymbolLookupPage, cancellationToken);
            var match = orders.FirstOrDefault(order => string.Equals(
                order.Id,
                orderId,
                StringComparison.Ordinal));
            return match?.Symbol.Normalized ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Order journal tap: the cancel of {OrderId} could not recover its symbol from the journal",
                orderId);
            return string.Empty;
        }
    }

    #endregion Symbol Lookup
}
