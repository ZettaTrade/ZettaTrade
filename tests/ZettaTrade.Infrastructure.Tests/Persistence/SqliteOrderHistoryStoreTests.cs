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
/// (ExchangeId, OrderId), newest-first reading, cancellation marking (a live
/// order only — a terminal recorded status is final), placement concurrency
/// (parallel placements and the place/cancel race), the restart story (a
/// fresh store instance reads another store's rows from the same file), and
/// the "no secrets" schema white-list.
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
    public async Task RecordPlacedAsync_RoundTripsTheExecutionFlags()
    {
        await WithTempDatabaseAsync(async (_, store) =>
        {
            await store.InitializeAsync();
            var placed = BuildOrder() with { ReduceOnly = true, PostOnly = true };
            await store.RecordPlacedAsync(placed);

            var orders = await store.GetRecentAsync(50);
            orders.Should().ContainSingle();
            var restored = orders[0];

            restored.ReduceOnly.Should().BeTrue();
            restored.PostOnly.Should().BeTrue();

            // The unflagged default round-trips as false (the migration's
            // backfilled rows read the same way).
            await store.RecordPlacedAsync(BuildOrder("paper-2"));
            var plain = (await store.GetRecentAsync(50)).Single(order => order.Id == "paper-2");
            plain.ReduceOnly.Should().BeFalse();
            plain.PostOnly.Should().BeFalse();
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
            // A cancel applies to a LIVE order: the journal holds a
            // non-terminal status first (a terminal one is final — see the
            // matrix below).
            await store.RecordPlacedAsync(BuildOrder(status: OrderStatus.Pending));

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

    /// <summary>
    /// BUG-042, the terminal matrix: once a row records a final outcome
    /// (Filled, Cancelled, Rejected or Expired), a later or duplicate cancel
    /// for it is ignored — the status and the never-touched UpdatedAt stay
    /// exactly as placed, so the journal carries no silent rewrites.
    /// </summary>
    [Theory]
    [InlineData(OrderStatus.Filled)]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Rejected)]
    [InlineData(OrderStatus.Expired)]
    public async Task MarkCancelledAsync_TerminalRecordedStatus_IsIgnoredWithoutARewrite(
        OrderStatus terminalStatus)
    {
        await WithTempDatabaseAsync(async (_, store) =>
        {
            await store.InitializeAsync();
            await store.RecordPlacedAsync(BuildOrder(status: terminalStatus));

            await store.MarkCancelledAsync("paper", "paper-1", BaseTime.AddMinutes(5));

            var orders = await store.GetRecentAsync(50);
            orders.Should().ContainSingle("the cancel neither creates nor duplicates rows");
            orders[0].Status.Should().Be(terminalStatus, "a terminal outcome is final");
            orders[0].UpdatedAt.Should().BeNull("the cancel never rewrote the row");
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
            await store.RecordPlacedAsync(BuildOrder(status: OrderStatus.Pending)); // live, then cancelled
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

    #region The Reset Wipe

    [Fact]
    public async Task DeleteExchangeAsync_RemovesOnlyThatExchangeRows()
    {
        // The paper reset's persistence half (T-192): exactly the named
        // exchange's rows leave; every other venue's journal of record stays.
        await WithTempDatabaseAsync(async (_, store) =>
        {
            await store.InitializeAsync();
            await store.RecordPlacedAsync(BuildOrder("paper-1", createdAt: BaseTime));
            await store.RecordPlacedAsync(BuildOrder("binance-1", createdAt: BaseTime.AddMinutes(1)) with
            {
                ExchangeId = "binance",
            });
            await store.RecordPlacedAsync(BuildOrder("paper-2", createdAt: BaseTime.AddMinutes(2)));

            var deleted = await store.DeleteExchangeAsync("paper");

            deleted.Should().Be(2, "both paper rows left");
            var orders = await store.GetRecentAsync(50);
            orders.Select(order => order.Id).Should().Equal(
                ["binance-1"],
                "the other exchange's journal of record stays");
        });
    }

    #endregion The Reset Wipe

    #region Concurrency

    /// <summary>
    /// BUG-043, the race the bug report names: two store instances on the
    /// SAME database file place the SAME order concurrently. Both succeed —
    /// the true ON CONFLICT upsert decides insert-or-update on the database
    /// side, so the check-then-act window (both FindAsync calls see null,
    /// the second SaveChanges dies on the unique constraint) is closed. The
    /// single row ends in one of the two recorded statuses, whichever
    /// committed last.
    /// </summary>
    [Fact]
    public async Task RecordPlacedAsync_ConcurrentPlacementsOfTheSameOrder_NeverThrowAndLeaveOneRow()
    {
        await WithTempDatabaseAsync(async (file, store) =>
        {
            await store.InitializeAsync();
            var second = CreateStore(file);
            await second.InitializeAsync();

            var act = async () => await Task.WhenAll(
                store.RecordPlacedAsync(BuildOrder(status: OrderStatus.Pending)),
                second.RecordPlacedAsync(BuildOrder(status: OrderStatus.Filled)));
            await act.Should().NotThrowAsync();

            var orders = await store.GetRecentAsync(50);
            orders.Should().ContainSingle("the (ExchangeId, OrderId) identity merged the writers");
            orders[0].Status.Should().BeOneOf(
                new[] { OrderStatus.Pending, OrderStatus.Filled },
                "each completed write is a valid final state");
        });
    }

    /// <summary>
    /// The place/cancel race smoke inside one store: a cancel can reach the
    /// journal while the placement of the same order is still in flight.
    /// Both complete without throwing and the row ends precisely in one of
    /// the two valid outcomes — Cancelled (the cancel won) or Pending (the
    /// cancel arrived first and was ignored as unrecorded) — never a partial
    /// write and never two rows.
    /// </summary>
    [Fact]
    public async Task RecordPlacedAndMarkCancelled_RacingOnOneStore_NeverThrowAndLeaveOneConsistentRow()
    {
        await WithTempDatabaseAsync(async (_, store) =>
        {
            await store.InitializeAsync();

            var act = async () => await Task.WhenAll(
                store.RecordPlacedAsync(BuildOrder(status: OrderStatus.Pending)),
                store.MarkCancelledAsync("paper", "paper-1", BaseTime.AddMinutes(5)));
            await act.Should().NotThrowAsync();

            var orders = await store.GetRecentAsync(50);
            orders.Should().ContainSingle();
            orders[0].Status.Should().BeOneOf(
                new[] { OrderStatus.Pending, OrderStatus.Cancelled },
                "the cancel either defers to the placement or applies on it");
        });
    }

    #endregion Concurrency

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
            "Commission", "ReduceOnly", "PostOnly", "Note", "Tags",
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

    #region Trade Meta (T-211)

    [Fact]
    public async Task SetTradeMeta_WritesTheAnnotations_AndTheySurviveTheRestart()
    {
        // The acceptance in miniature: the note and tags written in "run
        // one" read back in "run two" (a fresh store instance over the same
        // file) — the annotations persist like any journal row.
        await WithTempDatabaseAsync(async (file, store) =>
        {
            await store.InitializeAsync();
            await store.RecordPlacedAsync(BuildOrder());

            var saved = await store.SetTradeMetaAsync("paper", "paper-1", "scaled out at the level", "swing,scalp");

            saved.Should().BeTrue();
            var meta = await store.GetTradeMetaAsync([("paper", "paper-1")]);
            meta.Should().ContainSingle();
            meta[0].Note.Should().Be("scaled out at the level");
            meta[0].Tags.Should().Be("swing,scalp");

            var restartedStore = CreateStore(file);
            await restartedStore.InitializeAsync();
            var afterRestart = await restartedStore.GetTradeMetaAsync([("paper", "paper-1")]);
            afterRestart.Should().ContainSingle("the annotations survive the restart");
            afterRestart[0].Note.Should().Be("scaled out at the level");
            afterRestart[0].Tags.Should().Be("swing,scalp");
        });
    }

    [Fact]
    public async Task SetTradeMeta_ReplacesBothFieldsWhole_ANullClears()
    {
        // The annotation write replaces the pair whole: a null note and null
        // tags clear both — the honest removal, never a half-cleared row.
        await WithTempDatabaseAsync(async (_, store) =>
        {
            await store.InitializeAsync();
            await store.RecordPlacedAsync(BuildOrder());
            await store.SetTradeMetaAsync("paper", "paper-1", "note", "swing");

            var cleared = await store.SetTradeMetaAsync("paper", "paper-1", null, null);
            cleared.Should().BeTrue();

            (await store.GetTradeMetaAsync([("paper", "paper-1")])).Should().BeEmpty(
                "a fully cleared row is the honest unannotated again");
        });
    }

    [Fact]
    public async Task SetTradeMeta_UnrecordedOrder_ReturnsFalseAndWritesNothing()
    {
        await WithTempDatabaseAsync(async (_, store) =>
        {
            await store.InitializeAsync();

            var saved = await store.SetTradeMetaAsync("paper", "ghost-1", "note", "swing");

            saved.Should().BeFalse("the store refuses to fabricate a journal row");
            (await store.GetTradeMetaAsync([("paper", "ghost-1")])).Should().BeEmpty();
        });
    }

    [Fact]
    public async Task RecordPlacedAfterSetTradeMeta_TheStatusRefreshNeverTouchesTheAnnotations()
    {
        // The adapter's placement upsert rewrites the order lifecycle
        // fields by contract and lists its columns explicitly — the user
        // annotations are not among them, so a re-record (a status refresh)
        // cannot wipe what the user wrote.
        await WithTempDatabaseAsync(async (_, store) =>
        {
            await store.InitializeAsync();
            await store.RecordPlacedAsync(BuildOrder(status: OrderStatus.Pending));
            await store.SetTradeMetaAsync("paper", "paper-1", "note", "swing");

            await store.RecordPlacedAsync(BuildOrder(status: OrderStatus.Filled));

            var meta = await store.GetTradeMetaAsync([("paper", "paper-1")]);
            meta.Should().ContainSingle("the annotations survive the re-record");
            meta[0].Note.Should().Be("note");
            meta[0].Tags.Should().Be("swing");
            (await store.GetRecentAsync(50))[0].Status.Should().Be(OrderStatus.Filled);
        });
    }

    [Fact]
    public async Task GetTradeMeta_ReturnsOnlyAnnotatedRows_IsolatedPerExchange()
    {
        // One bulk read: unannotated rows never come back, and the same
        // order id on another exchange never leaks its annotations across.
        await WithTempDatabaseAsync(async (_, store) =>
        {
            await store.InitializeAsync();
            await store.RecordPlacedAsync(BuildOrder("paper-1"));
            await store.RecordPlacedAsync(BuildOrder("paper-2", createdAt: BaseTime.AddMinutes(1)));
            await store.RecordPlacedAsync(BuildOrder("binance-1", createdAt: BaseTime.AddMinutes(2)) with
            {
                ExchangeId = "binance",
            });
            await store.SetTradeMetaAsync("paper", "paper-1", "the paper note", "swing");
            await store.SetTradeMetaAsync("binance", "binance-1", "the binance note", " scalp ");

            var meta = await store.GetTradeMetaAsync(
            [
                ("paper", "paper-1"),
                ("paper", "paper-2"),
                ("binance", "binance-1"),
            ]);

            meta.Should().HaveCount(2, "the unannotated row stays absent");
            var paper = meta.Should().ContainSingle(entry => entry.ExchangeId == "paper").Subject;
            paper.OrderId.Should().Be("paper-1");
            paper.Note.Should().Be("the paper note");
            paper.Tags.Should().Be("swing");
            var binance = meta.Should().ContainSingle(entry => entry.ExchangeId == "binance").Subject;
            binance.OrderId.Should().Be("binance-1");
            binance.Note.Should().Be("the binance note");
            binance.Tags.Should().Be(" scalp ");
        });
    }

    [Fact]
    public async Task GetTradeMeta_EmptyKeys_ReturnsEmptyWithoutAQuery()
    {
        await WithTempDatabaseAsync(async (_, store) =>
        {
            await store.InitializeAsync();

            (await store.GetTradeMetaAsync([])).Should().BeEmpty();
        });
    }

    [Fact]
    public async Task Migrations_ProduceTheAnnotationColumns()
    {
        // The migration battery: a fresh database walks the whole migration
        // chain (InitializeAsync runs Migrate) and the Orders table ends
        // with the two annotation columns the model snapshot carries.
        await WithTempDatabaseAsync(async (file, store) =>
        {
            await store.InitializeAsync();

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

            columns.Should().Contain("Note").And.Contain("Tags");
        });
    }

    #endregion Trade Meta (T-211)
}
