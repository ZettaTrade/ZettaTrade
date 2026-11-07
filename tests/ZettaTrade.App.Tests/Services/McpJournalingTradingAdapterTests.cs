namespace ZettaTrade.App.Tests.Services;

using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Exchange;
using ZettaTrade.Infrastructure.Fees;
using ZettaTrade.Infrastructure.PnL;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// Tests for the MCP trading lane's journaling wrapper: a tool-placed
/// placement rides the exact journal pair the dialog writes (the session
/// journal plus the persistent ledger, with the T-080 commission stamped
/// on the fill), a tool-cancel marks the row cancelled under the wrapped
/// adapter's exchange identity, a persistence fault never turns an
/// accepted trade action into a tool error, mixed UI+MCP placements keep
/// the session journal's newest-first ordering, and a persisted MCP fill
/// is accounted identically by a PnL engine restart (the ledger replay
/// ingests each fill exactly once, commission included).
/// </summary>
public sealed class McpJournalingTradingAdapterTests
{
    #region Constants

    /// <summary>The reference close the substitute market data publishes.</summary>
    private const decimal ReferenceClose = 50_000m;

    /// <summary>The market order quantity the goldens trade.</summary>
    private const decimal OrderQuantity = 0.01m;

    #endregion Constants

    #region Tests

    [Fact]
    public async Task McpPlacement_RecordsTheSameJournalPairTheDialogWrites()
    {
        // Arrange
        var (wrapper, journal, store, _) = CreateHarness();

        // Act
        var order = await wrapper.PlaceOrderAsync(MarketOrder());

        // Assert
        var recent = journal.GetRecent();
        recent.Should().ContainSingle().Which.Should().BeSameAs(order);
        await store.Received(1).RecordPlacedAsync(order, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task McpPlacement_KeepsTheCommissionParity()
    {
        // Arrange
        var (wrapper, journal, store, _) = CreateHarness();

        // Act
        var order = await wrapper.PlaceOrderAsync(MarketOrder());

        // Assert: the paper fill stamps its computed commission (T-080), and
        // the wrapper records the order as-is - the fee data reaches every
        // journal-backed surface exactly as a UI-placed fill carries it.
        order.Commission.Should().NotBeNull();
        order.Commission.Should().BeGreaterThan(0m);
        order.ExchangeId.Should().Be(PaperTradingAdapter.ExchangeId);
        var recent = journal.GetRecent();
        recent.Should().ContainSingle().Which.Commission.Should().NotBeNull();
    }

    [Fact]
    public async Task McpCancel_MarksTheRowCancelledWithTheWrappedAdapterId()
    {
        // Arrange: a bare inner adapter is enough - the wrapper only commands
        // it and then marks the row under its exchange identity.
        var inner = Substitute.For<ITradingAdapter>();
        inner.Id.Returns(PaperTradingAdapter.ExchangeId);
        inner.CancelOrderAsync(Arg.Any<Symbol>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var journal = new OrderJournal();
        var store = Substitute.For<IOrderHistoryStore>();
        var wrapper = new McpJournalingTradingAdapter(
            inner,
            journal,
            store,
            NullLogger<McpJournalingTradingAdapter>.Instance);

        // Act
        await wrapper.CancelOrderAsync(BtcUsdt(), "paper-7");

        // Assert
        await inner.Received(1).CancelOrderAsync(
            Arg.Any<Symbol>(),
            "paper-7",
            Arg.Any<CancellationToken>());
        await store.Received(1).MarkCancelledAsync(
            PaperTradingAdapter.ExchangeId,
            "paper-7",
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
        journal.GetRecent().Should().BeEmpty("a cancellation records no placement");
    }

    [Fact]
    public async Task McpPlacement_PersistenceFault_NeverTurnsTheAcceptanceIntoAnError()
    {
        // Arrange
        var store = Substitute.For<IOrderHistoryStore>();
        store
            .When(s => s.RecordPlacedAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>()))
            .Throw(new InvalidOperationException("the ledger write fails"));
        var (wrapper, journal, _, _) = CreateHarness(store: store);

        // Act
        var order = await wrapper.PlaceOrderAsync(MarketOrder());

        // Assert: the adapter already accepted the order, so the journal
        // fault must not surface as a tool error; the session list still
        // carries the placement.
        order.Should().NotBeNull();
        order.Status.Should().Be(OrderStatus.Filled);
        journal.GetRecent().Should().ContainSingle().Which.Id.Should().Be(order.Id);
    }

    [Fact]
    public async Task MixedUiAndMcpPlacements_KeepTheNewestFirstOrdering()
    {
        // Arrange: the dialog lane writes its own placement first.
        var journal = new OrderJournal();
        var dialogOrder = MakeOrder("ui-1", OrderStatus.Filled);
        journal.RecordPlaced(dialogOrder);
        var (wrapper, _, _, _) = CreateHarness(journal);

        // Act
        var mcpOrder = await wrapper.PlaceOrderAsync(MarketOrder());

        // Assert: the journal serves newest first, independent of the lane
        // that recorded the placement.
        journal.GetRecent()
            .Select(o => o.Id)
            .Should()
            .Equal(mcpOrder.Id, "ui-1");
    }

    [Fact]
    public async Task McpFill_IsAccountedAndDeterministicAcrossRestarts()
    {
        // Arrange: a real paper lane over a real SQLite ledger, a real PnL
        // engine over its own SQLite profile - the acceptance goldens.
        var directory = Path.Combine(
            Path.GetTempPath(),
            "zt-mcp-journal-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var ledgerFile = Path.Combine(directory, "orders.db");
            var pnlFile = Path.Combine(directory, "pnl.db");
            var store = new SqliteOrderHistoryStore(
                new DbContextOptionsBuilder<OrderHistoryDbContext>()
                    .UseSqlite($"Data Source={ledgerFile};Pooling=false")
                    .Options,
                NullLogger<SqliteOrderHistoryStore>.Instance);
            await store.InitializeAsync();
            var (wrapper, _, _, _) = CreateHarness(store: store);

            // Act: one MCP market order - the fill stamps its commission and
            // the wrapper persists the row exactly as the dialog lane does.
            var order = await wrapper.PlaceOrderAsync(MarketOrder());
            order.Commission.Should().NotBeNull();

            var pnlOptions = new DbContextOptionsBuilder<PnlDbContext>()
                .UseSqlite($"Data Source={pnlFile};Pooling=false")
                .Options;
            var firstBoot = new PnlAccountingService(
                store,
                pnlOptions,
                NullLogger<PnlAccountingService>.Instance);
            await firstBoot.InitializeAsync();
            var firstSnapshot = await firstBoot.StateChanges.FirstAsync();

            // Assert: the realized account shows the commission as a
            // realized cost on the (exchange, symbol) account.
            var realized1 = AssertAccountRealized(firstSnapshot, order.Commission!.Value);

            // The "restart": a fresh engine over the SAME ledger files
            // replays every fill exactly once - the state is reproducible,
            // the commission is accounted exactly once.
            var secondBoot = new PnlAccountingService(
                store,
                pnlOptions,
                NullLogger<PnlAccountingService>.Instance);
            await secondBoot.InitializeAsync();
            var secondSnapshot = await secondBoot.StateChanges.FirstAsync();
            secondSnapshot.Positions.Should().ContainSingle(p =>
                p.ExchangeId == PaperTradingAdapter.ExchangeId
                && p.Symbol == BtcUsdt().Normalized);
            var realized2 = secondSnapshot.Positions.Single(p =>
                p.ExchangeId == PaperTradingAdapter.ExchangeId
                && p.Symbol == BtcUsdt().Normalized);
            realized2.RealizedPnl.Amount.Should().Be(-(order.Commission!.Value));
            realized2.SignedQuantity.Should().Be(realized1.SignedQuantity);
        }
        finally
        {
            TryCleanupDirectory(directory);
        }
    }

    #endregion Tests

    #region Helpers

    private static (McpJournalingTradingAdapter Wrapper, OrderJournal Journal, IOrderHistoryStore Store, ITradingAdapter Inner) CreateHarness(
        OrderJournal? journal = null,
        IOrderHistoryStore? store = null)
    {
        journal ??= new OrderJournal();
        store ??= Substitute.For<IOrderHistoryStore>();
        var inner = new PaperTradingAdapter(
            CreateMarketData(),
            CreateFeeSchedules(),
            NullLogger<PaperTradingAdapter>.Instance);
        var wrapper = new McpJournalingTradingAdapter(
            inner,
            journal,
            store,
            NullLogger<McpJournalingTradingAdapter>.Instance);
        return (wrapper, journal, store, inner);
    }

    /// <summary>
    /// The fee registry the harness builds on: an unconfigured settings
    /// snapshot means the shipped paper schedule answers (10/20 bps), the
    /// same registry the paper adapter's own goldens ride.
    /// </summary>
    private static FeeScheduleService CreateFeeSchedules()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Changes.Returns(Observable.Empty<SettingsChange>());
        return new(settings, NullLogger<FeeScheduleService>.Instance);
    }

    /// <summary>Market data substitute returning one reference H1 bar with the given close.</summary>
    private static IMarketDataAdapter CreateMarketData()
    {
        var marketData = Substitute.For<IMarketDataAdapter>();
        marketData.GetBarsAsync(
                Arg.Any<Symbol>(),
                Arg.Any<TimeFrame>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(new[] { ReferenceBar });
        return marketData;
    }

    private static readonly Bar ReferenceBar =
        new(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero), 49_500m, 50_500m, 49_000m, ReferenceClose, 1_000m);

    private static Symbol BtcUsdt() => Symbol.Parse("BTC/USDT");

    private static OrderRequest MarketOrder() =>
        new()
        {
            Symbol = BtcUsdt(),
            Side = OrderSide.Buy,
            Type = OrderType.Market,
            Quantity = new Quantity(OrderQuantity),
        };

    private static Order MakeOrder(string id, OrderStatus status) =>
        new()
        {
            Id = id,
            ExchangeId = PaperTradingAdapter.ExchangeId,
            Symbol = BtcUsdt(),
            Side = OrderSide.Buy,
            Type = OrderType.Limit,
            Quantity = new Quantity(OrderQuantity),
            Price = new Price(ReferenceClose),
            Status = status,
            TimeInForce = TimeInForce.GTC,
            FilledQuantity = new Quantity(OrderQuantity),
            CreatedAt = new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero),
            Commission = 0.5m,
        };

    private static PnlPositionSnapshot AssertAccountRealized(PnlSnapshot snapshot, decimal expectedCommission)
    {
        var account = snapshot.Positions.Should()
            .ContainSingle(p => p.ExchangeId == PaperTradingAdapter.ExchangeId && p.Symbol == BtcUsdt().Normalized)
            .Subject;
        account.RealizedPnl.Amount.Should().Be(-expectedCommission);
        account.SignedQuantity.Should().Be(OrderQuantity);
        account.AverageEntryPrice.Should().Be(ReferenceClose);
        return account;
    }

    private static void TryCleanupDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: the test artifacts never outlive the temp root.
        }
    }

    #endregion Helpers
}
