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
/// Tests for the protective-levels service (T-187): the placement goldens run
/// against the REAL paper adapter (a position seeded through it, the pair
/// placed, the adapter's own surfaces asserted after), and every gate path —
/// misdirected levels, a vanished or changed position, a risk refusal, a
/// refused confirmation, a refused second leg, an unsupported side — resolves
/// into an explicit localized failure with nothing (or honestly named
/// partial work) on the surface. The pair is an either-or group of
/// reduce-only opposite-side legs; the rewrite cancels the working pair
/// before the new legs place; the clear cancels only.
/// </summary>
public sealed class ProtectiveLevelsServiceTests
{
    #region Test Constants

    private static readonly Symbol BtcUsdt = Symbol.Parse("BTC/USDT");

    /// <summary>Close of the fake reference bar (the market anchor and the paper fill price).</summary>
    private const decimal ReferenceClose = 50_000m;

    /// <summary>Common position size in base asset.</summary>
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

    public ProtectiveLevelsServiceTests()
    {
        // The fake resolves dynamic messages in a predictable, test-visible
        // form: the key plus the arguments, e.g. "[Positions.NoPositionFormat:BTC/USDT]".
        _localization.GetString(Arg.Any<string>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}]");
        _localization.GetString(Arg.Any<string>(), Arg.Any<object[]>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}:{FormatArgs(call.ArgAt<object[]>(1))}]");

        // The REAL paper adapter reads bars through _marketData (the
        // conditional legs arm against the reference close).
        _marketData.GetBarsAsync(
                Arg.Any<Symbol>(),
                Arg.Any<TimeFrame>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(ReferenceBars());

        // The price stream the conditional legs arm against: a REAL silent
        // subject (the infra battery's precedent) — never a null or a stray
        // auto-substitute, and never a tick this suite does not push.
        _marketData.SubscribeTicker(Arg.Any<Symbol>()).Returns(new Subject<Ticker>());

        // The plain closed gate for substitute flows.
        _risk.CheckOrderAsync(Arg.Any<OrderRequest>(), Arg.Any<CancellationToken>())
            .Returns(RiskCheckResult.Approved());
    }

    #region Paper Goldens

    [Fact]
    public async Task ProtectAsync_OnLong_PlacesTheReduceOnlyPairBehindOneConfirmation()
    {
        // Arrange: a real paper adapter with a seeded 0.01 long.
        var paper = CreatePaperAdapter();
        await SeedLongAsync(paper, PositionQuantity);
        var service = CreateService(paper);
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await service.ProtectAsync(ReadPosition(paper), 49_000m, 51_000m);

        // Assert: the take-profit limit places first, the stop-loss
        // stop-limit second, both reduce-only opposite-side legs of ONE
        // either-or group sized by the position; the venue reads the levels
        // off the position itself.
        result.IsSuccess.Should().BeTrue();
        result.PlacedOrders.Should().HaveCount(2);
        var takeProfit = result.PlacedOrders[0];
        var stopLoss = result.PlacedOrders[1];
        takeProfit.Type.Should().Be(OrderType.Limit);
        takeProfit.Side.Should().Be(OrderSide.Sell);
        takeProfit.Price.Value.Should().Be(51_000m);
        takeProfit.ReduceOnly.Should().BeTrue();
        stopLoss.Type.Should().Be(OrderType.StopLimit);
        stopLoss.Side.Should().Be(OrderSide.Sell);
        stopLoss.StopPrice!.Value.Value.Should().Be(49_000m);
        stopLoss.Price.Value.Should().Be(49_000m, "the stop-limit's limit equals its trigger (the stop-market reading)");
        stopLoss.ReduceOnly.Should().BeTrue();
        takeProfit.OcoGroupId.Should().NotBeNullOrWhiteSpace();
        takeProfit.OcoGroupId.Should().Be(stopLoss.OcoGroupId, "the pair is one either-or group");
        takeProfit.Quantity.Value.Should().Be(PositionQuantity);
        stopLoss.Quantity.Value.Should().Be(PositionQuantity);
        result.ResultMessage.Should()
            .Contain("Positions.ProtectSetFormat")
            .And.Contain("49000.00000000")
            .And.Contain("51000.00000000");

        var position = (await paper.GetPositionsAsync()).Single();
        position.StopLoss!.Value.Value.Should().Be(49_000m);
        position.TakeProfit!.Value.Value.Should().Be(51_000m);
        (await paper.GetOpenOrdersAsync()).Should().HaveCount(2, "both legs stay working");

        // One confirmation over the whole rewrite is all the canon demands.
        await _confirmation.Received(1).RequestConfirmationAsync(
            Arg.Any<ConfirmationRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProtectAsync_OnShort_MirrorsTheDirections()
    {
        // Arrange: a real paper adapter with a seeded 0.01 short.
        var paper = CreatePaperAdapter();
        await SeedShortAsync(paper, PositionQuantity);
        var service = CreateService(paper);
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // Act: the short's stop sits above the market, the take-profit below.
        var result = await service.ProtectAsync(ReadPosition(paper), 51_000m, 49_000m);

        // Assert: both legs buy the exposure back, the stop arms beyond the
        // market, the limit rests below it, and the levels read off the
        // short position.
        result.IsSuccess.Should().BeTrue();
        var takeProfit = result.PlacedOrders[0];
        var stopLoss = result.PlacedOrders[1];
        takeProfit.Side.Should().Be(OrderSide.Buy);
        takeProfit.Price.Value.Should().Be(49_000m);
        stopLoss.Side.Should().Be(OrderSide.Buy);
        stopLoss.StopPrice!.Value.Value.Should().Be(51_000m);
        (await paper.GetOpenOrdersAsync()).Should().HaveCount(2);

        var position = (await paper.GetPositionsAsync()).Single();
        position.Side.Should().Be(OrderSide.Sell);
        position.StopLoss!.Value.Value.Should().Be(51_000m);
        position.TakeProfit!.Value.Value.Should().Be(49_000m);
    }

    [Fact]
    public async Task ProtectAsync_Rewrite_CancelsTheOldPairThenPlacesTheNew()
    {
        // Arrange: a protected long rewrites its levels.
        var paper = CreatePaperAdapter();
        await SeedLongAsync(paper, PositionQuantity);
        var service = CreateService(paper);
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var first = await service.ProtectAsync(ReadPosition(paper), 49_000m, 51_000m);
        first.IsSuccess.Should().BeTrue();

        // Act
        var second = await service.ProtectAsync(ReadPosition(paper), 48_500m, 50_500m);

        // Assert: the old pair left the surface (cancelled), the new pair is
        // the only thing working, the levels never stack, and the cancel
        // commands precede the new placements.
        second.IsSuccess.Should().BeTrue();
        var orders = await paper.GetOpenOrdersAsync();
        orders.Should().HaveCount(2, "the rewrite replaced the pair, never stacked onto it");
        var position = (await paper.GetPositionsAsync()).Single();
        position.StopLoss!.Value.Value.Should().Be(48_500m);
        position.TakeProfit!.Value.Value.Should().Be(50_500m);

        var cancelledIds = first.PlacedOrders.Select(order => order.Id).ToArray();
        orders.Select(order => order.Id).Should().NotIntersectWith(
            cancelledIds,
            "the old legs are terminal; the working set carries only the fresh pair");
    }

    [Fact]
    public async Task ProtectAsync_Clear_CancelsThePairAndTheLevelsVanish()
    {
        // Arrange: a protected long, then the user clears both fields.
        var paper = CreatePaperAdapter();
        await SeedLongAsync(paper, PositionQuantity);
        var service = CreateService(paper);
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(true);
        (await service.ProtectAsync(ReadPosition(paper), 49_000m, 51_000m)).IsSuccess.Should().BeTrue();

        // Act
        var result = await service.ProtectAsync(ReadPosition(paper), null, null);

        // Assert: the protective pair is gone with its levels; the position
        // itself survives, unprotected and honestly read.
        result.IsSuccess.Should().BeTrue();
        result.PlacedOrders.Should().BeEmpty();
        result.ResultMessage.Should().Contain("Positions.ProtectLevelCleared");
        (await paper.GetOpenOrdersAsync()).Should().BeEmpty();
        var position = (await paper.GetPositionsAsync()).Single();
        position.StopLoss.Should().BeNull();
        position.TakeProfit.Should().BeNull();
    }

    [Fact]
    public async Task ProtectAsync_Clear_WithNothingProtected_IsAnExplicitNothing()
    {
        // Arrange: a bare long and an empty dialog.
        var paper = CreatePaperAdapter();
        await SeedLongAsync(paper, PositionQuantity);
        var service = CreateService(paper);

        // Act
        var result = await service.ProtectAsync(ReadPosition(paper), null, null);

        // Assert: the no-op is honest — no confirmation, no cancel, no lie.
        result.IsSuccess.Should().BeTrue();
        result.ResultMessage.Should().Contain("Positions.ProtectNothingToClear");
        await _confirmation.DidNotReceive().RequestConfirmationAsync(
            Arg.Any<ConfirmationRequest>(),
            Arg.Any<CancellationToken>());
        (await paper.GetOpenOrdersAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ProtectAsync_PartialSet_RewritesTheWholePairState()
    {
        // Arrange: a fully protected long, then the user sets the stop only.
        var paper = CreatePaperAdapter();
        await SeedLongAsync(paper, PositionQuantity);
        var service = CreateService(paper);
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(true);
        (await service.ProtectAsync(ReadPosition(paper), 49_000m, 51_000m)).IsSuccess.Should().BeTrue();

        // Act
        var result = await service.ProtectAsync(ReadPosition(paper), 48_500m, null);

        // Assert: the edit's rewrite semantics — the take-profit leg of the
        // old pair cancelled with the rewrite, only the fresh stop works.
        result.IsSuccess.Should().BeTrue();
        var orders = await paper.GetOpenOrdersAsync();
        orders.Should().HaveCount(1);
        orders.Single().Type.Should().Be(OrderType.StopLimit);
        var position = (await paper.GetPositionsAsync()).Single();
        position.StopLoss!.Value.Value.Should().Be(48_500m);
        position.TakeProfit.Should().BeNull("the empty take-profit field cleared that level");
    }

    #endregion Paper Goldens

    #region Direction Refusals

    [Theory]
    [MemberData(nameof(MisdirectedLevels))]
    public async Task ProtectAsync_MisdirectedLevels_AreRejectedExplicitly(
        OrderSide side,
        decimal? stopLoss,
        decimal? takeProfit,
        string expectedKey)
    {
        // Arrange: a level AT the market is as refused as one beyond it —
        // it either fires at once or never arms; both are rejections, never
        // a silent reinterpretation.
        var paper = CreatePaperAdapter();
        _ = side == OrderSide.Buy
            ? await SeedLongAsync(paper, PositionQuantity)
            : await SeedShortAsync(paper, PositionQuantity);
        var service = CreateService(paper);

        // Act
        var result = await service.ProtectAsync(ReadPosition(paper), stopLoss, takeProfit);

        // Assert: the localized direction refusal, and nothing moved — no
        // placement, no confirmation, no cancel.
        result.IsSuccess.Should().BeFalse();
        result.PlacedOrders.Should().BeEmpty();
        result.ErrorMessage.Should().Contain(expectedKey);
        await _confirmation.DidNotReceive().RequestConfirmationAsync(
            Arg.Any<ConfirmationRequest>(),
            Arg.Any<CancellationToken>());
        (await paper.GetOpenOrdersAsync()).Should().BeEmpty();
    }

    #endregion Direction Refusals

    #region Gate Paths

    [Fact]
    public async Task ProtectAsync_WhenThePositionVanishesAtTheFreshRead_PlacesNothing()
    {
        // Arrange: the substitute serves no position at all.
        _trading.GetPositionsAsync().Returns([]);
        var service = CreateService(_trading);

        // Act
        var result = await service.ProtectAsync(BuildPosition(OrderSide.Buy), 49_000m, 51_000m);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("[Positions.NoPositionFormat:BTC/USDT]");
        result.PlacedOrders.Should().BeEmpty();
        await _trading.DidNotReceive().PlaceOrderAsync(Arg.Any<OrderRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProtectAsync_WhenTheQuantityChangesAtTheFreshRead_ReportsTheRace()
    {
        // Arrange: the row rendered 0.01; the adapter now serves 0.008.
        _trading.GetPositionsAsync().Returns([BuildPosition(OrderSide.Buy, 0.008m)]);
        var service = CreateService(_trading);

        // Act
        var result = await service.ProtectAsync(BuildPosition(OrderSide.Buy), 49_000m, 51_000m);

        // Assert: the changed state is reported with its fresh shape.
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should()
            .Contain("Positions.RacePositionChangedFormat")
            .And.Contain("[Order.Buy]")
            .And.Contain("0.008");
        await _trading.DidNotReceive().PlaceOrderAsync(Arg.Any<OrderRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProtectAsync_WhenRiskRejectsOneLeg_NothingMovesAndNothingIsConfirmed()
    {
        // Arrange: the first leg's risk check approves, the second's rejects —
        // the pair is atomic, so nothing is cancelled, placed or confirmed.
        _trading.GetPositionsAsync().Returns(
            _ => Task.FromResult<IReadOnlyList<Position>>([BuildPosition(OrderSide.Buy)]),
            _ => Task.FromResult<IReadOnlyList<Position>>([BuildPosition(OrderSide.Buy)]));
        _trading.GetOpenOrdersAsync().Returns([]);
        _risk.CheckOrderAsync(Arg.Any<OrderRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => Task.FromResult(RiskCheckResult.Approved()),
                _ => Task.FromResult(RiskCheckResult.Rejected("too large")));
        var service = CreateService(_trading, riskManager: _risk);

        // Act
        var result = await service.ProtectAsync(BuildPosition(OrderSide.Buy), 49_000m, 51_000m);

        // Assert
        await _risk.Received(2).CheckOrderAsync(Arg.Any<OrderRequest>(), Arg.Any<CancellationToken>());
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Positions.ActionRejectedFormat").And.Contain("too large");
        await _trading.DidNotReceive().PlaceOrderAsync(Arg.Any<OrderRequest>(), Arg.Any<CancellationToken>());
        await _trading.DidNotReceive().CancelOrderAsync(Arg.Any<Symbol>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _confirmation.DidNotReceive().RequestConfirmationAsync(
            Arg.Any<ConfirmationRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProtectAsync_WhenTheConfirmationIsRefused_NothingMoves()
    {
        // Arrange
        _trading.GetPositionsAsync().Returns(
            _ => Task.FromResult<IReadOnlyList<Position>>([BuildPosition(OrderSide.Buy)]),
            _ => Task.FromResult<IReadOnlyList<Position>>([BuildPosition(OrderSide.Buy)]));
        _trading.GetOpenOrdersAsync().Returns([]);
        var service = CreateService(_trading);
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(false);

        // Act
        var result = await service.ProtectAsync(BuildPosition(OrderSide.Buy), 49_000m, 51_000m);

        // Assert: a refused confirmation leaves the surface untouched.
        result.Outcome.Should().Be(PositionActionOutcome.Declined);
        result.ResultMessage.Should().BeNull();
        result.ErrorMessage.Should().BeNull();
        await _trading.DidNotReceive().PlaceOrderAsync(Arg.Any<OrderRequest>(), Arg.Any<CancellationToken>());
        await _trading.DidNotReceive().CancelOrderAsync(Arg.Any<Symbol>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProtectAsync_WhenThePositionVanishesAfterTheConfirmation_NothingMoves()
    {
        // Arrange: the user confirmed, but an external fill closed the
        // position meanwhile — the pre-mutation guard refuses the rewrite.
        _trading.GetPositionsAsync().Returns(
            _ => Task.FromResult<IReadOnlyList<Position>>([BuildPosition(OrderSide.Buy)]),
            _ => Task.FromResult<IReadOnlyList<Position>>([]));
        _trading.GetOpenOrdersAsync().Returns([]);
        var service = CreateService(_trading);
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await service.ProtectAsync(BuildPosition(OrderSide.Buy), 49_000m, 51_000m);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("[Positions.NoPositionFormat:BTC/USDT]");
        await _trading.DidNotReceive().PlaceOrderAsync(Arg.Any<OrderRequest>(), Arg.Any<CancellationToken>());
        await _trading.DidNotReceive().CancelOrderAsync(Arg.Any<Symbol>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProtectAsync_WhenTheSecondLegFails_CancelsThePlacedLegAndReports()
    {
        // Arrange: the take-profit places; the stop-loss placement is refused
        // by the adapter — the abort path cancels the working take-profit.
        var takeProfit = BuildWorkingOrder(new OrderRequest
        {
            Symbol = BtcUsdt,
            Side = OrderSide.Sell,
            Type = OrderType.Limit,
            Quantity = new Quantity(PositionQuantity),
            Price = new Price(51_000m),
        }, id: "paper-tp");
        _trading.GetPositionsAsync().Returns(
            _ => Task.FromResult<IReadOnlyList<Position>>([BuildPosition(OrderSide.Buy)]),
            _ => Task.FromResult<IReadOnlyList<Position>>([BuildPosition(OrderSide.Buy)]));
        _trading.GetOpenOrdersAsync().Returns([]);
        _trading.PlaceOrderAsync(Arg.Any<OrderRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => Task.FromResult(takeProfit),
                _ => Task.FromException<Order>(new InvalidOperationException("arming refused")));
        var service = CreateService(_trading);
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await service.ProtectAsync(BuildPosition(OrderSide.Buy), 49_000m, 51_000m);

        // Assert: the placed leg is cancelled through its own symbol and id;
        // the report names the abort honestly.
        await _trading.Received(1).CancelOrderAsync(BtcUsdt, "paper-tp", Arg.Any<CancellationToken>());
        result.IsSuccess.Should().BeFalse();
        result.PlacedOrders.Select(order => order.Id).Should().Equal("paper-tp");
        result.ErrorMessage.Should()
            .Contain("Positions.ProtectAbortedFormat")
            .And.Contain("paper-tp")
            .And.Contain("arming refused");
    }

    [Fact]
    public async Task ProtectAsync_WithAnUnsupportedSide_FailsExplicitly()
    {
        // Arrange: a ride-through side value — the protective side cannot be
        // derived, and no default is ever substituted.
        _trading.GetPositionsAsync().Returns([BuildPosition((OrderSide)99)]);
        var service = CreateService(_trading);

        // Act
        var result = await service.ProtectAsync(BuildPosition((OrderSide)99), 49_000m, 51_000m);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should()
            .Contain("Positions.ActionFailedFormat")
            .And.Contain("Unsupported position side 99");
        result.PlacedOrders.Should().BeEmpty();
        await _trading.DidNotReceive().PlaceOrderAsync(Arg.Any<OrderRequest>(), Arg.Any<CancellationToken>());
    }

    #endregion Gate Paths

    /// <summary>
    /// The direction matrix: a level AT the market is as refused as one
    /// beyond it — it either fires at once or never arms; both are
    /// rejections, never a silent reinterpretation.
    /// </summary>
    public static TheoryData<OrderSide, decimal?, decimal?, string> MisdirectedLevels => new()
    {
        { OrderSide.Buy, 50_000m, null, "Positions.ProtectStopMustBeBelowMarket" },
        { OrderSide.Buy, 50_100m, null, "Positions.ProtectStopMustBeBelowMarket" },
        { OrderSide.Buy, null, 50_000m, "Positions.ProtectTakeProfitMustBeAboveMarket" },
        { OrderSide.Buy, null, 49_900m, "Positions.ProtectTakeProfitMustBeAboveMarket" },
        { OrderSide.Sell, 50_000m, null, "Positions.ProtectStopMustBeAboveMarket" },
        { OrderSide.Sell, 49_900m, null, "Positions.ProtectStopMustBeAboveMarket" },
        { OrderSide.Sell, null, 50_000m, "Positions.ProtectTakeProfitMustBeBelowMarket" },
        { OrderSide.Sell, null, 50_100m, "Positions.ProtectTakeProfitMustBeBelowMarket" },
    };

    #region Test Helpers

    private PaperTradingAdapter CreatePaperAdapter() =>
        new(_marketData, PositionActionsServiceTests.CreateFeeSchedules(), NullLogger<PaperTradingAdapter>.Instance);

    private ProtectiveLevelsService CreateService(
        ITradingAdapter trading,
        IRiskManager? riskManager = null) => new(
        trading,
        riskManager ?? new DefaultRiskManager(NullLogger<DefaultRiskManager>.Instance),
        _confirmation,
        new OrderConfirmationComposer(_localization),
        new OrderJournal(),
        _history,
        _localization,
        NullLogger<ProtectiveLevelsService>.Instance);

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

    /// <summary>Seed a market short through the paper adapter itself.</summary>
    private static Task<Order> SeedShortAsync(
        PaperTradingAdapter paper,
        decimal quantity) => paper.PlaceOrderAsync(new OrderRequest
        {
            Symbol = BtcUsdt,
            Side = OrderSide.Sell,
            Type = OrderType.Market,
            Quantity = new Quantity(quantity),
            Price = new Price(ReferenceClose),
        });

    /// <summary>The position as the paper adapter serves it right now.</summary>
    private static Position ReadPosition(PaperTradingAdapter paper) =>
        paper.GetPositionsAsync().GetAwaiter().GetResult().Single();

    /// <summary>Two H1 reference bars; the newest by time closes at the reference level.</summary>
    private static IReadOnlyList<Bar> ReferenceBars() =>
    [
        new Bar(ReferenceBarTime.AddHours(-1), ReferenceClose, ReferenceClose, ReferenceClose, ReferenceClose, 1m),
        new Bar(ReferenceBarTime, ReferenceClose, ReferenceClose, ReferenceClose, ReferenceClose, 1m),
    ];

    /// <summary>A working paper-flavored order carrying the request's own facts.</summary>
    private static Order BuildWorkingOrder(
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
            StopPrice = request.StopPrice,
            OcoGroupId = request.OcoGroupId,
            ReduceOnly = request.ReduceOnly,
            Status = OrderStatus.Pending,
            FilledQuantity = Quantity.Zero,
            TimeInForce = request.TimeInForce,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    /// <summary>An open position fixture on the symbol (the row the user clicked).</summary>
    private static Position BuildPosition(
        OrderSide side,
        decimal quantity = PositionQuantity) => new()
        {
            Id = "pos-BTC/USDT",
            ExchangeId = "paper",
            Symbol = BtcUsdt,
            Side = side,
            Quantity = new Quantity(quantity),
            EntryPrice = new Price(ReferenceClose),
            CurrentPrice = new Price(ReferenceClose),
            OpenedAt = DateTimeOffset.UtcNow,
        };

    private static string FormatArgs(object[] args) => string.Join("|", args);

    #endregion Test Helpers
}
