namespace ZettaTrade.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// SQLite <see cref="IOrderHistoryStore"/>. One <see cref="OrderHistoryDbContext"/>
/// per operation (cheap: they share the singleton options object which only
/// carries the connection string), so writes never share live state. The
/// initialize step creates the data directory and applies migrations.
/// </summary>
public sealed class SqliteOrderHistoryStore(
    DbContextOptions<OrderHistoryDbContext> options,
    ILogger<SqliteOrderHistoryStore> logger) : IOrderHistoryStore, IAsyncInitializable
{
    #region Private Fields

    /// <summary>Growth cap in <see cref="GetRecentAsync"/>; the history view is read-only.</summary>
    private const int MaxRecentBatch = 1000;

    #endregion Private Fields

    #region Public Methods

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var dbContext = new OrderHistoryDbContext(options);
        var file = dbContext.Database.GetConnectionString()
            ?? throw new InvalidOperationException("The order history database has no connection string.");

        // Self-contained startup: the store must not assume someone else
        // created the data directory (e.g. freshly installed on a new machine).
        var directory = Path.GetDirectoryName(
            file.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase)
                ? file["Data Source=".Length..]
                : file);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await dbContext.Database.MigrateAsync(ct);
        logger.LogInformation(
            "Order history store ready at {DatabaseFile} (migrations applied)",
            file);
    }

    /// <inheritdoc/>
    public async Task RecordPlacedAsync(Order order, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        await using var dbContext = new OrderHistoryDbContext(options);

        // Upsert: the (ExchangeId, OrderId) pair is the identity; a re-record
        // (e.g. an adapter status refresh) updates the existing row.
        var entry = await dbContext.Orders.FindAsync(
            [order.ExchangeId, order.Id],
            cancellationToken);
        if (entry is null)
        {
            dbContext.Orders.Add(ToEntry(order));
        }
        else
        {
            OverwriteEntry(entry, order);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
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
        await using var dbContext = new OrderHistoryDbContext(options);

        var entry = await dbContext.Orders.FindAsync(
            [exchangeId, orderId],
            cancellationToken);
        if (entry is null)
        {
            // Best effort by contract: a cancellation can reach the trade
            // panel for an order the journal never recorded (e.g. placed via
            // MCP tools); it must never break the cancel flow.
            logger.LogWarning(
                "Order history: cancel for {ExchangeId}/{OrderId} has no recorded order; nothing to mark",
                exchangeId,
                orderId);
            return;
        }

        entry.Status = OrderStatus.Cancelled;
        entry.UpdatedAt = cancelledAt;
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Order history: {ExchangeId}/{OrderId} marked cancelled",
            exchangeId,
            orderId);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Order>> GetRecentAsync(int limit, CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(limit, 1, MaxRecentBatch);
        await using var dbContext = new OrderHistoryDbContext(options);

        var entries = await dbContext.Orders
            .OrderByDescending(entry => entry.CreatedAt)
            .Take(take)
            .ToListAsync(cancellationToken);
        return entries.Select(ToOrder).ToArray();
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>Map an adapter order onto a journal row.</summary>
    private static OrderHistoryEntry ToEntry(Order order) => new()
    {
        ExchangeId = order.ExchangeId,
        OrderId = order.Id,
        Symbol = order.Symbol.Normalized,
        Side = order.Side,
        Type = order.Type,
        Quantity = order.Quantity.Value,
        Price = order.Price.Value,
        Status = order.Status,
        TimeInForce = order.TimeInForce,
        CreatedAt = order.CreatedAt,
        UpdatedAt = order.UpdatedAt,
        FilledQuantity = order.FilledQuantity.Value,
        ClientOrderId = order.ClientOrderId,
        StopPrice = order.StopPrice?.Value,
        ExchangeOrderId = order.ExchangeOrderId,
        RejectReason = order.RejectReason,
        Expiry = order.Expiry,
    };

    /// <summary>Refresh an existing row from a re-recorded order.</summary>
    private static void OverwriteEntry(OrderHistoryEntry entry, Order order)
    {
        entry.Symbol = order.Symbol.Normalized;
        entry.Side = order.Side;
        entry.Type = order.Type;
        entry.Quantity = order.Quantity.Value;
        entry.Price = order.Price.Value;
        entry.Status = order.Status;
        entry.TimeInForce = order.TimeInForce;
        entry.CreatedAt = order.CreatedAt;
        entry.UpdatedAt = order.UpdatedAt;
        entry.FilledQuantity = order.FilledQuantity.Value;
        entry.ClientOrderId = order.ClientOrderId;
        entry.StopPrice = order.StopPrice?.Value;
        entry.ExchangeOrderId = order.ExchangeOrderId;
        entry.RejectReason = order.RejectReason;
        entry.Expiry = order.Expiry;
    }

    /// <summary>Map a journal row back onto the Core <see cref="Order"/> record.</summary>
    private static Order ToOrder(OrderHistoryEntry entry) => new()
    {
        Id = entry.OrderId,
        ExchangeId = entry.ExchangeId,
        Symbol = Symbol.Parse(entry.Symbol),
        Side = entry.Side,
        Type = entry.Type,
        Quantity = new Quantity(entry.Quantity),
        Price = new Price(entry.Price),
        Status = entry.Status,
        TimeInForce = entry.TimeInForce,
        CreatedAt = entry.CreatedAt,
        UpdatedAt = entry.UpdatedAt,
        FilledQuantity = new Quantity(entry.FilledQuantity),
        ClientOrderId = entry.ClientOrderId,
        StopPrice = entry.StopPrice is null ? null : new Price(entry.StopPrice.Value),
        ExchangeOrderId = entry.ExchangeOrderId,
        RejectReason = entry.RejectReason,
        Expiry = entry.Expiry,
    };

    #endregion Private Methods
}
