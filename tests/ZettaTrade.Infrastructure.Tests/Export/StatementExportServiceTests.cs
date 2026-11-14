namespace ZettaTrade.Infrastructure.Tests.Export;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NSubstitute;
using Xunit;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;
using Export = ZettaTrade.Infrastructure.Export;

/// <summary>
/// Goldens for the trade statement bundle (T-210), streamed (T-219): every
/// section rides the ONE T-084 CSV writer, the section names derive from
/// the manifest's base name, the manifest ties the bundle together (the
/// camelCase document carrying the filter echo and the per-file row
/// counts), and the manifest is written LAST — a bundle interrupted
/// mid-write leaves no manifest on disk, so a consumer treats the
/// manifest's presence as the completeness marker. The streamed pages
/// produce the materialized shape's bytes exactly: the same headers, the
/// same row order, the same manifest counts across the scan's pages.
/// </summary>
public sealed class StatementExportServiceTests
{
    #region Test Constants

    private static readonly DateTimeOffset BaseTime = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    #endregion Test Constants

    #region Bundle

    [Fact]
    public async Task StreamingBundle_WritesEverySection_AndTiesThemWithTheManifest()
    {
        await WithTempDirAsync(async directory =>
        {
            var manifestPath = Path.Combine(directory, "trade-statement.json");
            var service = new Export.StatementExportService(new Export.CsvExportService());

            var session = await service.BeginAsync(manifestPath, BuildHeader());
            await using (session)
            {
                await session.AppendOrdersPageAsync([BuildOrder(
                    id: "paper-1",
                    status: OrderStatus.Filled,
                    createdAt: BaseTime,
                    updatedAt: BaseTime.AddMinutes(2),
                    filledQuantity: 0.02m,
                    commission: 0.2004m)]);
                await session.CompleteAsync(BuildTail());
            }

            var files = Directory.GetFiles(directory).Select(Path.GetFileName).Order().ToArray();
            files.Should().Equal(
            [
                "trade-statement.balances.csv",
                "trade-statement.fills.csv",
                "trade-statement.json",
                "trade-statement.orders.csv",
                "trade-statement.realized-pnl.csv",
            ]);

            // The sections carry the T-084 goldens verbatim (the shared
            // writer, never a re-implementation) — the streamed page lands
            // the same bytes the materialized shape wrote.
            (await File.ReadAllTextAsync(Path.Combine(directory, "trade-statement.orders.csv"))).Should().Be(
                "id,exchange,symbol,side,type,price,quantity,filledQuantity,state,timeInForce,createdAt,updatedAt\r\n" +
                "paper-1,paper,BTC/USDT,Sell,Market,50100,0.02,0.02,Filled,GTC,2026-01-01T12:00:00.000Z,2026-01-01T12:02:00.000Z\r\n");
            (await File.ReadAllTextAsync(Path.Combine(directory, "trade-statement.fills.csv"))).Should().Be(
                "id,orderId,symbol,side,price,quantity,executedAt,fee\r\n" +
                "paper-1,paper-1,BTC/USDT,Sell,50100,0.02,2026-01-01T12:02:00.000Z,0.2004\r\n");
            (await File.ReadAllTextAsync(Path.Combine(directory, "trade-statement.realized-pnl.csv"))).Should().Be(
                "closedAt,exchange,symbol,side,quantity,entryPrice,exitPrice,realizedPnl,quoteCurrency\r\n" +
                "2026-01-01T12:02:00.000Z,paper,BTC/USDT,Sell,0.02,50100,50300,4,USDT\r\n");
            (await File.ReadAllTextAsync(Path.Combine(directory, "trade-statement.balances.csv"))).Should().Be(
                "asset,amount\r\n" +
                "BTC,0.5\r\n" +
                "USDT,1000\r\n");

            // The manifest ties the bundle: the filter echo, the row counts
            // and the derived section names.
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            var root = document.RootElement;
            root.GetProperty("generatedAt").GetString().Should().StartWith("2026-01-01T12:00:00");

            var filter = root.GetProperty("filter");
            filter.GetProperty("symbol").GetString().Should().Be("BTC/USDT");
            filter.GetProperty("exchange").ValueKind.Should().Be(JsonValueKind.Null);
            filter.GetProperty("fromUtc").ValueKind.Should().Be(JsonValueKind.Null);
            filter.GetProperty("toUtc").ValueKind.Should().Be(JsonValueKind.Null);
            filter.GetProperty("isPaper").GetBoolean().Should().BeTrue();

            var counts = root.GetProperty("counts");
            counts.GetProperty("orders").GetInt32().Should().Be(1);
            counts.GetProperty("fills").GetInt32().Should().Be(1);
            counts.GetProperty("realizedTrades").GetInt32().Should().Be(1);
            counts.GetProperty("balances").GetInt32().Should().Be(2);

            var filesElement = root.GetProperty("files");
            filesElement.GetArrayLength().Should().Be(4);
            filesElement[0].GetProperty("name").GetString().Should().Be("trade-statement.orders.csv");
            filesElement[0].GetProperty("rows").GetInt32().Should().Be(1);
            filesElement[1].GetProperty("name").GetString().Should().Be("trade-statement.fills.csv");
            filesElement[1].GetProperty("rows").GetInt32().Should().Be(1);
            filesElement[2].GetProperty("name").GetString().Should().Be("trade-statement.realized-pnl.csv");
            filesElement[2].GetProperty("rows").GetInt32().Should().Be(1);
            filesElement[3].GetProperty("name").GetString().Should().Be("trade-statement.balances.csv");
            filesElement[3].GetProperty("rows").GetInt32().Should().Be(2);
        });
    }

    [Fact]
    public async Task StreamingBundle_PagesAcrossTheScan_KeepTheCountsAndTheOrderWhole()
    {
        // A multi-page scan: the sections accumulate across the appends in
        // the scan order, the fills keep their ledger shape per page (the
        // submitted order books none), and the manifest counts every page's
        // rows — the whole-journal numbers without the whole-journal list.
        await WithTempDirAsync(async directory =>
        {
            var manifestPath = Path.Combine(directory, "trade-statement.json");
            var service = new Export.StatementExportService(new Export.CsvExportService());

            var session = await service.BeginAsync(manifestPath, BuildHeader());
            await using (session)
            {
                await session.AppendOrdersPageAsync(
                [
                    BuildOrder(
                        id: "filled-1",
                        status: OrderStatus.Filled,
                        createdAt: BaseTime,
                        updatedAt: BaseTime.AddMinutes(2),
                        filledQuantity: 0.02m,
                        commission: 0.2004m),
                    BuildOrder(
                        id: "working-1",
                        status: OrderStatus.Submitted,
                        createdAt: BaseTime.AddMinutes(3),
                        updatedAt: null,
                        filledQuantity: 0m,
                        commission: 0m),
                ]);
                await session.AppendOrdersPageAsync(
                [
                    BuildOrder(
                        id: "partial-1",
                        status: OrderStatus.PartiallyFilled,
                        createdAt: BaseTime.AddMinutes(5),
                        updatedAt: BaseTime.AddMinutes(6),
                        filledQuantity: 0.01m,
                        commission: 0.1m),
                ]);
                await session.CompleteAsync(BuildTail());
            }

            (await File.ReadAllTextAsync(Path.Combine(directory, "trade-statement.orders.csv"))).Should().Be(
                "id,exchange,symbol,side,type,price,quantity,filledQuantity,state,timeInForce,createdAt,updatedAt\r\n" +
                "filled-1,paper,BTC/USDT,Sell,Market,50100,0.02,0.02,Filled,GTC,2026-01-01T12:00:00.000Z,2026-01-01T12:02:00.000Z\r\n" +
                "working-1,paper,BTC/USDT,Sell,Market,50100,0.02,0,Submitted,GTC,2026-01-01T12:03:00.000Z,\r\n" +
                "partial-1,paper,BTC/USDT,Sell,Market,50100,0.02,0.01,PartiallyFilled,GTC,2026-01-01T12:05:00.000Z,2026-01-01T12:06:00.000Z\r\n");
            (await File.ReadAllTextAsync(Path.Combine(directory, "trade-statement.fills.csv"))).Should().Be(
                "id,orderId,symbol,side,price,quantity,executedAt,fee\r\n" +
                "filled-1,filled-1,BTC/USDT,Sell,50100,0.02,2026-01-01T12:02:00.000Z,0.2004\r\n" +
                "partial-1,partial-1,BTC/USDT,Sell,50100,0.01,2026-01-01T12:06:00.000Z,0.1\r\n");

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            var counts = document.RootElement.GetProperty("counts");
            counts.GetProperty("orders").GetInt32().Should().Be(3);
            counts.GetProperty("fills").GetInt32().Should().Be(2);
            counts.GetProperty("realizedTrades").GetInt32().Should().Be(1);
            counts.GetProperty("balances").GetInt32().Should().Be(2);

            var filesElement = document.RootElement.GetProperty("files");
            filesElement.EnumerateArray().Select(element => element.GetProperty("rows").GetInt32())
                .Should().Equal([3, 2, 1, 2]);
        });
    }

    [Fact]
    public async Task StreamingBundle_ATailFailure_LeavesNoManifestBehind()
    {
        // The manifest-last rule: the balances write faults after the
        // streamed sections and the realized ledger landed, the manifest
        // never lands — a torn bundle is detectable by its missing marker.
        await WithTempDirAsync(async directory =>
        {
            var manifestPath = Path.Combine(directory, "trade-statement.json");
            var sections = new RecordingSections();
            var csv = Substitute.For<Export.ICsvExportService>();
            csv.BeginOrderStreamAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(sections);
            csv.ExportRealizedTradesAsync(
                    Arg.Any<string>(), Arg.Any<IReadOnlyList<Export.StatementRealizedTrade>>(), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);
            csv.ExportBalancesAsync(
                    Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, Money>>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromException(new IOException("disk full")));
            var service = new Export.StatementExportService(csv);

            var session = await service.BeginAsync(manifestPath, BuildHeader());
            await using (session)
            {
                await session.AppendOrdersPageAsync([BuildOrder(
                    id: "paper-1",
                    status: OrderStatus.Filled,
                    createdAt: BaseTime,
                    updatedAt: BaseTime.AddMinutes(2),
                    filledQuantity: 0.02m,
                    commission: 0.2004m)]);

                await FluentActions.Awaiting(() => session.CompleteAsync(BuildTail()))
                    .Should().ThrowAsync<IOException>();
            }

            File.Exists(manifestPath).Should().BeFalse(
                "a manifest on disk means every section behind it landed whole");

            // The tail rode the fixed order: the realized ledger, then the
            // balances (which faulted) — the manifest would have been next.
            Received.InOrder(() =>
            {
                csv.BeginOrderStreamAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
                csv.ExportRealizedTradesAsync(
                    Arg.Any<string>(), Arg.Any<IReadOnlyList<Export.StatementRealizedTrade>>(), Arg.Any<CancellationToken>());
                csv.ExportBalancesAsync(
                    Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, Money>>(), Arg.Any<CancellationToken>());
            });

            // The scanned page rode the section stream, and the sections
            // closed despite the fault (the abandoned session released
            // them).
            sections.Pages.Should().HaveCount(1);
            sections.Disposals.Should().BeGreaterThanOrEqualTo(1);
        });
    }

    #endregion Bundle

    #region Helpers

    private static Export.StatementExportHeader BuildHeader() => new(
        SymbolFilter: "BTC/USDT",
        ExchangeFilter: null,
        FromUtc: null,
        ToUtc: null,
        IsPaperFilter: true);

    private static Export.StatementExportTail BuildTail() => new(
        GeneratedAt: BaseTime,
        RealizedTrades:
        [
            new Export.StatementRealizedTrade(
                ClosedAt: BaseTime.AddMinutes(2),
                ExchangeId: "paper",
                Symbol: "BTC/USDT",
                ClosingSide: "Sell",
                ClosingOrderId: "paper-2",
                Quantity: 0.02m,
                EntryPrice: 50_100m,
                ExitPrice: 50_300m,
                RealizedPnl: 4m,
                QuoteCurrency: "USDT",
                Note: null,
                Tags: null),
        ],
        Balances: new Dictionary<string, Money>
        {
            ["USDT"] = new(1_000m, "USDT"),
            ["BTC"] = new(0.5m, "BTC"),
        });

    private static Order BuildOrder(
        string id,
        OrderStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset? updatedAt,
        decimal filledQuantity,
        decimal commission) => new()
        {
            Id = id,
            ExchangeId = "paper",
            Symbol = Symbol.Parse("BTC/USDT"),
            Side = OrderSide.Sell,
            Type = OrderType.Market,
            Quantity = new Quantity(0.02m),
            Price = new Price(50_100m),
            Status = status,
            TimeInForce = TimeInForce.GTC,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            FilledQuantity = new Quantity(filledQuantity),
            Commission = commission,
        };

    private static async Task WithTempDirAsync(Func<string, Task> testBody)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"zettatrade-statement-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await testBody(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// The statement's section-stream fake: it records the appended pages
    /// and the disposals, so the failure facts can pin what rode the stream
    /// and what got released without a real file open.
    /// </summary>
    private sealed class RecordingSections : Export.IOrderSectionStream
    {
        public List<IReadOnlyList<Order>> Pages { get; } = [];

        public int Disposals { get; private set; }

        public Task AppendAsync(IReadOnlyList<Order> page, CancellationToken cancellationToken = default)
        {
            Pages.Add(page);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.CompletedTask;
        }
    }

    #endregion Helpers
}
