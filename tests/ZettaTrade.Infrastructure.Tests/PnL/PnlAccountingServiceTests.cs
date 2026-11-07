namespace ZettaTrade.Infrastructure.Tests.PnL;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.PnL;

/// <summary>
/// Tests for the PnL accounting core: the average-price goldens (a ladder
/// of buys priced in, then sells out to flat), commission application (a
/// realized cost on the fill it accompanies; absence ≡ zero), the
/// flip-mid-stream path (a closing trade larger than the position opens the
/// remainder on the other side), and the restart determinism acceptance —
/// a fresh engine reloads the same books EXACTLY from the persisted
/// realized history, including the commissions the ledger itself does not
/// carry.
/// </summary>
public sealed class PnlAccountingServiceTests
{
    #region Test Constants

    private static readonly DateTimeOffset BaseTime = new(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);

    #endregion Test Constants

    #region Happy Path

    [Fact]
    public async Task AveragePriceGolden_LadderBuysThenSellsToFlat()
    {
        await WithLedgerAsync(async (_, _, journal, createEngine) =>
        {
            var engine = createEngine();
            await engine.InitializeAsync();

            var open = await engine.IngestFillAsync(Fill("f1", OrderSide.Buy, "BTC/USDT", quantity: 1m, price: 10m), commission: null);
            var extend = await engine.IngestFillAsync(Fill("f2", OrderSide.Buy, "BTC/USDT", quantity: 2m, price: 13m, minute: 1), commission: null);

            open.Should().NotBeNull();
            open!.Effect.Should().Be(PnlFillEffect.Open);
            open.ClosedQuantity.Should().Be(0m);
            open.RealizedPnl.Should().Be(0m);

            extend!.Effect.Should().Be(PnlFillEffect.Open);
            var ladder = Position(engine, "BTC/USDT");
            ladder.SignedQuantity.Should().Be(3m);
            ladder.AverageEntryPrice.Should().Be(12m);
            ladder.RealizedPnl.Amount.Should().Be(0m);
            ladder.QuoteCurrency.Should().Be("USDT");

            var reduce = await engine.IngestFillAsync(Fill("f3", OrderSide.Sell, "BTC/USDT", quantity: 2m, price: 15m, minute: 2), commission: null);
            reduce!.Effect.Should().Be(PnlFillEffect.Reduce);
            reduce.ClosedQuantity.Should().Be(2m);
            reduce.RealizedPnl.Should().Be(6m);

            var halfway = Position(engine, "BTC/USDT");
            halfway.AverageEntryPrice.Should().Be(12m);

            var exit = await engine.IngestFillAsync(Fill("f4", OrderSide.Sell, "BTC/USDT", quantity: 1m, price: 9m, minute: 3), commission: null);
            exit!.Effect.Should().Be(PnlFillEffect.Reduce);
            exit.RealizedPnl.Should().Be(-3m);

            var flat = Position(engine, "BTC/USDT");
            flat.SignedQuantity.Should().Be(0m);
            flat.AverageEntryPrice.Should().Be(0m);
            flat.RealizedPnl.Amount.Should().Be(3m);
            flat.IsFlat.Should().BeTrue();
        });
    }

    [Fact]
    public async Task CommissionApplies_PerFill_AndCountsAsARealizedOpeningCost()
    {
        await WithLedgerAsync(async (_, _, _, createEngine) =>
        {
            var engine = createEngine();
            await engine.InitializeAsync();

            var open = await engine.IngestFillAsync(Fill("f1", OrderSide.Buy, "BTC/USDT", quantity: 2m, price: 10m), commission: 0.5m);
            open!.Effect.Should().Be(PnlFillEffect.Open);
            open.RealizedPnl.Should().Be(-0.5m);

            var firstClose = await engine.IngestFillAsync(Fill("f2", OrderSide.Sell, "BTC/USDT", quantity: 1m, price: 12m, minute: 1), commission: 0.3m);
            firstClose!.Effect.Should().Be(PnlFillEffect.Reduce);
            firstClose.RealizedPnl.Should().Be(1.7m);

            var secondClose = await engine.IngestFillAsync(Fill("f3", OrderSide.Sell, "BTC/USDT", quantity: 1m, price: 12m, minute: 2), commission: 0.3m);
            secondClose!.RealizedPnl.Should().Be(1.7m);

            // The round trip realized 2.0 gross minus both commissions.
            Position(engine, "BTC/USDT").RealizedPnl.Amount.Should().Be(2.9m);
            Position(engine, "BTC/USDT").IsFlat.Should().BeTrue();
        });
    }

    [Fact]
    public async Task CommissionAbsence_IsArithmeticallyZero_NoFeeIsFabricated()
    {
        await WithLedgerAsync(async (_, _, _, createEngine) =>
        {
            var engine = createEngine();
            await engine.InitializeAsync();

            var zero = await engine.IngestFillAsync(Fill("f1", OrderSide.Buy, "ETH/USDT", quantity: 1m, price: 10m), commission: 0m);
            var absent = await engine.IngestFillAsync(Fill("f2", OrderSide.Buy, "ETH/USDT", quantity: 1m, price: 10m, minute: 1), commission: null);

            zero!.RealizedPnl.Should().Be(0m);
            absent!.Effect.Should().Be(PnlFillEffect.Open);
            absent.RealizedPnl.Should().Be(0m);
            Position(engine, "ETH/USDT").AverageEntryPrice.Should().Be(10m);
            Position(engine, "ETH/USDT").RealizedPnl.Amount.Should().Be(0m);
        });
    }

    [Fact]
    public async Task ClosingBeyondThePosition_FlipsMidStream_AtTheSameFillPrice()
    {
        await WithLedgerAsync(async (_, _, _, createEngine) =>
        {
            var engine = createEngine();
            await engine.InitializeAsync();

            await engine.IngestFillAsync(Fill("f1", OrderSide.Buy, "BTC/USDT", quantity: 1m, price: 10m), commission: null);
            var flip = await engine.IngestFillAsync(Fill("f2", OrderSide.Sell, "BTC/USDT", quantity: 3m, price: 12m, minute: 1), commission: null);

            flip!.Effect.Should().Be(PnlFillEffect.Flip);
            flip.ClosedQuantity.Should().Be(1m);
            flip.RealizedPnl.Should().Be(2m);

            var shortLived = Position(engine, "BTC/USDT");
            shortLived.SignedQuantity.Should().Be(-2m);
            shortLived.AverageEntryPrice.Should().Be(12m);
            shortLived.IsFlat.Should().BeFalse();

            var unwind = await engine.IngestFillAsync(Fill("f3", OrderSide.Buy, "BTC/USDT", quantity: 2m, price: 9m, minute: 2), commission: null);
            unwind!.Effect.Should().Be(PnlFillEffect.Reduce);
            unwind.ClosedQuantity.Should().Be(2m);
            unwind.RealizedPnl.Should().Be(6m);

            Position(engine, "BTC/USDT").RealizedPnl.Amount.Should().Be(8m);
            Position(engine, "BTC/USDT").IsFlat.Should().BeTrue();
        });
    }

    [Fact]
    public async Task NonFillRows_StayUnaccounted()
    {
        await WithLedgerAsync(async (_, _, _, createEngine) =>
        {
            var engine = createEngine();
            await engine.InitializeAsync();

            var emissions = new List<PnlSnapshot>();
            using var collector = engine.StateChanges.Subscribe(emissions.Add);
            var baseline = emissions.Count;

            var outcome = await engine.IngestFillAsync(
                Fill("resting", OrderSide.Buy, "BTC/USDT", quantity: 1m, price: 10m, filledQuantity: 0m, status: OrderStatus.Pending),
                commission: null);

            outcome.Should().BeNull();
            emissions.Count.Should().Be(baseline);
        });
    }

    [Fact]
    public async Task MarketPriceDrivesUnrealized_AndIdenticalPricesDoNotReEmit()
    {
        await WithLedgerAsync(async (_, _, _, createEngine) =>
        {
            var engine = createEngine();
            await engine.InitializeAsync();
            await engine.IngestFillAsync(Fill("f1", OrderSide.Buy, "BTC/USDT", quantity: 1m, price: 10m), commission: null);

            var emissions = new List<PnlSnapshot>();
            using var collector = engine.StateChanges.Subscribe(emissions.Add);
            var baseline = emissions.Count;

            await engine.UpdateMarketPriceAsync(Symbol.Parse("BTC/USDT"), 12m);

            emissions.Count.Should().Be(baseline + 1);
            var priced = Position(engine, "BTC/USDT");
            priced.LastPrice.Should().Be(12m);
            priced.UnrealizedPnl.Amount.Should().Be(2m);
            priced.RealizedPnl.Amount.Should().Be(0m);

            await engine.UpdateMarketPriceAsync(Symbol.Parse("BTC/USDT"), 12m);
            emissions.Count.Should().Be(baseline + 1);
        });
    }

    #endregion Happy Path

    #region Persistence

    [Fact]
    public async Task Reload_ReconstructsIdenticalBooksFromThePersistedRealizedHistory()
    {
        await WithLedgerAsync(async (_, pnlFile, journal, createEngine) =>
        {
            // Ledger fills (the journal-driven path, no fee data) plus one
            // directly ingested fee'd fill the ledger does not carry.
            await journal.RecordPlacedAsync(Fill("f1", OrderSide.Buy, "BTC/USDT", quantity: 1m, price: 10m, minute: 1));
            await journal.RecordPlacedAsync(Fill("f2", OrderSide.Buy, "BTC/USDT", quantity: 2m, price: 13m, minute: 2));
            await journal.RecordPlacedAsync(Fill("f3", OrderSide.Buy, "BTC/USDT", quantity: 1m, price: 10m, minute: 3, filledQuantity: 0m, status: OrderStatus.Pending));
            await journal.RecordPlacedAsync(Fill("f4", OrderSide.Sell, "BTC/USDT", quantity: 1m, price: 15m, minute: 4));

            var first = createEngine();
            await first.InitializeAsync();
            await first.IngestFillAsync(Fill("fee", OrderSide.Buy, "ETH/USDT", quantity: 1m, price: 100m, minute: 5), commission: 0.7m);
            var booksWithoutPrice = Snapshot(first);
            await first.UpdateMarketPriceAsync(Symbol.Parse("BTC/USDT"), 14m);
            var booksPriced = Snapshot(first);
            var rows = await RealizedRowCountAsync(pnlFile);
            await first.DisposeAsync();

            // A fresh engine over the same files replays the persisted rows
            // (commissions included), then re-diffs the ledger — nothing
            // double-accounts, and the books come back identical.
            var second = createEngine();
            await second.InitializeAsync();
            var reloaded = Snapshot(second);

            (await RealizedRowCountAsync(pnlFile)).Should()
                .Be(rows, "the reload must not duplicate any accounted fill");
            reloaded.Positions.Should().BeEquivalentTo(booksWithoutPrice.Positions);

            // The live feed re-prices the reloaded books to the same
            // numbers the priced session showed: the unrealized formula is
            // restart-stable too (market prices are live data, they do not
            // persist).
            await second.UpdateMarketPriceAsync(Symbol.Parse("BTC/USDT"), 14m);
            Snapshot(second).Should().BeEquivalentTo(booksPriced);
        });
    }

    [Fact]
    public async Task LedgerFillCommissionPersists_AndPricesTheRealizedBooks()
    {
        await WithLedgerAsync(async (_, pnlFile, journal, createEngine) =>
        {
            // The paper fill path stamps a commission onto the executed
            // order (T-080); the journal row carries it into the ledger,
            // and the accounting core prices it per fill.
            await journal.RecordPlacedAsync(
                Fill("f1", OrderSide.Buy, "BTC/USDT", quantity: 1m, price: 10m, minute: 1, commission: 0.5m));
            await journal.RecordPlacedAsync(
                Fill("f2", OrderSide.Sell, "BTC/USDT", quantity: 1m, price: 12m, minute: 2, commission: 0.3m));

            var engine = createEngine();
            await engine.InitializeAsync();

            // The round trip realizes 2.0 gross minus both commissions.
            Position(engine, "BTC/USDT").RealizedPnl.Amount.Should().Be(1.2m);

            await using var context = new PnlDbContext(
                new DbContextOptionsBuilder<PnlDbContext>()
                    .UseSqlite($"Data Source={pnlFile};Pooling=false")
                    .Options);
            var rows = await context.Realized.OrderBy(row => row.OrderId).ToListAsync();

            rows.Should().HaveCount(2);
            rows.Single(row => row.OrderId == "f1").Commission.Should().Be(0.5m);
            rows.Single(row => row.OrderId == "f2").Commission.Should().Be(0.3m);
        });
    }

    [Fact]
    public async Task SyncWithLedger_IngestsOnce_AndSkipsNonFills()
    {
        await WithLedgerAsync(async (_, pnlFile, journal, createEngine) =>
        {
            await journal.RecordPlacedAsync(Fill("f1", OrderSide.Buy, "BTC/USDT", quantity: 1m, price: 10m, minute: 1));
            await journal.RecordPlacedAsync(Fill("f2", OrderSide.Buy, "BTC/USDT", quantity: 1m, price: 10m, minute: 2, status: OrderStatus.Cancelled));
            await journal.RecordPlacedAsync(Fill("f3", OrderSide.Buy, "BTC/USDT", quantity: 1m, price: 10m, minute: 3, filledQuantity: 0m, status: OrderStatus.Pending));
            await journal.RecordPlacedAsync(Fill("f4", OrderSide.Sell, "BTC/USDT", quantity: 1m, price: 12m, minute: 4));

            var engine = createEngine();
            await engine.InitializeAsync();
            Position(engine, "BTC/USDT").RealizedPnl.Amount.Should().Be(2m);
            var rowsAfterInit = await RealizedRowCountAsync(pnlFile);

            // The re-diff is idempotent: every ledger fill is already
            // accounted, so neither the books nor the table move.
            await engine.SyncWithLedgerAsync();
            await engine.SyncWithLedgerAsync();

            Position(engine, "BTC/USDT").RealizedPnl.Amount.Should().Be(2m);
            (await RealizedRowCountAsync(pnlFile)).Should().Be(rowsAfterInit);
        });
    }

    #endregion Persistence

    #region Helpers

    private static async Task WithLedgerAsync(
        Func<string, string, SqliteOrderHistoryStore, Func<PnlAccountingService>, Task> testBody)
    {
        var journalFile = Path.Combine(
            Path.GetTempPath(),
            $"zettatrade-pnl-journal-{Guid.NewGuid():N}.db");
        var pnlFile = Path.Combine(
            Path.GetTempPath(),
            $"zettatrade-pnl-{Guid.NewGuid():N}.db");
        var journal = new SqliteOrderHistoryStore(
            new DbContextOptionsBuilder<OrderHistoryDbContext>()
                // Pooling=false: each disposed context closes its connection,
                // so the temp files unlock for cleanup.
                .UseSqlite($"Data Source={journalFile};Pooling=false")
                .Options,
            NullLogger<SqliteOrderHistoryStore>.Instance);

        try
        {
            await journal.InitializeAsync();
            await testBody(
                journalFile,
                pnlFile,
                journal,
                () => new PnlAccountingService(
                    journal,
                    new DbContextOptionsBuilder<PnlDbContext>()
                        .UseSqlite($"Data Source={pnlFile};Pooling=false")
                        .Options,
                    NullLogger<PnlAccountingService>.Instance));
        }
        finally
        {
            foreach (var file in new[] { journalFile, pnlFile })
            {
                File.Delete(file);
            }
        }
    }

    private static Order Fill(
        string id,
        OrderSide side,
        string symbol,
        decimal quantity,
        decimal price,
        decimal filledQuantity = -1m,
        OrderStatus status = OrderStatus.Filled,
        string exchange = "paper",
        int minute = 0,
        DateTimeOffset? createdAt = null,
        decimal? commission = null) => new()
        {
            Id = id,
            ExchangeId = exchange,
            Symbol = Symbol.Parse(symbol),
            Side = side,
            Type = OrderType.Market,
            Quantity = new Quantity(quantity),
            Price = new Price(price),
            Status = status,
            TimeInForce = TimeInForce.GTC,
            CreatedAt = createdAt ?? BaseTime.AddMinutes(minute),
            FilledQuantity = filledQuantity < 0m ? new Quantity(quantity) : new Quantity(filledQuantity),
            Commission = commission,
        };

    private static PnlSnapshot Snapshot(PnlAccountingService engine)
    {
        PnlSnapshot? current = null;
        using var collector = engine.StateChanges.Subscribe(snapshot => current = snapshot);
        return current!;
    }

    private static PnlPositionSnapshot Position(PnlAccountingService engine, string symbol)
    {
        var found = Snapshot(engine).Positions.FirstOrDefault(p => p.Symbol == symbol);
        found.Should().NotBeNull($"the {symbol} account must exist");
        return found!;
    }

    private static async Task<int> RealizedRowCountAsync(string pnlFile)
    {
        await using var context = new PnlDbContext(
            new DbContextOptionsBuilder<PnlDbContext>()
                .UseSqlite($"Data Source={pnlFile};Pooling=false")
                .Options);
        return await context.Realized.CountAsync();
    }

    #endregion Helpers
}
