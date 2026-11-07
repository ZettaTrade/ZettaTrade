namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// The MCP trading lane's journaling wrapper (T-133): the trading adapter the
/// tools execute against, wrapped so every tool-placed order and cancellation
/// rides the exact journal ingestion the panels use — the session
/// <see cref="IOrderJournal"/> list and the persistent
/// <see cref="IOrderHistoryStore"/> ledger. The composition root hands THIS
/// wrapper to the trading tools' registration instead of the raw adapter, so
/// an MCP-placed paper fill becomes visible to every journal-backed surface
/// (order history, positions panel, CSV export, the trade statistics and the
/// PnL realized history) exactly like a fill placed from the UI. The wrapper
/// records only: it never alters a placement or cancellation decision — the
/// risk check still runs once, in the tool handler, before the order reaches
/// the wrapped adapter.
/// <para>
/// The persistence writes are best effort on both arms — the adapter has
/// already accepted or cancelled the order, so a journal fault must never
/// turn a successful trade action into a tool error (the same contract the
/// dialog and the position actions hold).
/// </para>
/// </summary>
public sealed class McpJournalingTradingAdapter(
    ITradingAdapter inner,
    IOrderJournal sessionJournal,
    IOrderHistoryStore orderHistory,
    ILogger<McpJournalingTradingAdapter> logger) : ITradingAdapter
{
    #region Private Fields

    private readonly ITradingAdapter _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly IOrderJournal _sessionJournal = sessionJournal ?? throw new ArgumentNullException(nameof(sessionJournal));
    private readonly IOrderHistoryStore _orderHistory = orderHistory ?? throw new ArgumentNullException(nameof(orderHistory));
    private readonly ILogger<McpJournalingTradingAdapter> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    #endregion Private Fields

    #region Trading Operations

    /// <inheritdoc/>
    public async Task<Order> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default)
    {
        var order = await _inner.PlaceOrderAsync(request, ct);

        // The two journal writes every accepted placement performs (the
        // dialog and the position actions pattern): the session list first
        // (in-memory, cannot fail meaningfully), then the persistent ledger
        // best effort — the persistence fault is logged and never re-raised
        // into the tool result.
        _sessionJournal.RecordPlaced(order);
        try
        {
            await _orderHistory.RecordPlacedAsync(order, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "MCP trading: failed to persist {OrderId} to the order journal",
                order.Id);
        }

        _logger.LogInformation(
            "MCP trading: placed {OrderId} ({Status}) on {Symbol}",
            order.Id,
            order.Status,
            order.Symbol.Normalized);
        return order;
    }

    /// <inheritdoc/>
    public async Task CancelOrderAsync(Symbol symbol, string orderId, CancellationToken ct = default)
    {
        await _inner.CancelOrderAsync(symbol, orderId, ct);

        // The same best-effort persistence the panel cancellations ride
        // after an accepted cancel: the wrapper's exchange identity is the
        // wrapped adapter's own, and a cancel for an order the journal
        // never recorded stays the store's logged no-op (its contract).
        try
        {
            await _orderHistory.MarkCancelledAsync(_inner.Id, orderId, DateTimeOffset.UtcNow, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "MCP trading: failed to mark {OrderId} cancelled in the order journal",
                orderId);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Order>> GetOpenOrdersAsync(CancellationToken ct = default) =>
        _inner.GetOpenOrdersAsync(ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Position>> GetPositionsAsync(CancellationToken ct = default) =>
        _inner.GetPositionsAsync(ct);

    /// <inheritdoc/>
    public Task<IReadOnlyDictionary<string, Money>> GetBalancesAsync(CancellationToken ct = default) =>
        _inner.GetBalancesAsync(ct);

    #endregion Trading Operations

    #region Market Data (pass-through)

    /// <inheritdoc/>
    public string Id => _inner.Id;

    /// <inheritdoc/>
    public string Name => _inner.Name;

    /// <inheritdoc/>
    public bool IsConnected => _inner.IsConnected;

    /// <inheritdoc/>
    public ExchangeCapabilities Capabilities => _inner.Capabilities;

    /// <inheritdoc/>
    public Task ConnectAsync(CancellationToken ct = default) => _inner.ConnectAsync(ct);

    /// <inheritdoc/>
    public Task DisconnectAsync(CancellationToken ct = default) => _inner.DisconnectAsync(ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Symbol>> GetSymbolsAsync(CancellationToken ct = default) =>
        _inner.GetSymbolsAsync(ct);

    /// <inheritdoc/>
    public IObservable<Ticker> SubscribeTicker(Symbol symbol) => _inner.SubscribeTicker(symbol);

    /// <inheritdoc/>
    public IObservable<OrderBook> SubscribeOrderBook(Symbol symbol, int depth = 20) =>
        _inner.SubscribeOrderBook(symbol, depth);

    /// <inheritdoc/>
    public IObservable<MarketTrade> SubscribeTrades(Symbol symbol) => _inner.SubscribeTrades(symbol);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Bar>> GetBarsAsync(
        Symbol symbol,
        TimeFrame timeFrame,
        DateTimeOffset from,
        DateTimeOffset? to = null,
        int limit = 500,
        CancellationToken ct = default) =>
        _inner.GetBarsAsync(symbol, timeFrame, from, to, limit, ct);

    #endregion Market Data (pass-through)

    #region IAsyncDisposable

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    #endregion IAsyncDisposable
}
