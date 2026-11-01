namespace ZettaTrade.Infrastructure.Tests.Persistence;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Tests for the SQLite order history store: round-trip fidelity, upsert per
/// (ExchangeId, OrderId), newest-first reading, cancellation marking, the
/// restart story (a fresh store instance reads another store's rows from the
/// same file), and the "no secrets" schema white-list.
/// </summary>
public sealed class SqliteOrderHistoryStoreTests
{
    #region Test Constants

    private static readonly DateTimeOffset BaseTime = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    #endregion Test Constants

    #region Helpers

    private static async Task WithTempDatabaseAsync(
        Func<string, SqliteOrderHistoryStore, Task> testBody)
    {
        var file = Path.Combine(
            Path.GetTempPath(),
            $"zettatrade-history-{Guid.NewGuid():N}.db");
        try
        {
            await testBody(file, CreateStore(file));
        }
        finally
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    private static SqliteOrderHistoryStore CreateStore(string databaseFile) => new(
        new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<OrderHistoryDbContext>()
            // Pooling=false: after each context disposes, its SQLite
            // connection closes, so the temp database file is unlocked.
            .UseSqlite($"Data Source={databaseFile};Pooling=false")
            .Options,
        NullLogger<SqliteOrderHistoryStore>.Instance);

    private static Order BuildOrder(
        string id = "paper-1",
        OrderStatus status = OrderStatus.Filled,
        DateTimeOffset? createdAt = null) => new()
        {
            Id = id,
            ExchangeId = "paper",
            Symbol = Symbol.Parse("BTC/USDT"),
            Side = OrderSide.Buy,
            Type = OrderType.Limit,
            Quantity = new Quantity(0.0125m),
            Price = new Price(49_999.99m),
            Status = status,
            TimeInForce = TimeInForce.GTC,
            CreatedAt = createdAt ?? BaseTime,
        };

    #endregion Helpers

    #region Happy Path

    [Fact]
    public async Task Initialize_ThenGetRecentOnEmptyStore_ReturnsEmpty()
    {
        await WithTempDatabaseAsync(async (file, store) =>
        {
            await store.InitializeAsync();

            (await store.GetRecentAsync(100)).Should().BeEmpty();
        });
    }

    [Fact]
    public async Task RecordPlacedAsync_RoundTripsAllOrderFields()
    {
        await WithTempDatabaseAsync(async (_, store) =>
        {
            await store.InitializeAsync();
            var placed = BuildOrder();
            await store.RecordPlacedAsync(placed);

            var orders = await store.GetRecentAsync(50);
            orders.Should().ContainSingle();
            var restored = orders[0];

            restored.Id.Should().Be(placed.Id);
            restored.ExchangeId.Should().Be(placed.ExchangeId);
            restored.Symbol.Normalized.Should().Be("BTC/USDT");
            restored.Side.Should().Be(OrderSide.Buy);
            restored.Type.Should().Be(OrderType.Limit);
            restored.Quantity.Value.Should().Be(0.0125m);
            restored.Price.Value.Should().Be(49_999.99m);
            restored.Status.Should().Be(OrderStatus.Filled);
            restored.TimeInForce.Should().Be(TimeInForce.GTC);
            restored.CreatedAt.Should().Be(BaseTime);
        });
    }

    [Fact]
    public async Task RecordPlacedAsync_TwiceForSameExchangeAndOrderId_UpsertsOneRow()
    {
        await WithTempDatabaseAsync(async (_, store) =>
        {
            await store.InitializeAsync();
            await store.RecordPlacedAsync(BuildOrder(status: OrderStatus.Pending));
            await store.RecordPlacedAsync(BuildOrder(status: OrderStatus.Filled));

            var orders = await store.GetRecentAsync(50);
            orders.Should().ContainSingle("the (ExchangeId, OrderId) pair is the identity");
            orders[0].Status.Should().Be(OrderStatus.Filled);
        });
    }

    [Fact]
    public async Task GetRecentAsync_ReturnsNewestFirst()
    {
        await WithTempDatabaseAsync(async (_, store) =>
        {
            await store.InitializeAsync();
            await store.RecordPlacedAsync(BuildOrder("paper-1", createdAt: BaseTime));
            await store.RecordPlacedAsync(BuildOrder("paper-2", createdAt: BaseTime.AddMinutes(1)));
            await store.RecordPlacedAsync(BuildOrder("paper-3", createdAt: BaseTime.AddMinutes(2)));

            var orders = await store.GetRecentAsync(50);
            orders.Select(order => order.Id).Should().Equal(["paper-3", "paper-2", "paper-1"]);
        });
    }

    [Fact]
    public async Task MarkCancelledAsync_SetsStatusAndUpdatedAtKeepsCreatedAt()
    {
        await WithTempDatabaseAsync(async (_, store) =>
        {
            await store.InitializeAsync();
            await store.RecordPlacedAsync(BuildOrder());

            var cancelledAt = BaseTime.AddMinutes(5);
            await store.MarkCancelledAsync("paper", "paper-1", cancelledAt);

            var orders = await store.GetRecentAsync(50);
            orders.Should().ContainSingle();
            orders[0].Status.Should().Be(OrderStatus.Cancelled);
            orders[0].UpdatedAt.Should().Be(cancelledAt);
            orders[0].CreatedAt.Should().Be(BaseTime);
        });
    }

    [Fact]
    public async Task MarkCancelledAsync_ForUnrecordedOrder_IsIgnoredWithoutThrowing()
    {
        await WithTempDatabaseAsync(async (_, store) =>
        {
            await store.InitializeAsync();

            var act = async () => await store.MarkCancelledAsync("paper", "ghost-1", BaseTime);
            await act.Should().NotThrowAsync();

            (await store.GetRecentAsync(50)).Should().BeEmpty();
        });
    }

    #endregion Happy Path

    #region Restart

    [Fact]
    public async Task FreshStoreInstance_ReadsRowsPersistedByAnotherStore()
    {
        // The acceptance check in miniature: place and cancel in "run one",
        // reopen the same database file in "run two" (a fresh store instance
        // with fresh contexts) and find the full state — still Cancelled.
        await WithTempDatabaseAsync(async (file, store) =>
        {
            await store.InitializeAsync();
            await store.RecordPlacedAsync(BuildOrder());
            await store.MarkCancelledAsync("paper", "paper-1", BaseTime.AddMinutes(5));

            var restartedStore = CreateStore(file);
            await restartedStore.InitializeAsync(); // migrations already applied: a no-op

            var orders = await restartedStore.GetRecentAsync(50);
            orders.Should().ContainSingle();
            orders[0].Id.Should().Be("paper-1");
            orders[0].Status.Should().Be(OrderStatus.Cancelled);
        });
    }

    #endregion Restart

    #region Schema Safety

    [Fact]
    public async Task OrdersSchema_ContainsOnlyKnownOrderColumns_NoSecretColumns()
    {
        // The "no secrets ever written to the DB" rule, guarded at the schema
        // level: the table can only carry order lifecycle fields, never
        // credential-like columns (API keys, secrets, tokens).
        var knownOrderColumns = new HashSet<string>(StringComparer.Ordinal)
        {
            "ExchangeId", "OrderId", "Symbol", "Side", "Type", "Quantity", "Price",
            "Status", "TimeInForce", "CreatedAt", "UpdatedAt", "FilledQuantity",
            "ClientOrderId", "StopPrice", "ExchangeOrderId", "RejectReason", "Expiry",
        };

        await WithTempDatabaseAsync(async (file, store) =>
        {
            await store.InitializeAsync();
            await store.RecordPlacedAsync(BuildOrder());

            // Pooling=false: pooled SQLite connections keep the file open,
            // which would block the temp-database delete below.
            await using var connection = new SqliteConnection($"Data Source={file};Pooling=false");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA table_info(Orders);";
            var columns = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    columns.Add(reader.GetString(reader.GetOrdinal("name")));
                }
            }

            columns.Should().HaveCount(knownOrderColumns.Count);
            columns.All(knownOrderColumns.Contains).Should().BeTrue();
        });
    }

    #endregion Schema Safety
}
