namespace ZettaTrade.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Livery.Contracts;
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

    /// <summary>
    /// The model converters, compiled once, for the raw upsert statement
    /// (BUG-043): the interpolated SQL runs AROUND the EF value-conversion
    /// pipeline, so its values must be pre-converted through the EXACT
    /// converters the entity model keeps on each column — the stored TEXT
    /// stays byte-consistent whether a row was written by <c>SaveChanges</c>
    /// or by the upsert SQL.
    /// </summary>
    private static readonly Func<DateTimeOffset, string> TimestampText =
        OrderHistoryConversions.Timestamp.ConvertToProviderExpression.Compile();

    /// <summary>See <see cref="TimestampText"/>.</summary>
    private static readonly Func<DateTimeOffset?, string> NullableTimestampText =
        OrderHistoryConversions.NullableTimestamp.ConvertToProviderExpression.Compile();

    /// <summary>See <see cref="TimestampText"/>.</summary>
    private static readonly Func<decimal, string> AmountText =
        OrderHistoryConversions.Amount.ConvertToProviderExpression.Compile();

    /// <summary>See <see cref="TimestampText"/>.</summary>
    private static readonly Func<decimal?, string> NullableAmountText =
        OrderHistoryConversions.NullableAmount.ConvertToProviderExpression.Compile();

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

        // The placement is a TRUE DB upsert (BUG-043): the pre-fix
        // check-then-act FindAsync/Add pair left a window where two concurrent
        // placements for the same (ExchangeId, OrderId) both saw null and the
        // second SaveChangesAsync died on the unique constraint. ONE statement
        // decides insert-or-update on the database side; SQLite merges on the
        // composite primary key, and a re-record (an adapter status refresh)
        // is a full row rewrite by contract.
        //
        // The interpolated SQL runs AROUND the EF value converters, so every
        // value is pre-converted through THE SAME converter the entity model
        // keeps on that column — the stored TEXT stays byte-consistent with
        // what the entity pipeline would have written.
        var exchangeId = order.ExchangeId;
        var orderId = order.Id;
        var symbol = order.Symbol.Normalized;
        var side = order.Side.ToString();
        var type = order.Type.ToString();
        var quantity = AmountText(order.Quantity.Value);
        var price = AmountText(order.Price.Value);
        var status = order.Status.ToString();
        var timeInForce = order.TimeInForce.ToString();
        var createdAt = TimestampText(order.CreatedAt);
        var updatedAt = NullableTimestampText(order.UpdatedAt);
        var filledQuantity = AmountText(order.FilledQuantity.Value);
        var clientOrderId = order.ClientOrderId;
        var stopPrice = NullableAmountText(order.StopPrice?.Value);
        var exchangeOrderId = order.ExchangeOrderId;
        var rejectReason = order.RejectReason;
        var expiry = NullableTimestampText(order.Expiry);
        var commission = NullableAmountText(order.Commission);
        var reduceOnly = order.ReduceOnly;
        var postOnly = order.PostOnly;

        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO Orders (
                ExchangeId, OrderId, Symbol, Side, Type, Quantity, Price, Status, TimeInForce,
                CreatedAt, UpdatedAt, FilledQuantity, ClientOrderId, StopPrice, ExchangeOrderId,
                RejectReason, Expiry, Commission, ReduceOnly, PostOnly)
            VALUES (
                {exchangeId}, {orderId}, {symbol}, {side}, {type}, {quantity}, {price}, {status},
                {timeInForce}, {createdAt}, {updatedAt}, {filledQuantity}, {clientOrderId}, {stopPrice},
                {exchangeOrderId}, {rejectReason}, {expiry}, {commission}, {reduceOnly}, {postOnly})
            ON CONFLICT (ExchangeId, OrderId) DO UPDATE SET
                Symbol = {symbol},
                Side = {side},
                Type = {type},
                Quantity = {quantity},
                Price = {price},
                Status = {status},
                TimeInForce = {timeInForce},
                CreatedAt = {createdAt},
                UpdatedAt = {updatedAt},
                FilledQuantity = {filledQuantity},
                ClientOrderId = {clientOrderId},
                StopPrice = {stopPrice},
                ExchangeOrderId = {exchangeOrderId},
                RejectReason = {rejectReason},
                Expiry = {expiry},
                Commission = {commission},
                ReduceOnly = {reduceOnly},
                PostOnly = {postOnly}
            """,
            cancellationToken);
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

        // A terminal status is final (BUG-042): a late or duplicate cancel
        // for an order that already Filled/Rejected/Expired — or whose
        // cancellation was already recorded — must be ignored, never a
        // silent rewrite of the recorded outcome. This store is the journal
        // of record for trading activity; it has no revisions.
        if (IsTerminal(entry.Status))
        {
            logger.LogWarning(
                "Order history: cancel for {ExchangeId}/{OrderId} arrived after the terminal status {Status}; the recorded outcome is kept",
                exchangeId,
                orderId,
                entry.Status);
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

    /// <summary>
    /// Whether an order status is final: Filled, Cancelled, Rejected and
    /// Expired end the order's lifecycle, while Pending, Submitted and
    /// PartiallyFilled can still transition anywhere. The journal-of-record
    /// rule (BUG-042) keys on this: no later event rewrites a terminal
    /// outcome.
    /// </summary>
    private static bool IsTerminal(OrderStatus status) =>
        status is OrderStatus.Filled
            or OrderStatus.Cancelled
            or OrderStatus.Rejected
            or OrderStatus.Expired;

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
        ReduceOnly = entry.ReduceOnly,
        PostOnly = entry.PostOnly,
        CreatedAt = entry.CreatedAt,
        UpdatedAt = entry.UpdatedAt,
        FilledQuantity = new Quantity(entry.FilledQuantity),
        ClientOrderId = entry.ClientOrderId,
        StopPrice = entry.StopPrice is null ? null : new Price(entry.StopPrice.Value),
        ExchangeOrderId = entry.ExchangeOrderId,
        RejectReason = entry.RejectReason,
        Expiry = entry.Expiry,
        Commission = entry.Commission,
    };

    #endregion Private Methods
}

