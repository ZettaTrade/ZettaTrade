namespace ZettaTrade.App.Tests.Services;

using System;
using System.Globalization;
using System.Linq;
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
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// Tests for the position actions service: close/partial-close/reverse
/// goldens run against the REAL paper adapter (a long seeded through it, the
/// action executed, the netted position state asserted after), every order
/// passes the real risk manager's gates, the reverse is exactly two
/// risk-checked orders behind ONE refused-able confirmation, and every
/// rejection path — no position, unparsable/oversized reduce amount, risk
/// refusal, refused confirmation, a fill that lands first, an unsupported
/// adapter shape — resolves into an explicit localized failure with nothing
/// (or honestly named partial work) on the surface.
/// </summary>
public sealed class PositionActionsServiceTests
{
    #region Test Constants

    private static readonly Symbol BtcUsdt = Symbol.Parse("BTC/USDT");

    /// <summary>Close of the fake reference bar (the market anchor and the paper fill price).</summary>
    private const decimal ReferenceClose = 50_000m;

    /// <summary>Common position size in base asset (a 500 USD notional at the reference close).</summary>
    private const decimal PositionQuantity = 0.01m;

    private static readonly DateTimeOffset ReferenceBarTime =
        new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    #endregion Test Constants

    #region Test Fields

    private readonly IMarketDataAdapter _marketData = Substitute.For<IMarketDataAdapter>();
    private readonly ITradingAdapter _trading = Substitute.For<ITradingAdapter>();
    private readonly IConfirmationService _confirmation = Substitute.For<IConfirmationService>();
    private readonly ILocalizationManager _localization = Substitute.For<ILocalizationManager>();
    private readonly IOrderHistoryStore _history = Substitute.For<IOrderHistoryStore>();
    private readonly IRiskManager _risk = Substitute.For<IRiskManager>();

    #endregion Test Fields

    public PositionActionsServiceTests()
    {
        // The fake resolves dynamic messages in a predictable, test-visible
        // form: the key plus the arguments, e.g. "[Positions.NoPositionFormat:BTC/USDT]".
        _localization.GetString(Arg.Any<string>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}]");
        _localization.GetString(Arg.Any<string>(), Arg.Any<object[]>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}:{FormatArgs(call.ArgAt<object[]>(1))}]");

        // The market reference rate: two H1 bars, the newest by time closing
        // at the reference close (MarketReference picks by bar time). Both
        // faces are stubbed: the REAL paper adapter reads bars through
        // _marketData, while the plain trading substitute IS the market
        // data face the service resolves its reference close against.
        _marketData.GetBarsAsync(
                Arg.Any<Symbol>(),
                Arg.Any<TimeFrame>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(ReferenceBars());
        _trading.GetBarsAsync(
                Arg.Any<Symbol>(),
                Arg.Any<TimeFrame>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(ReferenceBars());

        // The plain closed gate for substitute flows.
        _risk.CheckOrderAsync(Arg.Any<OrderRequest>(), Arg.Any<CancellationToken>())
            .Returns(RiskCheckResult.Approved());
    }

    #region Paper Goldens

    [Fact]
    public async Task CloseAsync_OnPaperPosition_PlacesOneMarketLegAndFlattens()
    {
        // Arrange: a real paper adapter with a seeded 0.01 long.
        var paper = CreatePaperAdapter();
        await SeedLongAsync(paper, PositionQuantity);
        var service = CreateService(paper);
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await service.CloseAsync(BtcUsdt);

        // Assert: one filled reduce-only-shaped market exit, position gone.
        result.IsSuccess.Should().BeTrue();
        result.PlacedOrders.Should().HaveCount(1);
        var leg = result.PlacedOrders.Single();
        leg.Symbol.Normalized.Should().Be(BtcUsdt.Normalized);
        leg.Side.Should().Be(OrderSide.Sell);
        leg.Type.Should().Be(OrderType.Market);
        leg.Quantity.Value.Should().Be(PositionQuantity);
        leg.Status.Should().Be(OrderStatus.Filled);
        leg.Price.Value.Should().Be(ReferenceClose);
        result.ResultMessage.Should().Contain("Positions.ClosedFormat").And.Contain("BTC/USDT");
        (await paper.GetPositionsAsync()).Should().BeEmpty("the close flattens the position");
        (await paper.GetBalancesAsync())["USDT"].Amount.Should()
            .Be(PaperTradingAdapter.InitialQuoteAmount, "the seeded entry is undone at the same price");
    }

    [Fact]
    public async Task ReduceAsync_OnPaperPosition_PlacesOneMarketLegAndShrinksThePosition()
    {
        // Arrange: a real paper adapter with a seeded 0.01 long.
        var paper = CreatePaperAdapter();
        await SeedLongAsync(paper, PositionQuantity);
        var service = CreateService(paper);
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await service.ReduceAsync(BtcUsdt, "0.005");

        // Assert: one market sell of the requested size; the rest stays open.
        result.IsSuccess.Should().BeTrue();
        result.PlacedOrders.Should().HaveCount(1);
        var leg = result.PlacedOrders.Single();
        leg.Side.Should().Be(OrderSide.Sell);
        leg.Quantity.Value.Should().Be(0.005m);
        leg.Status.Should().Be(OrderStatus.Filled);
        result.ResultMessage.Should().Contain("Positions.ReducedFormat").And.Contain("0.005");

        var remaining = (await paper.GetPositionsAsync()).Single();
        remaining.Side.Should().Be(OrderSide.Buy);
        remaining.Quantity.Value.Should().Be(0.005m);
    }

    [Fact]
    public async Task ReverseAsync_OnPaperPosition_PlacesExactlyTwoLegsAndFlipsTheSide()
    {
        // Arrange: a real paper adapter with a seeded 0.01 long.
        var paper = CreatePaperAdapter();
        await SeedLongAsync(paper, PositionQuantity);
        var service = CreateService(paper);
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await service.ReverseAsync(BtcUsdt);

        // Assert: two filled market sells of the position's own size; the
        // paper state flips to a 0.01 short; both orders hit the journal.
        result.IsSuccess.Should().BeTrue();
        result.PlacedOrders.Should().HaveCount(2);
        result.PlacedOrders[0].Side.Should().Be(OrderSide.Sell, "the exit leg sells the long");
        result.PlacedOrders[1].Side.Should().Be(OrderSide.Sell, "the re-entry leg opens the short");
        result.PlacedOrders.Should().OnlyContain(leg => leg.Quantity.Value == PositionQuantity);
        result.PlacedOrders[0].Id.Should().NotBe(result.PlacedOrders[1].Id);
        result.ResultMessage.Should().Contain("Positions.ReversedFormat");

        var flipped = (await paper.GetPositionsAsync()).Single();
        flipped.Side.Should().Be(OrderSide.Sell);
        flipped.Quantity.Value.Should().Be(PositionQuantity);

        // One confirmation over both legs is all the canon demands.
        await _confirmation.Received(1).RequestConfirmationAsync(
            Arg.Any<ConfirmationRequest>(),
            Arg.Any<CancellationToken>());
    }

    #endregion Paper Goldens

    #region Rejection Paths

    [Fact]
    public async Task CloseAsync_WithNoPosition_IsRejectedExplicitlyWithoutPlacement()
    {
        // Arrange: a real paper adapter with nothing opened.
        var paper = CreatePaperAdapter();
        var service = CreateService(paper);

        // Act
        var result = await service.CloseAsync(BtcUsdt);

        // Assert: the explicit close-on-empty rejection, no degenerate order.
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("[Positions.NoPositionFormat:BTC/USDT]");
        result.PlacedOrders.Should().BeEmpty();
    }

    [Fact]
    public async Task CloseAsync_WithZeroQuantityPosition_IsRejectedExplicitly()
    {
        // Arrange: an adapter that serves a zero quantity entry — never a
        // degenerate order is built from it.
        _trading.GetPositionsAsync().Returns([BuildPosition(BtcUsdt, OrderSide.Buy, 0m)]);
        var service = CreateService(_trading);

        // Act
        var result = await service.CloseAsync(BtcUsdt);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("[Positions.NoPositionFormat:BTC/USDT]");
        await _trading.DidNotReceive().PlaceOrderAsync(Arg.Any<OrderRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReduceAsync_WithOversizedAmount_IsRejected()
    {
        // Arrange: a 0.01 long; the reduce asks for 0.02.
        var paper = CreatePaperAdapter();
        await SeedLongAsync(paper, PositionQuantity);
        var service = CreateService(paper);

        // Act
        var result = await service.ReduceAsync(BtcUsdt, "0.02");

        // Assert: explicit rejection, nothing placed, the position intact.
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("[Positions.ReduceTooLargeFormat:0.02|0.01]");
        result.PlacedOrders.Should().BeEmpty();
        var position = (await paper.GetPositionsAsync()).Single();
        position.Quantity.Value.Should().Be(PositionQuantity);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData(" ")]
    public async Task ReduceAsync_WithZeroNegativeUnparsableOrEmptyAmount_IsRejected(string rawInput)
    {
        // Arrange: the parse is the first gate — no adapter read happens.
        _trading.GetPositionsAsync().Returns([BuildPosition(BtcUsdt, OrderSide.Buy, 0.01m)]);
        var service = CreateService(_trading);

        // Act
        var result = await service.ReduceAsync(BtcUsdt, rawInput);

        // Assert: the raw input rides the localized message; nothing placed.
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be($"[Positions.ReduceNotANumberFormat:{rawInput.Trim()}]");
        await _trading.DidNotReceive().PlaceOrderAsync(Arg.Any<OrderRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CloseAsync_WhenRiskControlsReject_PlacesNothing()
    {
        // Arrange: a surrogate trading face serves a 5 BTC long at the
        // reference close — a 250 000 USD exit, over the real risk manager's
        // hard cap. (The real paper ledger cannot host it: its simulated
        // balance stands at 100 000 USDT, so an over-cap seed is impossible
        // there — the gate is exercised against the honest current state.)
        var position = BuildPosition(BtcUsdt, OrderSide.Buy, 5m);
        _trading.GetPositionsAsync()
            .Returns(Task.FromResult<IReadOnlyList<Position>>([position]));
        var service = CreateService(_trading, riskManager: new DefaultRiskManager(NullLogger<DefaultRiskManager>.Instance));

        // Act
        var result = await service.CloseAsync(BtcUsdt);

        // Assert: the localized rejection names the reason; the order never
        // reaches a confirmation or an adapter; the position stands.
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Positions.ActionRejectedFormat").And.Contain("hard cap");
        result.PlacedOrders.Should().BeEmpty();
        (await _trading.GetPositionsAsync()).Single().Quantity.Value.Should().Be(5m);
        await _trading.DidNotReceive().PlaceOrderAsync(Arg.Any<OrderRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReverseAsync_WhenRiskControlsRejectOneLeg_PlacesNothing()
    {
        // Arrange: the entry leg (not reduce-only) is risk-refused while the
        // exit leg (reduce-only) would pass — both risk checks run before
        // any placement, so a refused secondary leg leaves an empty surface.
        _risk.CheckOrderAsync(Arg.Any<OrderRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<OrderRequest>(0).ReduceOnly
                ? RiskCheckResult.Approved()
                : RiskCheckResult.Rejected("entry leg refused"));
        _trading.GetPositionsAsync().Returns([BuildPosition(BtcUsdt, OrderSide.Buy, PositionQuantity)]);
        var service = CreateService(_trading, riskManager: _risk);

        // Act
        var result = await service.ReverseAsync(BtcUsdt);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Positions.ActionRejectedFormat").And.Contain("entry leg refused");
        result.PlacedOrders.Should().BeEmpty();
        await _trading.DidNotReceive().PlaceOrderAsync(Arg.Any<OrderRequest>(), Arg.Any<CancellationToken>());
        await _risk.Received(2).CheckOrderAsync(Arg.Any<OrderRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CloseAsync_WhenConfirmationIsRefused_PlacesNothing()
    {
        // Arrange
        var paper = CreatePaperAdapter();
        await SeedLongAsync(paper, PositionQuantity);
        var service = CreateService(paper);
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(false);

        // Act
        var result = await service.CloseAsync(BtcUsdt);

        // Assert: the declined outcome is neither success nor failure — no
        // report and nothing placed.
        result.Outcome.Should().Be(PositionActionOutcome.Declined);
        result.PlacedOrders.Should().BeEmpty();
        result.ErrorMessage.Should().BeNull();
        (await paper.GetPositionsAsync()).Single().Quantity.Value.Should().Be(PositionQuantity);
    }

    [Fact]
    public async Task ReverseAsync_WhenConfirmationIsRefused_PlacesNothing()
    {
        // Arrange: the mandatory reverse confirmation covers both legs.
        var paper = CreatePaperAdapter();
        await SeedLongAsync(paper, PositionQuantity);
        var service = CreateService(paper);
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(false);

        // Act
        var result = await service.ReverseAsync(BtcUsdt);

        // Assert: nothing placed anywhere, the position intact.
        result.Outcome.Should().Be(PositionActionOutcome.Declined);
        result.PlacedOrders.Should().BeEmpty();
        var position = (await paper.GetPositionsAsync()).Single();
        position.Side.Should().Be(OrderSide.Buy);
        position.Quantity.Value.Should().Be(PositionQuantity);
    }

    [Fact]
    public async Task ReverseAsync_RunsTwoRiskChecksAndPlacesTwoOrders()
    {
        // Arrange: the sizing read and the pre-exit guard see the intact
        // long; the post-exit read sees the symbol flat, so both legs place.
        var placedRequests = new List<OrderRequest>();
        _trading.GetPositionsAsync().Returns(
            _ => Task.FromResult<IReadOnlyList<Position>>([BuildPosition(BtcUsdt, OrderSide.Buy, PositionQuantity)]),
            _ => Task.FromResult<IReadOnlyList<Position>>([BuildPosition(BtcUsdt, OrderSide.Buy, PositionQuantity)]),
            _ => Task.FromResult<IReadOnlyList<Position>>([]));
        _trading.PlaceOrderAsync(
                Arg.Do<OrderRequest>(placedRequests.Add),
                Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(BuildFilledOrder(
                call.ArgAt<OrderRequest>(0),
                $"paper-{placedRequests.Count}")));
        var service = CreateService(_trading, riskManager: _risk);
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await service.ReverseAsync(BtcUsdt);

        // Assert: exactly two risk-manager checks (both legs, before any
        // placement) and exactly two orders — the reduce-only exit first,
        // the plain opposite-side re-entry second, same size.
        await _risk.Received(2).CheckOrderAsync(Arg.Any<OrderRequest>(), Arg.Any<CancellationToken>());
        result.IsSuccess.Should().BeTrue();
        placedRequests.Should().HaveCount(2);
        placedRequests[0].Side.Should().Be(OrderSide.Sell);
        placedRequests[0].ReduceOnly.Should().BeTrue();
        placedRequests[0].Quantity.Value.Should().Be(PositionQuantity);
        placedRequests[1].Side.Should().Be(OrderSide.Sell);
        placedRequests[1].ReduceOnly.Should().BeFalse();
        placedRequests[1].Quantity.Value.Should().Be(PositionQuantity);
        result.PlacedOrders.Select(order => order.Id).Should().Equal("paper-1", "paper-2");
        result.ResultMessage.Should().Contain("Positions.ReversedFormat").And.Contain("paper-1");
    }

    #endregion Rejection Paths

    #region Races And Unsupported Shapes

    [Fact]
    public async Task CloseAsync_WhenThePositionVanishesMidAction_PlacesNothingAndReports()
    {
        // Arrange: the sizing read sees the long; by the pre-placement read
        // an external fill (ignored here) has closed it first.
        _trading.GetPositionsAsync().Returns(
            _ => Task.FromResult<IReadOnlyList<Position>>([BuildPosition(BtcUsdt, OrderSide.Buy, PositionQuantity)]),
            _ => Task.FromResult<IReadOnlyList<Position>>([]));
        var service = CreateService(_trading);
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await service.CloseAsync(BtcUsdt);

        // Assert: the fill that landed first wins; the close resolves into
        // the explicit close-on-empty rejection and nothing is placed.
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("[Positions.NoPositionFormat:BTC/USDT]");
        result.PlacedOrders.Should().BeEmpty();
        await _trading.DidNotReceive().PlaceOrderAsync(Arg.Any<OrderRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CloseAsync_WhenThePositionSizeChangesMidAction_PlacesNothingAndReportsTheRace()
    {
        // Arrange: the sizing read sees 0.01; the pre-placement read sees a
        // partial external fill already applied.
        _trading.GetPositionsAsync().Returns(
            _ => Task.FromResult<IReadOnlyList<Position>>([BuildPosition(BtcUsdt, OrderSide.Buy, PositionQuantity)]),
            _ => Task.FromResult<IReadOnlyList<Position>>([BuildPosition(BtcUsdt, OrderSide.Buy, 0.008m)]));
        var service = CreateService(_trading);
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await service.CloseAsync(BtcUsdt);

        // Assert: the changed state is reported with its fresh shape; nothing
        // placed, nothing silently dropped.
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should()
            .Contain("Positions.RacePositionChangedFormat")
            .And.Contain("[Order.Buy]")
            .And.Contain("0.008")
            .And.Contain("BTC/USDT");
        result.PlacedOrders.Should().BeEmpty();
        await _trading.DidNotReceive().PlaceOrderAsync(Arg.Any<OrderRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReverseAsync_WhenAnExternalFillWinsAfterTheExit_AbortsTheEntryAndReports()
    {
        // Arrange: the exit completes; by the post-exit read the opposite
        // direction is already open (an external fill won the race). The
        // re-entry must NOT double against it.
        var exit = BuildFilledOrder(new OrderRequest
        {
            Symbol = BtcUsdt,
            Side = OrderSide.Sell,
            Type = OrderType.Market,
            Quantity = new Quantity(PositionQuantity),
            Price = new Price(ReferenceClose),
        }, id: "paper-exit");
        _trading.GetPositionsAsync().Returns(
            _ => Task.FromResult<IReadOnlyList<Position>>([BuildPosition(BtcUsdt, OrderSide.Buy, PositionQuantity)]),
            _ => Task.FromResult<IReadOnlyList<Position>>([BuildPosition(BtcUsdt, OrderSide.Buy, PositionQuantity)]),
            _ => Task.FromResult<IReadOnlyList<Position>>([BuildPosition(BtcUsdt, OrderSide.Sell, PositionQuantity)]));
        _trading.PlaceOrderAsync(Arg.Any<OrderRequest>(), Arg.Any<CancellationToken>())
            .Returns(exit);
        var service = CreateService(_trading);
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await service.ReverseAsync(BtcUsdt);

        // Assert: the exit is on the surface and named; the re-entry is not;
        // the report is the honest abort, not a degenerate flip.
        await _trading.Received(1).PlaceOrderAsync(Arg.Any<OrderRequest>(), Arg.Any<CancellationToken>());
        result.IsSuccess.Should().BeFalse();
        result.PlacedOrders.Select(order => order.Id).Should().Equal("paper-exit");
        result.ErrorMessage.Should()
            .Contain("Positions.ReverseAbortedFormat")
            .And.Contain("[Order.Sell]")
            .And.Contain("BTC/USDT");
    }

    [Fact]
    public async Task CloseAsync_WhenReferenceDataIsGone_ReportsTheFailure()
    {
        // Arrange: the real paper adapter; the seeded exit's own market reset
        // carries an explicit fill price and reads no bars, so the close's
        // own reference fetch is the FIRST bar call — and it finds nothing
        // (that's the attempt failing: an explicit error, never a guessing
        // order one degrades into).
        var paper = CreatePaperAdapter();
        _marketData.GetBarsAsync(
                Arg.Any<Symbol>(),
                Arg.Any<TimeFrame>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<Bar>>([]));
        await SeedLongAsync(paper, PositionQuantity);
        var service = CreateService(paper);
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await service.CloseAsync(BtcUsdt);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Positions.ActionFailedFormat").And.Contain("No reference bar available");
        result.PlacedOrders.Should().BeEmpty();
        (await paper.GetPositionsAsync()).Single().Quantity.Value.Should().Be(PositionQuantity);
    }

    [Fact]
    public async Task CloseAsync_WithAnUnsupportedPositionSide_FailsExplicitly()
    {
        // Arrange: a ride-through side value — the exit side cannot be
        // derived, and no default is ever substituted.
        _trading.GetPositionsAsync().Returns([BuildPosition(BtcUsdt, (OrderSide)99, PositionQuantity)]);
        var service = CreateService(_trading);

        // Act
        var result = await service.CloseAsync(BtcUsdt);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should()
            .Contain("Positions.ActionFailedFormat")
            .And.Contain("Unsupported position side 99");
        result.PlacedOrders.Should().BeEmpty();
        await _trading.DidNotReceive().PlaceOrderAsync(Arg.Any<OrderRequest>(), Arg.Any<CancellationToken>());
    }

    #endregion Races And Unsupported Shapes

    #region Test Helpers

    private PaperTradingAdapter CreatePaperAdapter() => new(_marketData, NullLogger<PaperTradingAdapter>.Instance);

    private PositionActionsService CreateService(
        ITradingAdapter trading,
        IRiskManager? riskManager = null,
        IOrderHistoryStore? history = null) => new(
        trading,
        riskManager ?? new DefaultRiskManager(NullLogger<DefaultRiskManager>.Instance),
        _confirmation,
        new OrderConfirmationComposer(_localization),
        new OrderJournal(),
        history ?? _history,
        _localization,
        NullLogger<PositionActionsService>.Instance);

    /// <summary>Seed a market long through the paper adapter itself.</summary>
    private static Task<Order> SeedLongAsync(
        PaperTradingAdapter paper,
        decimal quantity) => paper.PlaceOrderAsync(new OrderRequest
    {
        Symbol = BtcUsdt,
        Side = OrderSide.Buy,
        Type = OrderType.Market,
        Quantity = new Quantity(quantity),
        Price = new Price(ReferenceClose),
    });

    /// <summary>Two H1 reference bars; the newest by time closes at the reference level.</summary>
    private static IReadOnlyList<Bar> ReferenceBars() =>
    [
        new Bar(ReferenceBarTime.AddHours(-1), ReferenceClose, ReferenceClose, ReferenceClose, ReferenceClose, 1m),
        new Bar(ReferenceBarTime, ReferenceClose, ReferenceClose, ReferenceClose, ReferenceClose, 1m),
    ];

    /// <summary>A paper-flavored filled order carrying the request's own facts.</summary>
    private static Order BuildFilledOrder(
        OrderRequest request,
        string id) => new()
    {
        Id = id,
        ExchangeId = "paper",
        ExchangeOrderId = id,
        Symbol = request.Symbol,
        Side = request.Side,
        Type = request.Type,
        Quantity = request.Quantity,
        Price = request.Price ?? Price.Zero,
        Status = OrderStatus.Filled,
        FilledQuantity = request.Quantity,
        TimeInForce = request.TimeInForce,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>An open position fixture on the symbol.</summary>
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

    private static string FormatArgs(object[] args) => string.Join("|", args);

    #endregion Test Helpers
}
