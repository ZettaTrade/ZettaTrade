namespace ZettaTrade.Infrastructure.Tests.Export;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;
using Export = ZettaTrade.Infrastructure.Export;

/// <summary>
/// Goldens for the CSV export service: the RFC 4180 quoting (embedded
/// commas, quotes and newlines survive a re-parse), the invariant decimals
/// under a ru-RU host culture (the machine file never spells "0,01"), the
/// ISO-8601 UTC timestamps (any input offset converts to the Z form), the
/// three column sets (orders, fills mirror the trade ledger, candles
/// carry the full OHLCV), the empty set yielding a header-only file, and
/// the cancellation discipline (no torn row on disk).
/// </summary>
public sealed class CsvExportServiceTests
{
    #region Test Constants

    private static readonly DateTimeOffset BaseTime = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly Export.CsvExportService Service = new();

    #endregion Test Constants

    #region Orders

    [Fact]
    public async Task ExportOrdersAsync_WritesTheGoldenHeaderAndRow()
    {
        await WithTempFileAsync(async path =>
        {
            var order = BuildOrder(
                id: "paper-1",
                status: OrderStatus.Filled,
                createdAt: BaseTime,
                updatedAt: BaseTime.AddMinutes(2),
                filledQuantity: 0.02m);

            await Service.ExportOrdersAsync(path, [order]);

            (await File.ReadAllTextAsync(path)).Should().Be(
                "id,exchange,symbol,side,type,price,quantity,filledQuantity,state,timeInForce,createdAt,updatedAt\r\n" +
                "paper-1,paper,BTC/USDT,Sell,Market,50100,0.02,0.02,Filled,GTC,2026-01-01T12:00:00.000Z,2026-01-01T12:02:00.000Z\r\n");
        });
    }

    [Fact]
    public async Task ExportOrdersAsync_Withoutupdates_LeavesTheCellEmpty()
    {
        await WithTempFileAsync(async path =>
        {
            var order = BuildOrder(
                id: "paper-1",
                status: OrderStatus.Submitted,
                createdAt: BaseTime,
                updatedAt: null,
                filledQuantity: 0m);

            await Service.ExportOrdersAsync(path, [order]);

            var lines = (await File.ReadAllLinesAsync(path)).Should().HaveCount(2).And.Subject.ToArray();
            lines[1].Should().EndWith(",GTC,2026-01-01T12:00:00.000Z,");
        });
    }

    [Fact]
    public async Task ExportOrdersAsync_EscapesCommasQuotesAndNewlines_PerRfc4180()
    {
        await WithTempFileAsync(async path =>
        {
            // The edge fields: a symbol carrying a comma and quote
            // characters, an id carrying the same, and a rejection reason
            // driving multi-line content inside one quoted cell.
            var order = BuildOrder(
                id: "id,with\"quotes",
                status: OrderStatus.Rejected,
                createdAt: BaseTime,
                updatedAt: null,
                filledQuantity: 0m);
            order = order with
            {
                Symbol = Symbol.Parse("X,Y\"Z/USDT"),
                // The exchange id rides an exported column, so the
                // multi-line content lands inside one quoted cell.
                ExchangeId = "exchange\r\nname",
            };

            await Service.ExportOrdersAsync(path, [order]);

            // The file re-parses to exactly the written fields: the escaping
            // is lossless for every quoted trigger character.
            var rows = ParseCsv(await File.ReadAllTextAsync(path));
            rows.Should().HaveCount(2);
            rows[1].Should().Equal([
                "id,with\"quotes",
                "exchange\r\nname",
                "X,Y\"Z/USDT",
                "Sell",
                "Market",
                "50100",
                "0.02",
                "0",
                "Rejected",
                "GTC",
                "2026-01-01T12:00:00.000Z",
                "",
            ]);
            rows[1][0].Should().Contain(",").And.Contain("\"");
            rows[1][2].Should().Contain(",").And.Contain("\"");
            rows[1][1].Should().Contain("\r\n");
        });
    }

    [Fact]
    public async Task ExportOrdersAsync_EmptySet_ProducesAHeaderOnlyFile()
    {
        await WithTempFileAsync(async path =>
        {
            await Service.ExportOrdersAsync(path, []);

            (await File.ReadAllTextAsync(path)).Should().Be(
                "id,exchange,symbol,side,type,price,quantity,filledQuantity,state,timeInForce,createdAt,updatedAt\r\n");
        });
    }

    [Fact]
    public async Task ExportOrdersAsync_UnderRuRuHostCulture_NeverEmitsCommaDecimals()
    {
        await WithTempFileAsync(async path =>
        {
            using var cultureScope = new CultureScope("ru-RU");
            var order = BuildOrder(
                id: "paper-1",
                status: OrderStatus.Filled,
                createdAt: BaseTime,
                updatedAt: null,
                filledQuantity: 0.02m,
                price: 50_100.55m);

            await Service.ExportOrdersAsync(path, [order]);

            var content = await File.ReadAllTextAsync(path);
            content.Should().Contain("50100.55").And.Contain("0.02");
            content.Should().NotContain("0,02").And.NotContain("50100,55");
        });
    }

    [Fact]
    public async Task ExportOrdersAsync_ConvertsAnyInputOffsetToUtcIso()
    {
        await WithTempFileAsync(async path =>
        {
            // The journal may carry a non-zero offset: the file always
            // spells the converted UTC moment with the Z designator.
            var order = BuildOrder(
                id: "paper-1",
                status: OrderStatus.Submitted,
                createdAt: new DateTimeOffset(2026, 1, 1, 14, 0, 0, TimeSpan.FromHours(2)),
                updatedAt: null,
                filledQuantity: 0m);

            await Service.ExportOrdersAsync(path, [order]);

            (await File.ReadAllTextAsync(path)).Should().Contain(
                "2026-01-01T12:00:00.000Z");
        });
    }

    [Fact]
    public async Task ExportOrdersAsync_NonexistentDirectory_FailsHonestly()
    {
        await FluentActions.Awaiting(() => Service.ExportOrdersAsync(
                Path.Combine(Path.GetTempPath(), $"zt-missing-{Guid.NewGuid():N}", "orders.csv"),
                new List<Order>()))
            .Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [Fact]
    public async Task ExportOrdersAsync_CancellationBetweenRows_KeepsOnlyWholeRows()
    {
        // A two-bar lazy series whose second read cancels: the header and
        // the first row stay whole on disk, the second never gets a torn
        // write past the cancellation point.
        using var cts = new CancellationTokenSource();
        var orders = new CancelAfterNList<Order>(
            [
                BuildOrder("paper-1", OrderStatus.Submitted, BaseTime, null, 0m),
                BuildOrder("paper-2", OrderStatus.Submitted, BaseTime, null, 0m),
            ],
            cancelAfter: 1,
            cts);

        await WithTempFileAsync(async path =>
        {
            await FluentActions.Awaiting(() => Service.ExportOrdersAsync(path, orders, cts.Token))
                .Should().ThrowAsync<OperationCanceledException>();

            var lines = (await File.ReadAllLinesAsync(path)).Should().HaveCount(2).And.Subject.ToArray();
            lines[0].Should().Be("id,exchange,symbol,side,type,price,quantity,filledQuantity,state,timeInForce,createdAt,updatedAt");
            lines[1].Should().Be("paper-1,paper,BTC/USDT,Sell,Market,50100,0.02,0,Submitted,GTC,2026-01-01T12:00:00.000Z,");
        });
    }

    #endregion Orders

    #region Fills

    [Fact]
    public async Task ExportFillsAsync_MirrorsTheLedgerShapeAndSkipsUnexecutedOrders()
    {
        await WithTempFileAsync(async path =>
        {
            var executed = BuildOrder(
                id: "fill-order",
                status: OrderStatus.Filled,
                createdAt: BaseTime,
                updatedAt: BaseTime.AddSeconds(90),
                filledQuantity: 0.02m,
                commission: 0.2004m);
            var partial = BuildOrder(
                id: "partial-order",
                status: OrderStatus.PartiallyFilled,
                createdAt: BaseTime.AddMinutes(5),
                updatedAt: null,
                filledQuantity: 0.01m);
            var untouched = BuildOrder(
                id: "working-order",
                status: OrderStatus.Submitted,
                createdAt: BaseTime.AddMinutes(6),
                updatedAt: null,
                filledQuantity: 0m);

            await Service.ExportFillsAsync(path, [executed, partial, untouched]);

            (await File.ReadAllTextAsync(path)).Should().Be(
                "id,orderId,symbol,side,price,quantity,executedAt,fee\r\n" +
                "fill-order,fill-order,BTC/USDT,Sell,50100,0.02,2026-01-01T12:01:30.000Z,0.2004\r\n" +
                "partial-order,partial-order,BTC/USDT,Sell,50100,0.01,2026-01-01T12:05:00.000Z,\r\n");
        });
    }

    [Fact]
    public async Task ExportFillsAsync_EmptySet_ProducesAHeaderOnlyFile()
    {
        await WithTempFileAsync(async path =>
        {
            await Service.ExportFillsAsync(path, []);

            (await File.ReadAllTextAsync(path)).Should().Be("id,orderId,symbol,side,price,quantity,executedAt,fee\r\n");
        });
    }

    #endregion Fills

    #region Candles

    [Fact]
    public async Task ExportCandlesAsync_WritesEveryLoadedCandleInInvariantOhlcv()
    {
        await WithTempFileAsync(async path =>
        {
            using var cultureScope = new CultureScope("ru-RU");
            IReadOnlyList<Bar> bars =
            [
                new(BaseTime, 100.5m, 110m, 99.9m, 105m, 12.34m),
                new(BaseTime.AddHours(1), 105m, 120m, 104.44m, 119m, 0.005m),
            ];

            await Service.ExportCandlesAsync(path, bars);

            (await File.ReadAllTextAsync(path)).Should().Be(
                "timestamp,open,high,low,close,volume\r\n" +
                "2026-01-01T12:00:00.000Z,100.5,110,99.9,105,12.34\r\n" +
                "2026-01-01T13:00:00.000Z,105,120,104.44,119,0.005\r\n");
        });
    }

    [Fact]
    public async Task ExportCandlesAsync_EmptySet_ProducesAHeaderOnlyFile()
    {
        await WithTempFileAsync(async path =>
        {
            await Service.ExportCandlesAsync(path, []);

            (await File.ReadAllTextAsync(path)).Should().Be("timestamp,open,high,low,close,volume\r\n");
        });
    }

    [Fact]
    public async Task ExportCandlesAsync_CancellationBetweenRows_KeepsOnlyWholeRows()
    {
        // The same between-rows discipline the orders carry, pinned on the
        // candles surface too: the header and the first candle stay whole,
        // the second is never torn past the cancellation point.
        using var cts = new CancellationTokenSource();
        IReadOnlyList<Bar> bars =
        [
            new(BaseTime, 100.5m, 110m, 99.9m, 105m, 12.34m),
            new(BaseTime.AddHours(1), 105m, 120m, 104.44m, 119m, 0.005m),
        ];
        var lazyBars = new CancelAfterNList<Bar>(bars, cancelAfter: 1, cts);

        await WithTempFileAsync(async path =>
        {
            await FluentActions.Awaiting(() => Service.ExportCandlesAsync(path, lazyBars, cts.Token))
                .Should().ThrowAsync<OperationCanceledException>();

            var lines = (await File.ReadAllLinesAsync(path)).Should().HaveCount(2).And.Subject.ToArray();
            lines[0].Should().Be("timestamp,open,high,low,close,volume");
            lines[1].Should().Be("2026-01-01T12:00:00.000Z,100.5,110,99.9,105,12.34");
        });
    }

    #endregion Candles

    #region Positions (T-210)

    [Fact]
    public async Task ExportPositionsAsync_WritesTheGoldenHeaderAndRow()
    {
        await WithTempFileAsync(async path =>
        {
            var position = new Position
            {
                Id = "pos-1",
                ExchangeId = "paper",
                Symbol = Symbol.Parse("BTC/USDT"),
                Side = OrderSide.Buy,
                Quantity = new Quantity(0.5m),
                EntryPrice = new Price(50_000m),
                CurrentPrice = new Price(51_000m),
                OpenedAt = BaseTime,
                StopLoss = new Price(49_000m),
                TakeProfit = new Price(53_000m),
            };

            await Service.ExportPositionsAsync(path, [position]);

            (await File.ReadAllTextAsync(path)).Should().Be(
                "id,exchange,symbol,side,quantity,entryPrice,currentPrice,openedAt,stopLoss,takeProfit,unrealizedPnl\r\n" +
                "pos-1,paper,BTC/USDT,Buy,0.5,50000,51000,2026-01-01T12:00:00.000Z,49000,53000,500.0\r\n",
                "the long's unrealized PnL books (51 000 - 50 000) × 0.5 in the quote");
        });
    }

    [Fact]
    public async Task ExportPositionsAsync_QuotelessSymbol_LeavesThePnlCellEmpty()
    {
        // A stock position carries no quote asset: the PnL is the honest
        // unknown — an empty cell, never a fabricated currency.
        await WithTempFileAsync(async path =>
        {
            var position = new Position
            {
                Id = "pos-2",
                ExchangeId = "paper",
                Symbol = Symbol.Parse("AAPL"),
                Side = OrderSide.Sell,
                Quantity = new Quantity(10m),
                EntryPrice = new Price(190m),
                CurrentPrice = new Price(185m),
                OpenedAt = BaseTime.AddMinutes(5),
            };

            await Service.ExportPositionsAsync(path, [position]);

            var lines = (await File.ReadAllLinesAsync(path)).Should().HaveCount(2).And.Subject.ToArray();
            lines[1].Should().Be("pos-2,paper,AAPL,Sell,10,190,185,2026-01-01T12:05:00.000Z,,,");
        });
    }

    [Fact]
    public async Task ExportPositionsAsync_EmptySet_ProducesAHeaderOnlyFile()
    {
        await WithTempFileAsync(async path =>
        {
            await Service.ExportPositionsAsync(path, []);

            (await File.ReadAllTextAsync(path)).Should().Be(
                "id,exchange,symbol,side,quantity,entryPrice,currentPrice,openedAt,stopLoss,takeProfit,unrealizedPnl\r\n");
        });
    }

    [Fact]
    public async Task ExportPositionsAsync_WritesUtf8WithoutTheBom()
    {
        await WithTempFileAsync(async path =>
        {
            await Service.ExportPositionsAsync(path, []);

            // The encoding discipline rides the shared writer: the first
            // bytes must NOT spell the UTF-8 byte order mark.
            var bytes = (await File.ReadAllBytesAsync(path)).Should().HaveCountGreaterThanOrEqualTo(3).And.Subject.ToArray();
            (bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                .Should().BeFalse("the file must not start with the UTF-8 BOM");
        });
    }

    #endregion Positions (T-210)

    #region Realized Trades (T-210)

    [Fact]
    public async Task ExportRealizedTradesAsync_WritesTheGoldenHeaderAndRow()
    {
        await WithTempFileAsync(async path =>
        {
            var trade = new Export.StatementRealizedTrade(
                ClosedAt: BaseTime.AddMinutes(3),
                ExchangeId: "paper",
                Symbol: "BTC/USDT",
                ClosingSide: "Sell",
                ClosingOrderId: "o-2",
                Quantity: 0.02m,
                EntryPrice: 50_100m,
                ExitPrice: 50_300m,
                RealizedPnl: 4m,
                QuoteCurrency: "USDT",
                Note: null,
                Tags: null);

            await Service.ExportRealizedTradesAsync(path, [trade]);

            (await File.ReadAllTextAsync(path)).Should().Be(
                "closedAt,exchange,symbol,side,quantity,entryPrice,exitPrice,realizedPnl,quoteCurrency\r\n" +
                "2026-01-01T12:03:00.000Z,paper,BTC/USDT,Sell,0.02,50100,50300,4,USDT\r\n");
        });
    }

    [Fact]
    public async Task ExportRealizedTradesAsync_QuotelessSymbol_LeavesTheCellEmpty()
    {
        await WithTempFileAsync(async path =>
        {
            var trade = new Export.StatementRealizedTrade(
                ClosedAt: BaseTime,
                ExchangeId: "paper",
                Symbol: "AAPL",
                ClosingSide: "Buy",
                ClosingOrderId: "o-1",
                Quantity: 10m,
                EntryPrice: 190m,
                ExitPrice: 185m,
                RealizedPnl: -50m,
                QuoteCurrency: null,
                Note: null,
                Tags: null);

            await Service.ExportRealizedTradesAsync(path, [trade]);

            var lines = (await File.ReadAllLinesAsync(path)).Should().HaveCount(2).And.Subject.ToArray();
            lines[1].Should().EndWith(",-50,");
        });
    }

    [Fact]
    public async Task ExportRealizedTradesAsync_EmptySet_ProducesAHeaderOnlyFile()
    {
        await WithTempFileAsync(async path =>
        {
            await Service.ExportRealizedTradesAsync(path, []);

            (await File.ReadAllTextAsync(path)).Should().Be(
                "closedAt,exchange,symbol,side,quantity,entryPrice,exitPrice,realizedPnl,quoteCurrency\r\n");
        });
    }

    #endregion Realized Trades (T-210)

    #region Realized Trades Annotations (T-211)

    [Fact]
    public async Task ExportRealizedTradesAsync_AnnotatedRow_GrowsTheMetaTailOverTheWholeFile()
    {
        // The T-211 columns join the document ONLY when at least one row
        // carries an annotation; the whole file keeps one column shape, and
        // the unannotated rows carry honest empty cells.
        await WithTempFileAsync(async path =>
        {
            var annotated = new Export.StatementRealizedTrade(
                ClosedAt: BaseTime.AddMinutes(1),
                ExchangeId: "paper",
                Symbol: "BTC/USDT",
                ClosingSide: "Sell",
                ClosingOrderId: "o-2",
                Quantity: 1m,
                EntryPrice: 10m,
                ExitPrice: 12m,
                RealizedPnl: 2m,
                QuoteCurrency: "USDT",
                Note: "scaled out at the level",
                Tags: "swing,scalp");
            var plain = annotated with
            {
                ClosedAt = BaseTime.AddMinutes(3),
                ClosingOrderId = "o-4",
                Quantity = 1m,
                EntryPrice = 10m,
                ExitPrice = 9m,
                RealizedPnl = -1m,
                Note = null,
                Tags = null,
            };

            await Service.ExportRealizedTradesAsync(path, [annotated, plain]);

            (await File.ReadAllTextAsync(path)).Should().Be(
                "closedAt,exchange,symbol,side,quantity,entryPrice,exitPrice,realizedPnl,quoteCurrency,note,tags\r\n" +
                "2026-01-01T12:01:00.000Z,paper,BTC/USDT,Sell,1,10,12,2,USDT,scaled out at the level,\"swing,scalp\"\r\n" +
                "2026-01-01T12:03:00.000Z,paper,BTC/USDT,Sell,1,10,9,-1,USDT,,\r\n");
        });
    }

    [Fact]
    public async Task ExportRealizedTradesAsync_TagsOnlyAnnotation_StillGrowsTheTail()
    {
        await WithTempFileAsync(async path =>
        {
            var trade = new Export.StatementRealizedTrade(
                ClosedAt: BaseTime,
                ExchangeId: "paper",
                Symbol: "BTC/USDT",
                ClosingSide: "Sell",
                ClosingOrderId: "o-2",
                Quantity: 1m,
                EntryPrice: 10m,
                ExitPrice: 11m,
                RealizedPnl: 1m,
                QuoteCurrency: "USDT",
                Note: null,
                Tags: "swing");

            await Service.ExportRealizedTradesAsync(path, [trade]);

            (await File.ReadAllTextAsync(path)).Should().Be(
                "closedAt,exchange,symbol,side,quantity,entryPrice,exitPrice,realizedPnl,quoteCurrency,note,tags\r\n" +
                "2026-01-01T12:00:00.000Z,paper,BTC/USDT,Sell,1,10,11,1,USDT,,swing\r\n");
        });
    }

    #endregion Realized Trades Annotations (T-211)

    #region Balances (T-210)

    [Fact]
    public async Task ExportBalancesAsync_WritesTheSnapshotInAssetOrder()
    {
        // The dictionary's insertion order is not a file contract: the
        // asset-ordered snapshot stays stable whatever the caller's order.
        await WithTempFileAsync(async path =>
        {
            var balances = new Dictionary<string, Money>
            {
                ["USDT"] = new(1_000m, "USDT"),
                ["BTC"] = new(0.5m, "BTC"),
            };

            await Service.ExportBalancesAsync(path, balances);

            (await File.ReadAllTextAsync(path)).Should().Be(
                "asset,amount\r\n" +
                "BTC,0.5\r\n" +
                "USDT,1000\r\n");
        });
    }

    [Fact]
    public async Task ExportBalancesAsync_EmptySet_ProducesAHeaderOnlyFile()
    {
        await WithTempFileAsync(async path =>
        {
            await Service.ExportBalancesAsync(path, new Dictionary<string, Money>());

            (await File.ReadAllTextAsync(path)).Should().Be("asset,amount\r\n");
        });
    }

    #endregion Balances (T-210)

    #region Format

    [Fact]
    public async Task ExportOrdersAsync_WritesUtf8WithoutTheBom()
    {
        await WithTempFileAsync(async path =>
        {
            var order = BuildOrder(
                id: "paper-1",
                status: OrderStatus.Submitted,
                createdAt: BaseTime,
                updatedAt: null,
                filledQuantity: 0m);

            await Service.ExportOrdersAsync(path, [order]);

            // The first bytes must NOT spell the UTF-8 byte order mark:
            // tool parsers stay byte-clean (RFC-following consumers never
            // want a BOM).
            var bytes = (await File.ReadAllBytesAsync(path)).Should().HaveCountGreaterThanOrEqualTo(3).And.Subject.ToArray();
            (bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                .Should().BeFalse("the file must not start with the UTF-8 BOM");
        });
    }

    #endregion Format

    #region Helpers

    private static async Task WithTempFileAsync(Func<string, Task> testBody)
    {
        var file = Path.Combine(
            Path.GetTempPath(),
            $"zettatrade-csv-{Guid.NewGuid():N}.csv");
        try
        {
            await testBody(file);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static Order BuildOrder(
        string id,
        OrderStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset? updatedAt,
        decimal filledQuantity,
        decimal price = 50_100m,
        decimal quantity = 0.02m,
        decimal? commission = null) => new()
        {
            Id = id,
            ExchangeId = "paper",
            Symbol = Symbol.Parse("BTC/USDT"),
            Side = OrderSide.Sell,
            Type = OrderType.Market,
            Quantity = new Quantity(quantity),
            Price = new Price(price),
            Status = status,
            TimeInForce = TimeInForce.GTC,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            FilledQuantity = new Quantity(filledQuantity),
            Commission = commission,
        };

    /// <summary>
    /// An item list that cancels the given token once the given number of
    /// items was read: the writer's between-rows cancellation point fires
    /// mid-enumeration, like a real cancel racing a long export.
    /// </summary>
    private sealed class CancelAfterNList<T>(
        IReadOnlyList<T> items,
        int cancelAfter,
        CancellationTokenSource cts) : IReadOnlyList<T>
    {
        public int Count => items.Count;

        public IEnumerator<T> GetEnumerator()
        {
            for (var i = 0; i < items.Count; i++)
            {
                // Fires as the (cancelAfter+1)-th enumeration step begins:
                // one row is already behind (whole on disk), the next writer
                // iteration sees the cancelled token at its between-rows
                // point, before that row is ever written.
                if (i > cancelAfter - 1)
                {
                    cts.Cancel();
                }

                yield return items[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public T this[int index] => throw new NotSupportedException("The export enumerates the list.");
    }

    /// <summary>Swaps the process culture for the scope's duration (restores it after).</summary>
    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _previous = CultureInfo.CurrentCulture;
        private readonly CultureInfo _previousUi = CultureInfo.CurrentUICulture;

        public CultureScope(string name)
        {
            var culture = new CultureInfo(name);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _previous;
            CultureInfo.CurrentUICulture = _previousUi;
        }
    }

    #endregion Helpers

    #region Mini Parser

    /// <summary>
    /// A minimal RFC 4180 parser for the goldens: fields split on commas
    /// outside quotes; a quoted field's embedded quotes double; CRLF, LF
    /// and CR all end a record outside quotes. Quote-started fields that
    /// are not closed make the parse throw — a malformed export is caught
    /// by the re-parse itself.
    /// </summary>
    private static string[][] ParseCsv(string content)
    {
        var records = new List<string[]>();
        var fields = new List<string>();
        var field = new System.Text.StringBuilder();
        var inQuotes = false;
        var pending = false; // A field was seen after the last separator.

        void FinishField()
        {
            fields.Add(field.ToString());
            field.Clear();
            pending = false;
        }

        void FinishRecord()
        {
            if (inQuotes)
            {
                throw new InvalidOperationException("The CSV carries a field never closed by a quote.");
            }

            if (pending || fields.Count > 0 || field.Length > 0)
            {
                FinishField();
            }

            if (fields.Count > 0)
            {
                records.Add([.. fields]);
                fields.Clear();
            }
        }

        for (var i = 0; i < content.Length; i++)
        {
            var ch = content[i];
            switch (ch)
            {
                case '"' when inQuotes && i + 1 < content.Length && content[i + 1] == '"':
                    field.Append('"');
                    i++;
                    break;
                case '"':
                    inQuotes = !inQuotes;
                    pending = true;
                    break;
                case ',' when !inQuotes:
                    FinishField();
                    pending = true;
                    break;
                case '\r' when !inQuotes && i + 1 < content.Length && content[i + 1] == '\n':
                    i++;
                    FinishRecord();
                    break;
                case '\r' when !inQuotes:
                    FinishRecord();
                    break;
                default:
                    field.Append(ch);
                    pending = true;
                    break;
            }
        }

        FinishRecord();
        return [.. records];
    }

    #endregion Mini Parser
}
