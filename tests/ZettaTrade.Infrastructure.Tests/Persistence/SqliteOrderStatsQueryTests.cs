namespace ZettaTrade.Infrastructure.Tests.Persistence;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Infrastructure.Exchange;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Tests for the SQLite order-stats query: the keyset walk (ascending
/// (CreatedAt, ExchangeId, OrderId), strictly after the previous page's last
/// order - the same-timestamp tie must straddle a page boundary without
/// skipping), the effective exhaustion (a short page, then an empty read),
/// the scope filters (symbol, exchange, inclusive-from/exclusive-to dates,
/// the venue paper/live tag), the per-page size clamp, and the annotation
/// ride-along (T-211): each row carries the journal row's note and tags.
/// </summary>
public sealed class SqliteOrderStatsQueryTests
{
    #region Test Constants

    private static readonly DateTimeOffset BaseTime = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    #endregion Test Constants

    #region Helpers

    private static async Task WithTempDatabaseAsync(
        Func<string, IOrderStatsQuery, Task> testBody)
    {
        var file = Path.Combine(
            Path.GetTempPath(),
            $"zettatrade-stats-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<OrderHistoryDbContext>()
                // Pooling=false: after each context disposes, its SQLite
                // connection closes, so the temp database file is unlocked.
                .UseSqlite($"Data Source={file};Pooling=false")
                .Options;
            await using (var context = new OrderHistoryDbContext(options))
            {
                await context.Database.EnsureCreatedAsync();
            }

            await testBody(file, new SqliteOrderStatsQuery(options));
        }
        finally
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    private static async Task SeedAsync(string databaseFile, params OrderHistoryEntry[] rows)
    {
        var options = new DbContextOptionsBuilder<OrderHistoryDbContext>()
            .UseSqlite($"Data Source={databaseFile};Pooling=false")
            .Options;
        await using var context = new OrderHistoryDbContext(options);
        context.Orders.AddRange(rows);
        await context.SaveChangesAsync();
    }

    private static OrderHistoryEntry BuildRow(
        string id,
        DateTimeOffset createdAt,
        string exchange = PaperTradingAdapter.ExchangeId,
        string symbol = "BTC/USDT",
        OrderStatus status = OrderStatus.Filled,
        decimal quantity = 1m,
        decimal? filledQuantity = null,
        string? note = null,
        string? tags = null) => new()
        {
            ExchangeId = exchange,
            OrderId = id,
            Symbol = symbol,
            Side = OrderSide.Buy,
            Type = OrderType.Limit,
            Quantity = quantity,
            Price = 10m,
            Status = status,
            TimeInForce = TimeInForce.GTC,
            ReduceOnly = false,
            PostOnly = false,
            CreatedAt = createdAt,
            FilledQuantity = filledQuantity ?? quantity,
            Note = note,
            Tags = tags,
        };

    #endregion Helpers

    #region Keyset Walk

    [Fact]
    public async Task ReadPageAsync_WalksAscendingAcrossPagesWithoutGapOrOverlap()
    {
        await WithTempDatabaseAsync(async (file, query) =>
        {
            var third = BaseTime.AddMinutes(2);
            await SeedAsync(
                file,
                BuildRow("r-1", BaseTime),
                BuildRow("r-2", BaseTime.AddMinutes(1)),
                BuildRow("r-3", third),
                BuildRow("r-4", BaseTime.AddMinutes(3)),
                BuildRow("r-5", BaseTime.AddMinutes(4)));

            var filter = new OrderStatsFilter(null, null, null, null, null);
            var page1 = await query.ReadPageAsync(filter, after: null, pageSize: 2);
            page1.Select(row => row.Order.Id).Should().Equal(["r-1", "r-2"]);

            var page2 = await query.ReadPageAsync(filter, page1[^1].Order, 2);
            page2.Select(row => row.Order.Id).Should().Equal(["r-3", "r-4"]);

            var page3 = await query.ReadPageAsync(filter, page2[^1].Order, 2);
            page3.Select(row => row.Order.Id).Should().Equal(["r-5"]);

            // One read past the last page reads exhaust: nothing more.
            var page4 = await query.ReadPageAsync(filter, page3[^1].Order, 2);
            page4.Should().BeEmpty();

            var walkedPages = new[] { page1, page2, page3 };
            walkedPages.SelectMany(page => page.Select(row => row.Order.Id))
                .Should().OnlyHaveUniqueItems("the keyset never replays a row");
        });
    }

    [Fact]
    public async Task ReadPageAsync_SameTimestampTiesStraddlePageBoundaryWithoutSkipping()
    {
        // Three rows share one CreatedAt: the tie-break runs to
        // (ExchangeId, OrderId). A page split right at the tie forces the
        // continuation anchor to carry BOTH tie-break columns — a
        // CreatedAt-only anchor would skip or loop here.
        await WithTempDatabaseAsync(async (file, query) =>
        {
            await SeedAsync(
                file,
                BuildRow("b-2", BaseTime, exchange: "binance"),
                BuildRow("b-1", BaseTime, exchange: "binance"),
                BuildRow("p-1", BaseTime, exchange: PaperTradingAdapter.ExchangeId),
                BuildRow("z-late", BaseTime.AddMinutes(1)));

            var filter = new OrderStatsFilter(null, null, null, null, null);
            var page1 = await query.ReadPageAsync(filter, after: null, pageSize: 2);

            page1.Select(row => row.Order.ExchangeId + "/" + row.Order.Id)
                .Should().Equal(["binance/b-1", "binance/b-2"]);

            var page2 = await query.ReadPageAsync(filter, page1[^1].Order, 2);
            page2.Select(row => row.Order.ExchangeId + "/" + row.Order.Id)
                .Should().Equal(["paper/p-1", "paper/z-late"]);
        });
    }

    #endregion Keyset Walk

    #region Scope Filters

    [Fact]
    public async Task ReadPageAsync_FiltersSymbolExchangeDatesAndVenueInsideTheDatabase()
    {
        await WithTempDatabaseAsync(async (file, query) =>
        {
            // In-scope rows: paper venue, ETH after the From bound
            // (inclusive), before the To bound (exclusive). Out-of-scope rows
            // cover each filter axis: another symbol, another exchange, rows
            // at BOTH date bounds (the boundary inclusivity pins) and a
            // zero-fill row the dashboard must still receive (the
            // accumulator, not the query, ignores zero fills).
            await SeedAsync(
                file,
                BuildRow("in-1", BaseTime.AddHours(1), symbol: "ETH/USDT"),
                BuildRow("in-2", BaseTime.AddHours(2), symbol: "ETH/USDT"),
                BuildRow("in-zero-fill", BaseTime.AddHours(3), symbol: "ETH/USDT", filledQuantity: 0m),
                BuildRow("out-symbol", BaseTime.AddHours(1), symbol: "BTC/USDT"),
                BuildRow("out-exchange", BaseTime.AddHours(1), exchange: "binance", symbol: "ETH/USDT"),
                BuildRow("out-before-from", BaseTime.AddMinutes(-1), symbol: "ETH/USDT"),
                BuildRow("out-at-to", BaseTime.AddHours(5), symbol: "ETH/USDT"));

            var filter = new OrderStatsFilter(
                Symbol: "ETH/USDT",
                Exchange: PaperTradingAdapter.ExchangeId,
                From: BaseTime,
                To: BaseTime.AddHours(5),
                IsPaper: true);

            var page = await query.ReadPageAsync(filter, after: null, pageSize: 100);
            page.Select(row => row.Order.Id).Should().Equal(["in-1", "in-2", "in-zero-fill"]);
        });
    }

    [Fact]
    public async Task ReadPageAsync_LiveVenueFilterExcludesPaperRows()
    {
        await WithTempDatabaseAsync(async (file, query) =>
        {
            await SeedAsync(
                file,
                BuildRow("live-1", BaseTime, exchange: "binance"),
                BuildRow("paper-1", BaseTime.AddMinutes(1)));

            var filter = new OrderStatsFilter(null, null, null, null, IsPaper: false);
            var page = await query.ReadPageAsync(filter, after: null, pageSize: 100);
            page.Select(row => row.Order.Id).Should().Equal(["live-1"]);
        });
    }

    [Fact]
    public async Task ReadPageAsync_UnfilteredScopeReturnsEverything()
    {
        await WithTempDatabaseAsync(async (file, query) =>
        {
            await SeedAsync(
                file,
                BuildRow("a-1", BaseTime, exchange: "binance"),
                BuildRow("p-1", BaseTime.AddMinutes(1)));

            var filter = new OrderStatsFilter(null, null, null, null, null);
            var page = await query.ReadPageAsync(filter, after: null, pageSize: 100);
            page.Select(row => row.Order.Id + "@" + row.Order.ExchangeId).Should().Equal(["a-1@binance", "p-1@paper"]);
        });
    }

    #endregion Scope Filters

    #region Page Clamp

    [Fact]
    public async Task ReadPageAsync_ClampsThePageSizeToTheBoundedWindow()
    {
        await WithTempDatabaseAsync(async (file, query) =>
        {
            await SeedAsync(
                file,
                BuildRow("r-1", BaseTime),
                BuildRow("r-2", BaseTime.AddMinutes(1)));

            var filter = new OrderStatsFilter(null, null, null, null, null);

            // A zero request reads as the smallest valid window; a garbage
            // huge request is clamped — never an unbounded read.
            (await query.ReadPageAsync(filter, after: null, pageSize: 0)).Should().HaveCount(1);
            (await query.ReadPageAsync(filter, after: null, pageSize: 1)).Should().HaveCount(1);
            (await query.ReadPageAsync(filter, after: null, pageSize: 500_000))
                .Should().HaveCount(2, "the clamp is far above any realistic page, not the row count");
        });
    }

    #endregion Page Clamp

    #region Annotation Ride-Along (T-211)

    [Fact]
    public async Task ReadPageAsync_CarriesTheRowsAnnotations_UnannotatedRowsStayNull()
    {
        await WithTempDatabaseAsync(async (file, query) =>
        {
            await SeedAsync(
                file,
                BuildRow("a-1", BaseTime),
                BuildRow("b-1", BaseTime.AddMinutes(1), note: "scaled out", tags: "swing,scalp"));

            var filter = new OrderStatsFilter(null, null, null, null, null);
            var page = await query.ReadPageAsync(filter, after: null, pageSize: 100);

            page.Should().HaveCount(2);
            page[0].Note.Should().BeNull("the honest unannotated");
            page[0].Tags.Should().BeNull();
            page[1].Note.Should().Be("scaled out");
            page[1].Tags.Should().Be("swing,scalp");
        });
    }

    #endregion Annotation Ride-Along (T-211)
}
