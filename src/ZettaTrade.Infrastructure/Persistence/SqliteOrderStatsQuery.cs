namespace ZettaTrade.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// SQLite <see cref="IOrderStatsQuery"/>. One <see cref="OrderHistoryDbContext"/>
/// per operation — the same cheap pattern as <see cref="SqliteOrderHistoryStore"/>
/// (they share the singleton options object which only carries the connection
/// string). The paper-live tag matches on the community's adapter id constant:
/// the paper ledger is the only venue whose Filled rows are guaranteed
/// whole-order fills, and the filter is what lets the dashboard scope to it.
/// </summary>
public sealed class SqliteOrderStatsQuery(
    DbContextOptions<OrderHistoryDbContext> options) : IOrderStatsQuery
{
    /// <summary>Upper pageSize clamp; the query never reads an unbounded window.</summary>
    private const int MaxPageSize = 10_000;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<OrderPageRow>> ReadPageAsync(
        OrderStatsFilter filter,
        Order? after,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var take = Math.Clamp(pageSize, 1, MaxPageSize);
        await using var dbContext = new OrderHistoryDbContext(options);

        var entries = await BuildPageQuery(dbContext, filter, after, take)
            .ToListAsync(cancellationToken);

        return entries.Select(ToPageRow).ToArray();
    }

    /// <summary>
    /// One keyset page: the filters ride the WHERE clause (so wide scopes
    /// filter inside the database, never in memory), and the continuation
    /// anchor turns into a lexicographic tuple constraint over the same
    /// (CreatedAt, ExchangeId, OrderId) ordering the page keeps — TEXT
    /// columns of canonical UTC ISO-8601 timestamps and plain identifiers
    /// compare chronologically as stored.
    /// </summary>
    private static IQueryable<OrderHistoryEntry> BuildPageQuery(
        OrderHistoryDbContext dbContext,
        OrderStatsFilter filter,
        Order? after,
        int take)
    {
        IQueryable<OrderHistoryEntry> rows = dbContext.Orders;

        if (!string.IsNullOrEmpty(filter.Symbol))
        {
            rows = rows.Where(entry => entry.Symbol == filter.Symbol);
        }

        if (!string.IsNullOrEmpty(filter.Exchange))
        {
            rows = rows.Where(entry => entry.ExchangeId == filter.Exchange);
        }

        if (filter.From is { } from)
        {
            rows = rows.Where(entry => entry.CreatedAt >= from);
        }

        if (filter.To is { } to)
        {
            rows = rows.Where(entry => entry.CreatedAt < to);
        }

        if (filter.IsPaper is { } isPaper)
        {
            rows = isPaper
                ? rows.Where(entry => entry.ExchangeId == PaperTradingAdapter.ExchangeId)
                : rows.Where(entry => entry.ExchangeId != PaperTradingAdapter.ExchangeId);
        }

        if (after is { } anchor)
        {
            rows = rows.Where(entry =>
                entry.CreatedAt > anchor.CreatedAt
                || (entry.CreatedAt == anchor.CreatedAt
                    && string.Compare(entry.ExchangeId, anchor.ExchangeId) > 0)
                || (entry.CreatedAt == anchor.CreatedAt
                    && entry.ExchangeId == anchor.ExchangeId
                    && string.Compare(entry.OrderId, anchor.Id) > 0));
        }



        return rows
            .OrderBy(entry => entry.CreatedAt)
            .ThenBy(entry => entry.ExchangeId)
            .ThenBy(entry => entry.OrderId)
            .Take(take);
    }

    /// <summary>Map a journal row onto the Core <see cref="Order"/> record shape.</summary>
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
    };

    /// <summary>
    /// Map a journal row onto the paged row shape: the Core <see cref="Order"/>
    /// the panels already serve, plus the row's user trade annotations
    /// (T-211) exactly as the journal keeps them.
    /// </summary>
    private static OrderPageRow ToPageRow(OrderHistoryEntry entry) =>
        new(ToOrder(entry), entry.Note, entry.Tags);
}
