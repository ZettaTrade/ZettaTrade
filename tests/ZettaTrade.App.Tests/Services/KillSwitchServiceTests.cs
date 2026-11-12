namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Livery.Localization;
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
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// Tests for the kill switch (T-188): ONE confirmation over the whole
/// plan, the per-item best-effort execution across venues (a fault turns
/// into its own failed report and never stops the rest), the honest
/// nothing-to-do state without a confirmation, the refused confirmation
/// touching nothing, and the fresh-re-read sizing of the reduce-only
/// market exits. The mixed-venue facts ride substitutes; the flatten
/// golden runs against the REAL paper adapter (a seeded position and a
/// resting order, the adapter's own surfaces asserted after).
/// </summary>
public sealed class KillSwitchServiceTests
{
    #region Test Constants

    private static readonly Symbol BtcUsdt = Symbol.Parse("BTC/USDT");
    private static readonly Symbol EthUsdt = Symbol.Parse("ETH/USDT");

    /// <summary>Close of the fake reference bar (the market anchor and the paper fill price).</summary>
    private const decimal ReferenceClose = 50_000m;

    private static readonly DateTimeOffset ReferenceBarTime =
        new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    #endregion Test Constants

    #region Test Fields

    private readonly IConfirmationService _confirmation = Substitute.For<IConfirmationService>();
    private readonly ILocalizationManager _localization = Substitute.For<ILocalizationManager>();
    private readonly IOrderHistoryStore _history = Substitute.For<IOrderHistoryStore>();
    private readonly IOrderJournal _journal = Substitute.For<IOrderJournal>();
    private readonly IRiskManager _risk = Substitute.For<IRiskManager>();

    #endregion Test Fields

    public KillSwitchServiceTests()
    {
        // The fake resolves dynamic messages in a predictable, test-visible
        // form: the key plus the arguments, e.g. "[Trading.KillSwitchNothing]".
        _localization.GetString(Arg.Any<string>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}]");
        _localization.GetString(Arg.Any<string>(), Arg.Any<object[]>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}:{FormatArgs(call.ArgAt<object[]>(1))}]");

        // The plain approved gate for substitute flows.
        _risk.CheckOrderAsync(Arg.Any<OrderRequest>(), Arg.Any<CancellationToken>())
            .Returns(RiskCheckResult.Approved());
    }

    #region The Sweep

    [Fact]
    public async Task ExecuteAsync_MixedVenues_SweepsEverythingBehindOneConfirmation()
    {
        // Arrange: venue A (the paper id) carries two working orders and one
        // long position; venue B (a named live venue) one short position and
        // no orders. The confirmation approves.
        var venueA = CreateVenue(id: PaperTradingAdapter.ExchangeId, name: "Paper Trading Simulator");
        var order1 = BuildOrder(BtcUsdt, "order-1", OrderSide.Buy, OrderType.Limit, 49_900m);
        var order2 = BuildOrder(BtcUsdt, "order-2", OrderSide.Sell, OrderType.Limit, 51_100m);
        var plannedPositionA = BuildPosition(BtcUsdt, OrderSide.Buy, 0.02m);
        var freshPositionA = BuildPosition(BtcUsdt, OrderSide.Buy, 0.02m);
        venueA.GetOpenOrdersAsync().Returns([order1, order2]);
        venueA.GetPositionsAsync().Returns(
            [plannedPositionA],
            [freshPositionA]);
        StubReferenceBars(venueA, BtcUsdt);
        venueA.PlaceOrderAsync(Arg.Any<OrderRequest>(), Arg.Any<CancellationToken>())
            .Returns(BuildOrder(BtcUsdt, "exit-a-1", OrderSide.Sell, OrderType.Market, ReferenceClose));

        var venueB = CreateVenue(id: "bybit", name: "Bybit Testnet");
        var plannedPositionB = BuildPosition(EthUsdt, OrderSide.Sell, 2m);
        var freshPositionB = BuildPosition(EthUsdt, OrderSide.Sell, 2m);
        venueB.GetOpenOrdersAsync().Returns([]);
        venueB.GetPositionsAsync().Returns(
            [plannedPositionB],
            [freshPositionB]);
        StubReferenceBars(venueB, EthUsdt);
        venueB.PlaceOrderAsync(Arg.Any<OrderRequest>(), Arg.Any<CancellationToken>())
            .Returns(BuildOrder(EthUsdt, "exit-b-1", OrderSide.Buy, OrderType.Market, ReferenceClose));
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var service = CreateService(venueA, venueB);

        // Act
        var result = await service.ExecuteAsync();

        // Assert: the sweep ran to its end, the ONE confirmation carried the
        // totals and both venue lines, the orders cancelled through their
        // own symbols and ids, both exits placed as reduce-only markets
        // sized against the fresh reads.
        result.Outcome.Should().Be(KillSwitchOutcome.Done);
        result.IsDeclined.Should().BeFalse();
        await _confirmation.Received(1).RequestConfirmationAsync(
            Arg.Is<ConfirmationRequest>(request => ConfirmNamesThePlan(
                request,
                "2",
                "2",
                "[Positions.BulkTagPaper]",
                "Bybit Testnet")),
            Arg.Any<CancellationToken>());

        await venueA.Received(1).CancelOrderAsync(BtcUsdt, "order-1", Arg.Any<CancellationToken>());
        await venueA.Received(1).CancelOrderAsync(BtcUsdt, "order-2", Arg.Any<CancellationToken>());
        await venueB.DidNotReceive().CancelOrderAsync(Arg.Any<Symbol>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        await venueA.Received(1).PlaceOrderAsync(
            Arg.Is<OrderRequest>(request =>
                request.Symbol.Normalized == BtcUsdt.Normalized
                && request.Side == OrderSide.Sell
                && request.Type == OrderType.Market
                && request.Quantity.Value == 0.02m
                && request.ReduceOnly
                && request.Price != null),
            Arg.Any<CancellationToken>());
        await venueB.Received(1).PlaceOrderAsync(
            Arg.Is<OrderRequest>(request =>
                request.Symbol.Normalized == EthUsdt.Normalized
                && request.Side == OrderSide.Buy
                && request.Type == OrderType.Market
                && request.Quantity.Value == 2m
                && request.ReduceOnly
                && request.Price != null),
            Arg.Any<CancellationToken>());

        result.Reports.Select(report => report.Outcome).Should().Equal(
        [
            KillSwitchItemOutcome.OrderCancelled,
            KillSwitchItemOutcome.OrderCancelled,
            KillSwitchItemOutcome.PositionClosed,
            KillSwitchItemOutcome.PositionClosed,
        ]);

        _journal.Received(2).RecordPlaced(Arg.Any<Order>());
        await _history.Received(2).RecordPlacedAsync(Arg.Any<Order>());
        await _history.Received(2).MarkCancelledAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>());

        result.Summary.Should().Contain("[Trading.KillSwitchVenueDoneFormat:[Positions.BulkTagPaper]|2|1|")
            .And.Contain("[Trading.KillSwitchVenueDoneFormat:Bybit Testnet|0|1|");
    }

    [Fact]
    public async Task ExecuteAsync_NothingAnywhere_ReportsTheNothingWithoutAConfirmation()
    {
        // Arrange: both venues read empty.
        var venueA = CreateVenue(id: PaperTradingAdapter.ExchangeId, name: "Paper Trading Simulator");
        venueA.GetOpenOrdersAsync().Returns([]);
        venueA.GetPositionsAsync().Returns([]);
        var venueB = CreateVenue(id: "bybit", name: "Bybit Testnet");
        venueB.GetOpenOrdersAsync().Returns([]);
        venueB.GetPositionsAsync().Returns([]);
        var service = CreateService(venueA, venueB);

        // Act
        var result = await service.ExecuteAsync();

        // Assert: the explicit localized nothing, no confirmation, no
        // commands, no report items.
        result.Outcome.Should().Be(KillSwitchOutcome.Done);
        result.Reports.Should().BeEmpty();
        result.Summary.Should().StartWith("[Trading.KillSwitchNothing]");
        await _confirmation.DidNotReceive().RequestConfirmationAsync(
            Arg.Any<ConfirmationRequest>(),
            Arg.Any<CancellationToken>());
        await venueA.DidNotReceive().CancelOrderAsync(Arg.Any<Symbol>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await venueB.DidNotReceive().PlaceOrderAsync(Arg.Any<OrderRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RefusedConfirmation_TouchesNothing()
    {
        // Arrange: the venue carries items; the user refuses the sweep.
        var venue = CreateVenue(id: PaperTradingAdapter.ExchangeId, name: "Paper Trading Simulator");
        venue.GetOpenOrdersAsync().Returns([BuildOrder(BtcUsdt, "order-1", OrderSide.Buy, OrderType.Limit, 49_900m)]);
        venue.GetPositionsAsync().Returns(
            [BuildPosition(BtcUsdt, OrderSide.Buy, 0.01m)],
            [BuildPosition(BtcUsdt, OrderSide.Buy, 0.01m)]);
        StubReferenceBars(venue, BtcUsdt);
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var service = CreateService(venue);

        // Act
        var result = await service.ExecuteAsync();

        // Assert: the refusal is the whole action — nothing commanded,
        // nothing reported.
        result.IsDeclined.Should().BeTrue();
        result.Reports.Should().BeEmpty();
        result.Summary.Should().BeNull();
        await venue.DidNotReceive().CancelOrderAsync(Arg.Any<Symbol>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await venue.DidNotReceive().PlaceOrderAsync(Arg.Any<OrderRequest>(), Arg.Any<CancellationToken>());
    }

    #endregion The Sweep

    #region Venue Faults

    [Fact]
    public async Task ExecuteAsync_UnreachableVenue_ReportsItAndSweepsTheRest()
    {
        // Arrange: the paper venue's read faults (an unreachable transport);
        // the bybit venue carries one order and is swept regardless.
        var venueA = CreateVenue(id: PaperTradingAdapter.ExchangeId, name: "Paper Trading Simulator");
        venueA.GetOpenOrdersAsync().Returns(_ => Task.FromException<IReadOnlyList<Order>>(new InvalidOperationException("transport down")));
        var venueB = CreateVenue(id: "bybit", name: "Bybit Testnet");
        var order = BuildOrder(EthUsdt, "order-b", OrderSide.Buy, OrderType.Limit, 2_000m);
        venueB.GetOpenOrdersAsync().Returns([order]);
        venueB.GetPositionsAsync().Returns([]);
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var service = CreateService(venueA, venueB);

        // Act
        var result = await service.ExecuteAsync();

        // Assert: the sweep continued, the faulting venue names itself
        // unreachable with the fault text, the rest ran.
        result.Outcome.Should().Be(KillSwitchOutcome.Done);
        result.Reports.Should().ContainSingle(report =>
            report.VenueName == "Bybit Testnet" && report.OrderId == "order-b");
        result.Summary.Should()
            .Contain("[Trading.KillSwitchVenueUnreachableFormat:[Positions.BulkTagPaper]|transport down]")
            .And.Contain("[Trading.KillSwitchVenueDoneFormat:Bybit Testnet|1|0|");
        await venueB.Received(1).CancelOrderAsync(EthUsdt, "order-b", Arg.Any<CancellationToken>());
    }

    #endregion Venue Faults

    #region Item Faults

    [Fact]
    public async Task ExecuteAsync_CancelFault_FailsTheItemAndContinues()
    {
        // Arrange: two orders; the first cancel faults, the second must run.
        var venue = CreateVenue(id: PaperTradingAdapter.ExchangeId, name: "Paper Trading Simulator");
        var order1 = BuildOrder(BtcUsdt, "order-1", OrderSide.Buy, OrderType.Limit, 49_900m);
        var order2 = BuildOrder(BtcUsdt, "order-2", OrderSide.Sell, OrderType.Limit, 51_100m);
        venue.GetOpenOrdersAsync().Returns([order1, order2]);
        venue.GetPositionsAsync().Returns([]);
        venue.CancelOrderAsync(BtcUsdt, "order-1", Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("venue refused the cancel"));
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var service = CreateService(venue);

        // Act
        var result = await service.ExecuteAsync();

        // Assert: the faulting order reports its own failure with the fault
        // text; the sibling cancelled; the summary carries the failed tail.
        result.Outcome.Should().Be(KillSwitchOutcome.Done);
        result.Reports.Should().Contain(report =>
            report.OrderId == "order-1"
            && report.Outcome == KillSwitchItemOutcome.Failed
            && report.Message == "venue refused the cancel");
        result.Reports.Should().Contain(report =>
            report.OrderId == "order-2" && report.Outcome == KillSwitchItemOutcome.OrderCancelled);
        await venue.Received(1).CancelOrderAsync(BtcUsdt, "order-2", Arg.Any<CancellationToken>());
        result.Summary.Should()
            .Contain("[Trading.KillSwitchFailedItemsFormat:order-1: venue refused the cancel]");
    }

    [Fact]
    public async Task ExecuteAsync_VanishedPosition_ReportsAlreadyFlat()
    {
        // Arrange: the plan read serves the position, the fresh re-read
        // serves none of it (an external fill landed first).
        var venue = CreateVenue(id: PaperTradingAdapter.ExchangeId, name: "Paper Trading Simulator");
        venue.GetOpenOrdersAsync().Returns([]);
        venue.GetPositionsAsync().Returns(
            _ => Task.FromResult<IReadOnlyList<Position>>([BuildPosition(BtcUsdt, OrderSide.Buy, 0.01m)]),
            _ => Task.FromResult<IReadOnlyList<Position>>([]));
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var service = CreateService(venue);

        // Act
        var result = await service.ExecuteAsync();

        // Assert: the goal state is already true, reported honestly — no
        // exit placed.
        result.Outcome.Should().Be(KillSwitchOutcome.Done);
        result.Reports.Should().ContainSingle(report =>
            report.Symbol.Normalized == BtcUsdt.Normalized
            && report.Outcome == KillSwitchItemOutcome.AlreadyFlat
            && report.OrderId == null);
        await venue.DidNotReceive().PlaceOrderAsync(Arg.Any<OrderRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RiskRejection_FailsTheItemWithoutAPlacement()
    {
        // Arrange: the risk gate refuses the exit (an honest reason).
        var venue = CreateVenue(id: PaperTradingAdapter.ExchangeId, name: "Paper Trading Simulator");
        venue.GetOpenOrdersAsync().Returns([]);
        venue.GetPositionsAsync().Returns(
            [BuildPosition(BtcUsdt, OrderSide.Buy, 0.01m)],
            [BuildPosition(BtcUsdt, OrderSide.Buy, 0.01m)]);
        StubReferenceBars(venue, BtcUsdt);
        _risk.CheckOrderAsync(Arg.Any<OrderRequest>(), Arg.Any<CancellationToken>())
            .Returns(RiskCheckResult.Rejected("notional exceeds the hard cap"));
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var service = CreateService(venue);

        // Act
        var result = await service.ExecuteAsync();

        // Assert: the rejected exit never reached the venue; the failure
        // names the gate's reason.
        result.Outcome.Should().Be(KillSwitchOutcome.Done);
        result.Reports.Should().ContainSingle(report =>
            report.Symbol.Normalized == BtcUsdt.Normalized
            && report.Outcome == KillSwitchItemOutcome.Failed
            && report.Message == "notional exceeds the hard cap");
        await venue.DidNotReceive().PlaceOrderAsync(Arg.Any<OrderRequest>(), Arg.Any<CancellationToken>());
        result.Summary.Should().Contain("notional exceeds the hard cap");
    }

    [Fact]
    public async Task ExecuteAsync_PlacementFault_FailsTheItemAndContinues()
    {
        // Arrange: two positions; the first placement faults, the second
        // must run.
        var venue = CreateVenue(id: PaperTradingAdapter.ExchangeId, name: "Paper Trading Simulator");
        venue.GetOpenOrdersAsync().Returns([]);
        venue.GetPositionsAsync().Returns(
            [BuildPosition(BtcUsdt, OrderSide.Buy, 0.01m), BuildPosition(EthUsdt, OrderSide.Sell, 2m)],
            [BuildPosition(BtcUsdt, OrderSide.Buy, 0.01m), BuildPosition(EthUsdt, OrderSide.Sell, 2m)]);
        StubReferenceBars(venue, BtcUsdt);
        StubReferenceBars(venue, EthUsdt);
        venue.PlaceOrderAsync(
                Arg.Is<OrderRequest>(request => request.Symbol.Normalized == BtcUsdt.Normalized),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<Order>(new InvalidOperationException("venue refused the placement")));
        venue.PlaceOrderAsync(
                Arg.Is<OrderRequest>(request => request.Symbol.Normalized == EthUsdt.Normalized),
                Arg.Any<CancellationToken>())
            .Returns(BuildOrder(EthUsdt, "exit-2", OrderSide.Buy, OrderType.Market, ReferenceClose));
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var service = CreateService(venue);

        // Act
        var result = await service.ExecuteAsync();

        // Assert: the faulting close reports its own failure; the sibling
        // closed with its exit order id.
        result.Outcome.Should().Be(KillSwitchOutcome.Done);
        result.Reports.Should().Contain(report =>
            report.Symbol.Normalized == BtcUsdt.Normalized
            && report.Outcome == KillSwitchItemOutcome.Failed
            && report.Message == "venue refused the placement");
        result.Reports.Should().Contain(report =>
            report.Symbol.Normalized == EthUsdt.Normalized
            && report.Outcome == KillSwitchItemOutcome.PositionClosed
            && report.OrderId == "exit-2");
        _journal.Received(1).RecordPlaced(Arg.Any<Order>());
    }

    [Fact]
    public async Task ExecuteAsync_UnsupportedSide_FailsTheItemExplicitly()
    {
        // Arrange: a ride-through side value — the exit side cannot be
        // derived, and no default is ever substituted.
        var venue = CreateVenue(id: PaperTradingAdapter.ExchangeId, name: "Paper Trading Simulator");
        venue.GetOpenOrdersAsync().Returns([]);
        venue.GetPositionsAsync().Returns(
            [BuildPosition(BtcUsdt, (OrderSide)99, 0.01m)],
            [BuildPosition(BtcUsdt, (OrderSide)99, 0.01m)]);
        StubReferenceBars(venue, BtcUsdt);
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var service = CreateService(venue);

        // Act
        var result = await service.ExecuteAsync();

        // Assert
        result.Outcome.Should().Be(KillSwitchOutcome.Done);
        result.Reports.Should().ContainSingle(report =>
            report.Outcome == KillSwitchItemOutcome.Failed
            && report.Message!.Contains("Unsupported position side 99"));
        await venue.DidNotReceive().PlaceOrderAsync(Arg.Any<OrderRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_FreshReadFault_ReportsTheNotAttemptedTail()
    {
        // Arrange: the plan read serves one order and one position; the
        // fresh position re-read faults mid-sweep (the venue's transport
        // died between the cancels and the closes).
        var venue = CreateVenue(id: PaperTradingAdapter.ExchangeId, name: "Paper Trading Simulator");
        var order = BuildOrder(BtcUsdt, "order-1", OrderSide.Buy, OrderType.Limit, 49_900m);
        venue.GetOpenOrdersAsync().Returns([order]);
        venue.GetPositionsAsync().Returns(
            _ => Task.FromResult<IReadOnlyList<Position>>([BuildPosition(BtcUsdt, OrderSide.Buy, 0.01m)]),
            _ => Task.FromException<IReadOnlyList<Position>>(new InvalidOperationException("transport down")));
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var service = CreateService(venue);

        // Act
        var result = await service.ExecuteAsync();

        // Assert: the fresh-read fault is the venue's own honest fault —
        // the order kept its report, the position carries the explicit
        // not-attempted tail, and the sweep still completes (the other
        // venues are never stopped by it).
        result.Outcome.Should().Be(KillSwitchOutcome.Done);
        result.Summary.Should().NotContain("KillSwitchAbortedFormat");
        result.Reports.Should().Contain(report =>
            report.OrderId == "order-1" && report.Outcome == KillSwitchItemOutcome.OrderCancelled);
        result.Reports.Should().Contain(report =>
            report.Symbol.Normalized == BtcUsdt.Normalized
            && report.Outcome == KillSwitchItemOutcome.NotAttempted
            && report.OrderId == null);
        result.Summary.Should().Contain("[Trading.KillSwitchVenueDoneFormat:[Positions.BulkTagPaper]|1|0||0|1|.]");
    }

    #endregion Item Faults

    #region Fresh Sizing

    [Fact]
    public async Task ExecuteAsync_FreshPositionShrunk_SizesByTheFreshRead()
    {
        // Arrange: the plan read confirms 0.02, the fresh re-read serves
        // 0.01 (a fill landed first) — the exit sizes by the fresh facts.
        var venue = CreateVenue(id: PaperTradingAdapter.ExchangeId, name: "Paper Trading Simulator");
        venue.GetOpenOrdersAsync().Returns([]);
        venue.GetPositionsAsync().Returns(
            [BuildPosition(BtcUsdt, OrderSide.Buy, 0.02m)],
            [BuildPosition(BtcUsdt, OrderSide.Buy, 0.01m)]);
        StubReferenceBars(venue, BtcUsdt);
        venue.PlaceOrderAsync(Arg.Any<OrderRequest>(), Arg.Any<CancellationToken>())
            .Returns(BuildOrder(BtcUsdt, "exit-1", OrderSide.Sell, OrderType.Market, ReferenceClose));
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var service = CreateService(venue);

        // Act
        var result = await service.ExecuteAsync();

        // Assert
        result.Outcome.Should().Be(KillSwitchOutcome.Done);
        await venue.Received(1).PlaceOrderAsync(
            Arg.Is<OrderRequest>(request =>
                request.Quantity.Value == 0.01m
                && request.ReduceOnly
                && request.Side == OrderSide.Sell),
            Arg.Any<CancellationToken>());
        result.Reports.Should().ContainSingle(report =>
            report.Outcome == KillSwitchItemOutcome.PositionClosed && report.OrderId == "exit-1");
    }

    #endregion Fresh Sizing

    #region The Real Paper Adapter

    [Fact]
    public async Task ExecuteAsync_TheRealPaperVenue_FlattensTheSeededSurface()
    {
        // Arrange: a real paper adapter with a seeded market long and a
        // resting limit order riding it.
        var marketData = Substitute.For<IMarketDataAdapter>();
        marketData.GetBarsAsync(
                Arg.Any<Symbol>(),
                Arg.Any<TimeFrame>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(ReferenceBars());
        marketData.SubscribeTicker(Arg.Any<Symbol>()).Returns(new Subject<Ticker>());
        var paper = new PaperTradingAdapter(
            marketData,
            PositionActionsServiceTests.CreateFeeSchedules(),
            NullLogger<PaperTradingAdapter>.Instance);
        await paper.PlaceOrderAsync(new OrderRequest
        {
            Symbol = BtcUsdt,
            Side = OrderSide.Buy,
            Type = OrderType.Market,
            Quantity = new Quantity(0.01m),
            Price = new Price(ReferenceClose),
        });
        await paper.PlaceOrderAsync(new OrderRequest
        {
            Symbol = BtcUsdt,
            Side = OrderSide.Sell,
            Type = OrderType.Limit,
            Quantity = new Quantity(0.01m),
            Price = new Price(60_000m),
            ReduceOnly = true,
        });
        (await paper.GetOpenOrdersAsync()).Should().HaveCount(1);
        (await paper.GetPositionsAsync()).Should().HaveCount(1);
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var service = CreateService(paper);

        // Act
        var result = await service.ExecuteAsync();

        // Assert: the surface is flat — no open orders, no open position;
        // the reports name one cancelled order and one placed exit.
        result.Outcome.Should().Be(KillSwitchOutcome.Done);
        (await paper.GetOpenOrdersAsync()).Should().BeEmpty();
        (await paper.GetPositionsAsync()).Should().BeEmpty();
        result.Reports.Should().ContainSingle(report =>
            report.Outcome == KillSwitchItemOutcome.OrderCancelled && report.Symbol.Normalized == BtcUsdt.Normalized);
        result.Reports.Should().ContainSingle(report =>
            report.Outcome == KillSwitchItemOutcome.PositionClosed
            && report.Symbol.Normalized == BtcUsdt.Normalized
            && report.OrderId != null);
    }

    #endregion The Real Paper Adapter

    #region Test Helpers

    private KillSwitchService CreateService(params ITradingAdapter[] venues) => new(
        new KillSwitchVenues(venues),
        _risk,
        _confirmation,
        new OrderConfirmationComposer(_localization),
        _journal,
        _history,
        _localization,
        NullLogger<KillSwitchService>.Instance);

    /// <summary>A trading-venue substitute with its identity facts set.</summary>
    private static ITradingAdapter CreateVenue(string id, string name)
    {
        var venue = Substitute.For<ITradingAdapter>();
        venue.Id.Returns(id);
        venue.Name.Returns(name);
        return venue;
    }

    /// <summary>The reference bars the market anchor resolves from.</summary>
    private static void StubReferenceBars(ITradingAdapter venue, Symbol symbol) => venue.GetBarsAsync(
            Arg.Is<Symbol>(candidate => candidate.Normalized == symbol.Normalized),
            Arg.Any<TimeFrame>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>())
        .Returns(ReferenceBars());

    /// <summary>A working paper-flavored order carrying its own facts.</summary>
    private static Order BuildOrder(
        Symbol symbol,
        string id,
        OrderSide side,
        OrderType type,
        decimal price) => new()
        {
            Id = id,
            ExchangeId = "paper",
            ExchangeOrderId = id,
            Symbol = symbol,
            Side = side,
            Type = type,
            Quantity = new Quantity(0.01m),
            Price = new Price(price),
            TimeInForce = TimeInForce.GTC,
            Status = OrderStatus.Pending,
            FilledQuantity = Quantity.Zero,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    /// <summary>An open position fixture.</summary>
    private static Position BuildPosition(
        Symbol symbol,
        OrderSide side,
        decimal quantity) => new()
        {
            Id = $"pos-{symbol.Normalized}",
            ExchangeId = "paper",
            Symbol = symbol,
            Side = side,
            Quantity = new Quantity(quantity),
            EntryPrice = new Price(ReferenceClose),
            CurrentPrice = new Price(ReferenceClose),
            OpenedAt = DateTimeOffset.UtcNow,
        };

    /// <summary>Two H1 reference bars; the newest by time closes at the reference level.</summary>
    private static IReadOnlyList<Bar> ReferenceBars() =>
    [
        new Bar(ReferenceBarTime.AddHours(-1), ReferenceClose, ReferenceClose, ReferenceClose, ReferenceClose, 1m),
        new Bar(ReferenceBarTime, ReferenceClose, ReferenceClose, ReferenceClose, ReferenceClose, 1m),
    ];

    /// <summary>
    /// The confirmation's shape: the kill-switch template over the totals
    /// and both venues' plan lines (the paper tag localized, the live
    /// venue's own name).
    /// </summary>
    private static bool ConfirmNamesThePlan(
        ConfirmationRequest request,
        string totalOrders,
        string totalPositions,
        string venueATag,
        string venueBName)
    {
        if (request.MessageKey != "Trading.ConfirmKillSwitchFormat")
        {
            return false;
        }

        var arguments = request.Arguments;
        if (arguments.Length != 3)
        {
            return false;
        }

        return arguments[0] as string == totalOrders
            && arguments[1] as string == totalPositions
            && arguments[2] as string is { } listing
            && listing.Contains(venueATag)
            && listing.Contains(venueBName);
    }

    private static string FormatArgs(object[] args) => string.Join("|", args);

    #endregion Test Helpers
}
