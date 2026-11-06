namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
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
/// Tests for the order amend pipeline (T-075): every amend re-reads the
/// adapter's fresh state, re-validates the amended shape intrinsically and
/// through the risk manager, asks for a human confirmation on the
/// large-delta rule, and rides the honest replace seam — cancel + new on
/// the shipped paper adapter, in place on replace-capable adapters. The
/// fill race resolves by state: an order that filled or ended before the
/// replace wins, is reported, and never gets a replacement placed alongside
/// it.
/// </summary>
public sealed class OrderAmendServiceTests
{
    #region Test Constants

    /// <summary>Close of the simulated latest reference bar.</summary>
    private const decimal ReferenceClose = 50_000m;

    /// <summary>Buy limit placed below the reference close — it rests as Pending.</summary>
    private const decimal RestingLimitPrice = 49_000m;

    /// <summary>Common order quantity in base asset (9_800 USD at the resting price).</summary>
    private const decimal OriginalQuantity = 0.2m;

    /// <summary>Small shrink that stays far below the large-delta confirmation threshold.</summary>
    private const decimal ShrunkQuantity = 0.19m;

    /// <summary>Limit price whose notional (7_300 USD) sits below the confirmation band (36500 / 49000 − 1 = −25.5%).</summary>
    private const decimal ShrunkLimitPrice = 36_500m;

    /// <summary>Quantity whose amended notional exceeds the DefaultRiskManager hard cap.</summary>
    private const decimal HardCapBreakingQuantity = 15m;

    /// <summary>Fill quantity marking a partially-filled working order.</summary>
    private const decimal PartlyFilledQuantity = 0.05m;

    #endregion

    #region Harness

    private readonly ILocalizationManager _localization = Substitute.For<ILocalizationManager>();
    private readonly IConfirmationService _confirmation = Substitute.For<IConfirmationService>();
    private readonly IRiskManager _riskManager = Substitute.For<IRiskManager>();
    private readonly IOrderHistoryStore _history = Substitute.For<IOrderHistoryStore>();
    private readonly IOrderJournal _journal = new OrderJournal();

    public OrderAmendServiceTests()
    {
        // The fake resolves dynamic messages in a predictable, test-visible
        // form: the key plus the formatted arguments, e.g. "[Positions.AmendDone:paper-1|paper-2]".
        _localization.GetString(Arg.Any<string>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}]");
        _localization.GetString(Arg.Any<string>(), Arg.Any<object[]>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}:{FormatArgs(call.ArgAt<object[]>(1))}]");

        // Default risk posture: approve without demanding an extra
        // confirmation, so the panel-level tests focus one leg at a time.
        _riskManager.CheckOrderAsync(Arg.Any<OrderRequest>())
            .Returns(RiskCheckResult.Approved());
    }

    private OrderAmendService CreateService(ITradingAdapter trading, IRiskManager? riskManager = null) =>
        new(
            trading,
            riskManager ?? _riskManager,
            _confirmation,
            new OrderConfirmationComposer(_localization),
            _journal,
            _history,
            _localization,
            NullLogger<OrderAmendService>.Instance);

    /// <summary>Market data substitute returning one reference H1 bar with the simulated close.</summary>
    private static IMarketDataAdapter StubReferenceClose()
    {
        var marketData = Substitute.For<IMarketDataAdapter>();
        marketData.GetBarsAsync(
                Arg.Any<Symbol>(),
                Arg.Any<TimeFrame>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns([ReferenceBar]);
        return marketData;
    }

    private static PaperTradingAdapter CreatePaperAdapter(IMarketDataAdapter marketData) =>
        new(marketData, NullLogger<PaperTradingAdapter>.Instance);

    private static readonly Bar ReferenceBar =
        new(DateTimeOffset.Parse("2026-11-06T12:00:00+00:00"), 49_500m, 50_500m, 49_000m, ReferenceClose, 1_000m);

    private static Symbol BtcUsdt() => Symbol.Parse("BTC/USDT");

    private static OrderRequest LimitOrder(decimal quantity, decimal price) => new()
    {
        Symbol = BtcUsdt(),
        Side = OrderSide.Buy,
        Type = OrderType.Limit,
        Quantity = new Quantity(quantity),
        Price = new Price(price),
    };

    /// <summary>
    /// A working order entity as the adapter itself would expose it; used
    /// with substitute adapters for the pipeline-gate tests.
    /// </summary>
    private static Order WorkingLimit(
        string id,
        OrderStatus status = OrderStatus.Pending,
        decimal quantity = OriginalQuantity,
        decimal price = RestingLimitPrice) => new()
    {
        Id = id,
        ExchangeId = PaperTradingAdapter.ExchangeId,
        Symbol = BtcUsdt(),
        Side = OrderSide.Buy,
        Type = OrderType.Limit,
        Quantity = new Quantity(quantity),
        Price = new Price(price),
        Status = status,
        TimeInForce = TimeInForce.GTC,
        CreatedAt = DateTimeOffset.Parse("2026-11-06T10:00:00+00:00"),
    };

    /// <summary>The replacement order a substitute adapter hands back on placement.</summary>
    private static Order ReplacementFor(Order original, OrderRequest request) => new()
    {
        Id = original.Id + "-r",
        ExchangeId = original.ExchangeId,
        Symbol = request.Symbol,
        Side = request.Side,
        Type = request.Type,
        Quantity = request.Quantity,
        Price = request.Price ?? throw new InvalidOperationException("the replacement request carries no limit price"),
        StopPrice = request.StopPrice,
        TrailDistance = request.TrailDistance,
        OcoGroupId = request.OcoGroupId,
        TimeInForce = request.TimeInForce,
        Status = OrderStatus.Pending,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    #endregion

    #region Paper Cancel+New Golden

    [Fact]
    public async Task AmendAsync_QuantityOnlyEdit_CancelsOrderAndPlacesTheReplacement()
    {
        // Arrange: a real paper adapter with one resting buy limit; the edit
        // shrinks the quantity below the large-delta threshold, so the amend
        // rides cancel + new without any human confirmation leg.
        var adapter = CreatePaperAdapter(StubReferenceClose());
        var service = CreateService(adapter);
        var original = await adapter.PlaceOrderAsync(
            LimitOrder(OriginalQuantity, RestingLimitPrice));

        var risked = (OrderRequest?)null;
        _riskManager.CheckOrderAsync(Arg.Do<OrderRequest>(r => risked = r))
            .Returns(RiskCheckResult.Approved());

        // Act
        var result = await service.AmendAsync(
            new OrderAmendRequest { Working = original, NewQuantity = ShrunkQuantity });

        // Assert: one new working order at the ORIGINAL price (only the
        // quantity was edited), carrying a fresh identity.
        var open = await adapter.GetOpenOrdersAsync();
        var replacement = open.Should().ContainSingle().Which;
        replacement.Id.Should().NotBe(original.Id, "a paper amend is an honest cancel + new order");
        replacement.Quantity.Value.Should().Be(ShrunkQuantity);
        replacement.Price.Value.Should().Be(RestingLimitPrice);
        replacement.Status.Should().Be(OrderStatus.Pending);

        risked.Should().NotBeNull();
        risked!.Quantity.Value.Should().Be(ShrunkQuantity);
        risked.Price!.Value.Should().Be(new Price(RestingLimitPrice));

        result.Succeeded.Should().BeTrue();
        result.Message.Should().Contain("Positions.AmendDone")
            .And.Contain(original.Id)
            .And.Contain(replacement.Id);

        // The original cancellation entered the persistent history and the
        // replacement landed in the session journal exactly once.
        await _history.Received(1).RecordPlacedAsync(replacement);
        await _history.Received(1).MarkCancelledAsync(
            PaperTradingAdapter.ExchangeId,
            original.Id,
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
        var journalled = _journal.GetRecent();
        journalled.Should().ContainSingle().Which.Id.Should().Be(replacement.Id);

        // The small shrink commits no rewrite of live exposure: no dialog.
        await _confirmation.DidNotReceive().RequestConfirmationAsync(Arg.Any<ConfirmationRequest>());
    }

    [Fact]
    public async Task AmendAsync_LargeShrinkingPriceEdit_ConfirmsThenCancelsAndReplaces()
    {
        // Arrange: re-pricing 49_000 → 36_500 shrinks the notional by 25.5%
        // (beyond the 25% threshold, in the shrinking direction — the delta
        // rule is absolute), while the risk manager itself does not need a
        // dialog: the confirmation fires purely from the large-delta leg.
        var adapter = CreatePaperAdapter(StubReferenceClose());
        var service = CreateService(adapter);
        var original = await adapter.PlaceOrderAsync(
            LimitOrder(OriginalQuantity, RestingLimitPrice));

        var confirmation = (ConfirmationRequest?)null;
        _confirmation.RequestConfirmationAsync(Arg.Do<ConfirmationRequest>(cr => confirmation = cr))
            .Returns(true);

        // Act
        var result = await service.AmendAsync(new OrderAmendRequest
        {
            Working = original,
            NewQuantity = OriginalQuantity,
            NewLimitPrice = ShrunkLimitPrice,
        });

        // Assert: the confirmation names the full delta honestly.
        confirmation.Should().NotBeNull();
        confirmation!.MessageKey.Should().Be("Positions.ConfirmAmend");
        confirmation.Arguments.Should().HaveCount(7);
        confirmation.Arguments[0].Should().Be("[Order.Buy]");
        confirmation.Arguments[1].Should().Be(original.Id);
        confirmation.Arguments[2].Should().Be("0.2");
        confirmation.Arguments[3].Should().Be("0.2");
        confirmation.Arguments[4].Should().Be("49000.00000000");
        confirmation.Arguments[5].Should().Be("36500.00000000");
        confirmation.Arguments[6].Should().Be("BTC/USDT");

        // After the confirmation the order was swapped, not re-priced in place.
        var replacement = (await adapter.GetOpenOrdersAsync()).Should().ContainSingle().Which;
        replacement.Id.Should().NotBe(original.Id);
        replacement.Price.Value.Should().Be(ShrunkLimitPrice);
        replacement.Quantity.Value.Should().Be(OriginalQuantity);
        result.Succeeded.Should().BeTrue();
        result.Message.Should().Contain("Positions.AmendDone")
            .And.Contain(original.Id)
            .And.Contain(replacement.Id);
    }

    [Fact]
    public async Task AmendAsync_LargePriceEdit_DeclinedConfirmation_LeavesTheOrderUntouched()
    {
        // Arrange
        var adapter = CreatePaperAdapter(StubReferenceClose());
        var service = CreateService(adapter);
        var original = await adapter.PlaceOrderAsync(
            LimitOrder(OriginalQuantity, RestingLimitPrice));
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>()).Returns(false);

        // Act
        var result = await service.AmendAsync(new OrderAmendRequest
        {
            Working = original,
            NewQuantity = OriginalQuantity,
            NewLimitPrice = ShrunkLimitPrice,
        });

        // Assert: a declined confirmation is a deliberate stop — nothing was
        // cancelled, nothing was placed, and the panel gets no outcome line.
        result.Succeeded.Should().BeFalse();
        result.Message.Should().BeNull();
        (await adapter.GetOpenOrdersAsync())
            .Should().ContainSingle().Which.Id.Should().Be(original.Id);
        _journal.GetRecent().Should().BeEmpty();
    }

    [Fact]
    public async Task AmendAsync_RiskHardCapBreach_RejectsAndLeavesTheOrderWorking()
    {
        // Arrange: 15 BTC at 49_000 = 735_000 USD, far above the hard cap —
        // the risk gate stops the amend before any confirmation or mutation.
        var adapter = new FakeTradingAdapter();
        var service = CreateService(
            adapter,
            new DefaultRiskManager(NullLogger<DefaultRiskManager>.Instance));
        var original = await adapter.PlaceOrderAsync(
            LimitOrder(OriginalQuantity, RestingLimitPrice));
        adapter.ResetCounters();

        // Act
        var result = await service.AmendAsync(
            new OrderAmendRequest { Working = original, NewQuantity = HardCapBreakingQuantity });

        // Assert
        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("Positions.AmendRejected").And.Contain("hard cap");
        adapter.CancelCount.Should().Be(0);
        adapter.PlaceCount.Should().Be(0);
        (await adapter.GetOpenOrdersAsync()).Should().ContainSingle().Which
            .Quantity.Value.Should().Be(OriginalQuantity);
        await _confirmation.DidNotReceive().RequestConfirmationAsync(Arg.Any<ConfirmationRequest>());
    }

    #endregion

    #region Pipeline Gates

    [Fact]
    public async Task AmendAsync_UnknownOrderId_ReportsStaleWithoutMutation()
    {
        // Arrange: the target is not in the open set anymore.
        var trading = Substitute.For<ITradingAdapter>();
        trading.GetOpenOrdersAsync().Returns([]);
        var service = CreateService(trading);
        var stale = WorkingLimit("paper-7");

        // Act
        var result = await service.AmendAsync(
            new OrderAmendRequest { Working = stale, NewQuantity = ShrunkQuantity });

        // Assert: the display row was stale; the flow reports in the user's
        // language and touches nothing.
        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("Positions.AmendStale").And.Contain("paper-7");
        await trading.DidNotReceive().CancelOrderAsync(
            Arg.Any<Symbol>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await trading.DidNotReceive().PlaceOrderAsync(Arg.Any<OrderRequest>());
        await _riskManager.DidNotReceive().CheckOrderAsync(Arg.Any<OrderRequest>());
    }

    [Theory]
    [InlineData(OrderType.Market)]
    [InlineData(OrderType.StopMarket)]
    public async Task AmendAsync_NonAmendableType_IsRefusedBeforeAnyMutation(OrderType type)
    {
        // Arrange: market and stop-market orders execute at once — there is
        // no working shape left to amend, and the flow refuses loudly.
        var trading = Substitute.For<ITradingAdapter>();
        trading.GetOpenOrdersAsync().Returns(
            [WorkingLimit("paper-7") with { Type = type }]);
        var service = CreateService(trading);
        var working = WorkingLimit("paper-7") with { Type = type };

        // Act
        var result = await service.AmendAsync(
            new OrderAmendRequest { Working = working, NewQuantity = ShrunkQuantity });

        // Assert
        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("Positions.AmendTypeUnsupported")
            .And.Contain(type.ToString());
        await trading.DidNotReceive().CancelOrderAsync(
            Arg.Any<Symbol>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await trading.DidNotReceive().PlaceOrderAsync(Arg.Any<OrderRequest>());
        await _riskManager.DidNotReceive().CheckOrderAsync(Arg.Any<OrderRequest>());
    }

    [Fact]
    public async Task AmendAsync_OcoLeg_IsRefusedBeforeAnyMutation()
    {
        // Arrange: the cancel+new seam cannot hold an either-or group
        // atomically, so OCO legs are refused on purpose.
        var trading = Substitute.For<ITradingAdapter>();
        var working = WorkingLimit("paper-7") with { OcoGroupId = "oco-3" };
        trading.GetOpenOrdersAsync().Returns([working]);
        var service = CreateService(trading);

        // Act
        var result = await service.AmendAsync(
            new OrderAmendRequest { Working = working, NewQuantity = ShrunkQuantity });

        // Assert
        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("Positions.AmendOcoUnsupported").And.Contain("oco-3");
        await trading.DidNotReceive().CancelOrderAsync(
            Arg.Any<Symbol>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await trading.DidNotReceive().PlaceOrderAsync(Arg.Any<OrderRequest>());
        await _riskManager.DidNotReceive().CheckOrderAsync(Arg.Any<OrderRequest>());
    }

    [Fact]
    public async Task AmendAsync_PriceEditOnTrailingStop_IsRefused()
    {
        // Arrange: a trailing stop's displayed price is its ratcheting
        // trigger, not an editable level; a price edit must be refused, not
        // silently dropped (trading safety: never substitute).
        var trading = Substitute.For<ITradingAdapter>();
        var working = WorkingLimit("paper-7") with
        {
            Type = OrderType.TrailingStop,
            StopPrice = new Price(49_500m),
            TrailDistance = new Price(500m),
        };
        trading.GetOpenOrdersAsync().Returns([working]);
        var service = CreateService(trading);

        // Act
        var result = await service.AmendAsync(new OrderAmendRequest
        {
            Working = working,
            NewQuantity = ShrunkQuantity,
            NewLimitPrice = 48_000m,
        });

        // Assert
        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("Positions.AmendInvalid")
            .And.Contain("TrailingStop")
            .And.Contain("price edit");
        await trading.DidNotReceive().CancelOrderAsync(
            Arg.Any<Symbol>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await trading.DidNotReceive().PlaceOrderAsync(Arg.Any<OrderRequest>());
        await _riskManager.DidNotReceive().CheckOrderAsync(Arg.Any<OrderRequest>());
    }

    [Fact]
    public async Task AmendAsync_NoChangeEdit_IsRefused()
    {
        // Arrange: same quantity and no price edit changes nothing; grinding
        // a cancel (or a fill race) for zero effect is refused.
        var trading = Substitute.For<ITradingAdapter>();
        var working = WorkingLimit("paper-7");
        trading.GetOpenOrdersAsync().Returns([working]);
        var service = CreateService(trading);

        // Act
        var result = await service.AmendAsync(
            new OrderAmendRequest { Working = working, NewQuantity = OriginalQuantity });

        // Assert
        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("Positions.AmendNoChange");
        await trading.DidNotReceive().CancelOrderAsync(
            Arg.Any<Symbol>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await trading.DidNotReceive().PlaceOrderAsync(Arg.Any<OrderRequest>());
        await _riskManager.DidNotReceive().CheckOrderAsync(Arg.Any<OrderRequest>());
    }

    [Fact]
    public async Task AmendAsync_QuantityBelowFilled_IsRefused()
    {
        // Arrange: the working order carries 0.05 filled; shrinking below
        // that is unparsable — never silently clamped to the fill.
        var trading = Substitute.For<ITradingAdapter>();
        var working = WorkingLimit("paper-7", OrderStatus.PartiallyFilled)
            with { FilledQuantity = new Quantity(PartlyFilledQuantity) };
        trading.GetOpenOrdersAsync().Returns([working]);
        var service = CreateService(trading);

        // Act
        var result = await service.AmendAsync(
            new OrderAmendRequest { Working = working, NewQuantity = 0.03m });

        // Assert
        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("Positions.AmendQtyBelowFilled")
            .And.Contain("0.03")
            .And.Contain("0.05");
        await trading.DidNotReceive().CancelOrderAsync(
            Arg.Any<Symbol>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await trading.DidNotReceive().PlaceOrderAsync(Arg.Any<OrderRequest>());
    }

    #endregion

    #region Risk And Confirmation Integration

    [Fact]
    public async Task AmendAsync_RiskRejection_ReportsReasonAndNeverMutatesNeverConfirms()
    {
        // Arrange
        var trading = Substitute.For<ITradingAdapter>();
        var working = WorkingLimit("paper-7");
        trading.GetOpenOrdersAsync().Returns([working]);
        _riskManager.CheckOrderAsync(Arg.Any<OrderRequest>())
            .Returns(RiskCheckResult.Rejected("hard cap exceeded"));
        var service = CreateService(trading);

        // Act
        var result = await service.AmendAsync(
            new OrderAmendRequest { Working = working, NewQuantity = ShrunkQuantity });

        // Assert: the localized rejection carries the gate's own reason; the
        // order survives untouched and no dialog was ever opened.
        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("Positions.AmendRejected")
            .And.Contain("hard cap exceeded");
        await trading.DidNotReceive().CancelOrderAsync(
            Arg.Any<Symbol>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await trading.DidNotReceive().PlaceOrderAsync(Arg.Any<OrderRequest>());
        await _confirmation.DidNotReceive().RequestConfirmationAsync(Arg.Any<ConfirmationRequest>());
    }

    [Fact]
    public async Task AmendAsync_RiskDemandsConfirmation_Declined_StopsSilently()
    {
        // Arrange: the risk manager's own threshold demands a dialog; the
        // user declines — nothing placed or cancelled.
        var trading = Substitute.For<ITradingAdapter>();
        var working = WorkingLimit("paper-7");
        trading.GetOpenOrdersAsync().Returns([working]);
        _riskManager.CheckOrderAsync(Arg.Any<OrderRequest>())
            .Returns(RiskCheckResult.Approved(requiresConfirmation: true));
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>()).Returns(false);
        var service = CreateService(trading);

        // Act
        var result = await service.AmendAsync(
            new OrderAmendRequest { Working = working, NewQuantity = ShrunkQuantity });

        // Assert
        result.Succeeded.Should().BeFalse();
        result.Message.Should().BeNull();
        await trading.DidNotReceive().CancelOrderAsync(
            Arg.Any<Symbol>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await trading.DidNotReceive().PlaceOrderAsync(Arg.Any<OrderRequest>());
    }

    [Fact]
    public async Task AmendAsync_RiskDemandsConfirmation_Confirmed_ProceedsAsCancelAndNew()
    {
        // Arrange
        var trading = Substitute.For<ITradingAdapter>();
        var working = WorkingLimit("paper-7");
        trading.GetOpenOrdersAsync().Returns([working]);
        _riskManager.CheckOrderAsync(Arg.Any<OrderRequest>())
            .Returns(RiskCheckResult.Approved(requiresConfirmation: true));
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>()).Returns(true);
        var replacement = ReplacementFor(working, LimitOrder(ShrunkQuantity, RestingLimitPrice));
        trading.PlaceOrderAsync(Arg.Any<OrderRequest>()).Returns(replacement);
        var service = CreateService(trading);

        // Act
        var result = await service.AmendAsync(
            new OrderAmendRequest { Working = working, NewQuantity = ShrunkQuantity });

        // Assert
        result.Succeeded.Should().BeTrue();
        result.Message.Should().Contain("Positions.AmendDone")
            .And.Contain(working.Id)
            .And.Contain(replacement.Id);
        await trading.Received(1).CancelOrderAsync(
            BtcUsdt(), working.Id, Arg.Any<CancellationToken>());
        await trading.Received(1).PlaceOrderAsync(Arg.Any<OrderRequest>());
        await _confirmation.Received(1).RequestConfirmationAsync(Arg.Any<ConfirmationRequest>());
    }

    [Fact]
    public async Task AmendAsync_PartlyFilledOrder_AlwaysAsksForConfirmation()
    {
        // Arrange: a fill-in-progress order's amend rewrites its remaining
        // exposure — the fill leg of the confirmation rule fires even with a
        // tiny delta the notional rule would ignore.
        var trading = Substitute.For<ITradingAdapter>();
        var working = WorkingLimit("paper-7", OrderStatus.PartiallyFilled)
            with { FilledQuantity = new Quantity(PartlyFilledQuantity) };
        trading.GetOpenOrdersAsync().Returns([working]);
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>()).Returns(true);
        var replacement = ReplacementFor(working, LimitOrder(ShrunkQuantity, RestingLimitPrice));
        trading.PlaceOrderAsync(Arg.Any<OrderRequest>()).Returns(replacement);
        var service = CreateService(trading);

        // Act
        var result = await service.AmendAsync(
            new OrderAmendRequest { Working = working, NewQuantity = ShrunkQuantity });

        // Assert: confirmed once, then swapped honestly.
        result.Succeeded.Should().BeTrue();
        result.Message.Should().Contain("Positions.AmendDone");
        await _confirmation.Received(1).RequestConfirmationAsync(Arg.Any<ConfirmationRequest>());
        await trading.Received(1).CancelOrderAsync(
            BtcUsdt(), working.Id, Arg.Any<CancellationToken>());
    }

    #endregion

    #region Fill Race

    [Fact]
    public async Task AmendAsync_FillWinsTheCancelRace_ReportsTerminalStateAndNeverReplaces()
    {
        // Arrange: the fill lands exactly between the replace-time re-read
        // and the cancel — the paper contract refuses the cancel and the
        // terminal state wins, so no replacement may ever be placed.
        var adapter = new FakeTradingAdapter();
        var service = CreateService(adapter);
        var original = await adapter.PlaceOrderAsync(
            LimitOrder(OriginalQuantity, RestingLimitPrice));
        adapter.OnCancel = () => adapter.ForceFill(original.Id);
        adapter.ResetCounters();

        // Act
        var result = await service.AmendAsync(
            new OrderAmendRequest { Working = original, NewQuantity = ShrunkQuantity });

        // Assert: the fill won — reported, and the working set stays empty.
        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("Positions.AmendStale").And.Contain(original.Id);
        (await adapter.GetOpenOrdersAsync()).Should().BeEmpty();
        adapter.PlaceCount.Should().Be(0);
        await _history.DidNotReceive().MarkCancelledAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
        _journal.GetRecent().Should().BeEmpty();
    }

    [Fact]
    public async Task AmendAsync_CancelRefusedForAnotherReason_LeavesTheOrderWorking()
    {
        // Arrange: a fault on the wire during the cancel — the original must
        // remain working (no half-way seam) and the fault is reported.
        var adapter = new FakeTradingAdapter();
        var service = CreateService(adapter);
        var original = await adapter.PlaceOrderAsync(
            LimitOrder(OriginalQuantity, RestingLimitPrice));
        adapter.OnCancel = () => throw new HttpRequestException("wire down");
        adapter.ResetCounters();

        // Act
        var result = await service.AmendAsync(
            new OrderAmendRequest { Working = original, NewQuantity = ShrunkQuantity });

        // Assert
        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("Positions.AmendCancelFailed")
            .And.Contain("wire down");
        (await adapter.GetOpenOrdersAsync()).Should().ContainSingle().Which
            .Id.Should().Be(original.Id);
        adapter.PlaceCount.Should().Be(0);
    }

    [Fact]
    public async Task AmendAsync_PlacementFailsAfterCancel_ReportsTheHoleLoudly()
    {
        // Arrange: the cancel landed, the placement was refused — the panel
        // must never hide the hole, nor invent a working order it does not have.
        var adapter = new FakeTradingAdapter();
        var service = CreateService(adapter);
        var original = await adapter.PlaceOrderAsync(
            LimitOrder(OriginalQuantity, RestingLimitPrice));

        // The failure starts only once the setup order rests — otherwise the
        // original could never have existed for the flow to target.
        adapter.PlacementFailure = "wire down";
        adapter.ResetCounters();

        // Act
        var result = await service.AmendAsync(
            new OrderAmendRequest { Working = original, NewQuantity = ShrunkQuantity });

        // Assert
        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("Positions.AmendReplaceFailed")
            .And.Contain(original.Id)
            .And.Contain("wire down");
        (await adapter.GetOpenOrdersAsync()).Should().BeEmpty();
        adapter.CancelCount.Should().Be(1);
        adapter.PlaceCount.Should().Be(1);
        _journal.GetRecent().Should().BeEmpty();
    }

    #endregion

    #region Replace Seam

    [Fact]
    public async Task AmendAsync_ReplaceCapableAdapter_AmendsInPlaceWithoutACancel()
    {
        // Arrange: an adapter exposing the replace seam performs the amend in
        // place — the order keeps its identity and nothing is cancelled.
        var adapter = new ReplaceableFakeTradingAdapter();
        var service = CreateService(adapter);
        var original = await adapter.PlaceOrderAsync(
            LimitOrder(OriginalQuantity, RestingLimitPrice));
        adapter.ResetCounters();

        // Act
        var result = await service.AmendAsync(
            new OrderAmendRequest { Working = original, NewQuantity = ShrunkQuantity });

        // Assert
        result.Succeeded.Should().BeTrue();
        result.Message.Should().Contain("Positions.AmendDoneInPlace")
            .And.Contain(original.Id);
        var open = await adapter.GetOpenOrdersAsync();
        var replaced = open.Should().ContainSingle().Which;
        replaced.Id.Should().Be(original.Id, "an in-place replace keeps the order's identity");
        replaced.Quantity.Value.Should().Be(ShrunkQuantity);
        replaced.Price.Value.Should().Be(RestingLimitPrice);
        adapter.CancelCount.Should().Be(0);
        adapter.PlaceCount.Should().Be(0);
        await _history.Received(1).RecordPlacedAsync(replaced);
        _journal.GetRecent().Should().ContainSingle().Which.Id.Should().Be(original.Id);
    }

    [Fact]
    public async Task AmendAsync_ReplaceCapableAdapter_RefusedByRace_ReportsStale()
    {
        // Arrange: the replace-capable adapter owns the race decision too; a
        // fill that landed first refuses the replace, the terminal state wins.
        var adapter = new ReplaceableFakeTradingAdapter();
        var service = CreateService(adapter);
        var original = await adapter.PlaceOrderAsync(
            LimitOrder(OriginalQuantity, RestingLimitPrice));
        adapter.OnReplace = () => adapter.ForceFill(original.Id);
        adapter.ResetCounters();

        // Act
        var result = await service.AmendAsync(
            new OrderAmendRequest { Working = original, NewQuantity = ShrunkQuantity });

        // Assert
        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("Positions.AmendStale").And.Contain(original.Id);
        (await adapter.GetOpenOrdersAsync()).Should().BeEmpty();
        adapter.CancelCount.Should().Be(0);
        adapter.PlaceCount.Should().Be(0);
        _journal.GetRecent().Should().BeEmpty();
    }

    #endregion

    #region Test Doubles

    /// <summary>
    /// Minimal in-memory trading adapter shaped like the shipped paper one:
    /// placements rest as Pending, the cancel refuses everything not
    /// Pending with the exact paper refusal shape (the race handshake), and
    /// the hooks inject races at the exact seams the pipeline cannot lock
    /// its way around.
    /// </summary>
    private class FakeTradingAdapter : ITradingAdapter
    {
        private const string ExchangeId = "fake";

        private int _sequence;
        private readonly Dictionary<string, Order> _orders = [];

        /// <summary>Raised at the start of every cancel (the race injection point).</summary>
        public Action? OnCancel { get; set; }

        /// <summary>How many cancel requests reached the adapter.</summary>
        public int CancelCount { get; private set; }

        /// <summary>How many placements reached the adapter.</summary>
        public int PlaceCount { get; private set; }

        /// <summary>When set, every placement fails with this message.</summary>
        public string? PlacementFailure { get; set; }

        /// <summary>Clears the counters after the test's own setup placements.</summary>
        public void ResetCounters()
        {
            CancelCount = 0;
            PlaceCount = 0;
        }

        /// <summary>Terminal-state injection: the order filled before the flow's next step ran.</summary>
        public void ForceFill(string orderId)
        {
            var order = _orders[orderId];
            _orders[orderId] = order with
            {
                Status = OrderStatus.Filled,
                FilledQuantity = order.Quantity,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
        }

        /// <summary>The stored order entity (null when the id is unknown).</summary>
        protected Order? TryGet(string orderId) =>
            _orders.TryGetValue(orderId, out var order) ? order : null;

        /// <summary>Replace the stored entity under the same id.</summary>
        protected void Update(Order order) => _orders[order.Id] = order;

        public string Id => ExchangeId;

        public string Name => "Fake";

        public bool IsConnected => true;

        public ExchangeCapabilities Capabilities => ExchangeCapabilities.Trading;

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<Symbol>> GetSymbolsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException("the amend flow never touches market data");

        public IObservable<Ticker> SubscribeTicker(Symbol symbol) =>
            throw new NotSupportedException("the amend flow never touches market data");

        public IObservable<OrderBook> SubscribeOrderBook(Symbol symbol, int depth = 20) =>
            throw new NotSupportedException("the amend flow never touches market data");

        public IObservable<MarketTrade> SubscribeTrades(Symbol symbol) =>
            throw new NotSupportedException("the amend flow never touches market data");

        public Task<IReadOnlyList<Bar>> GetBarsAsync(
            Symbol symbol,
            TimeFrame timeFrame,
            DateTimeOffset from,
            DateTimeOffset? to = null,
            int limit = 500,
            CancellationToken ct = default) =>
            throw new NotSupportedException("the amend flow never touches market data");

        public Task<Order> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default)
        {
            PlaceCount++;
            if (PlacementFailure is { } failure)
            {
                throw new InvalidOperationException(failure);
            }

            var order = new Order
            {
                Id = $"fake-{Interlocked.Increment(ref _sequence)}",
                ExchangeId = ExchangeId,
                Symbol = request.Symbol,
                Side = request.Side,
                Type = request.Type,
                Quantity = request.Quantity,
                Price = request.Price ?? throw new InvalidOperationException("the replacement request carries no limit price"),
                StopPrice = request.StopPrice,
                TrailDistance = request.TrailDistance,
                OcoGroupId = request.OcoGroupId,
                TimeInForce = request.TimeInForce,
                Status = OrderStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            _orders[order.Id] = order;
            return Task.FromResult(order);
        }

        public Task CancelOrderAsync(Symbol symbol, string orderId, CancellationToken ct = default)
        {
            CancelCount++;
            OnCancel?.Invoke();
            if (!_orders.TryGetValue(orderId, out var order))
            {
                throw new InvalidOperationException($"Order {orderId} is unknown to the fake adapter.");
            }

            if (order.Symbol.Normalized != symbol.Normalized)
            {
                throw new InvalidOperationException(
                    $"Order {orderId} belongs to {order.Symbol.Normalized}, not {symbol.Normalized}.");
            }

            if (order.Status is not OrderStatus.Pending)
            {
                // The shipped paper semantics shape: exactly this refusal is
                // how the adapter tells the flow that a fill (or another
                // terminal event) already won.
                throw new InvalidOperationException($"Order {orderId} is already {order.Status}.");
            }

            _orders[orderId] = order with { Status = OrderStatus.Cancelled, UpdatedAt = DateTimeOffset.UtcNow };
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Order>> GetOpenOrdersAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Order>>(
                _orders.Values
                    .Where(order => order.Status
                        is OrderStatus.Pending
                        or OrderStatus.Submitted
                        or OrderStatus.PartiallyFilled)
                    .ToList());

        public Task<IReadOnlyList<Position>> GetPositionsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Position>>([]);

        public Task<IReadOnlyDictionary<string, Money>> GetBalancesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, Money>>(new Dictionary<string, Money>());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Fake adapter with an in-place replace seam, for the seam branch tests.</summary>
    private sealed class ReplaceableFakeTradingAdapter : FakeTradingAdapter, IOrderReplaceSupport
    {
        /// <summary>Raised at the start of every in-place replace (the race injection point).</summary>
        public Action? OnReplace { get; set; }

        public async Task<Order> ReplaceOrderAsync(
            Symbol symbol,
            string orderId,
            OrderRequest amended,
            CancellationToken ct = default)
        {
            OnReplace?.Invoke();
            if (TryGet(orderId) is not { } stored)
            {
                throw new InvalidOperationException($"Order {orderId} is unknown to the fake adapter.");
            }

            if (stored.Status is not OrderStatus.Pending)
            {
                throw new InvalidOperationException($"Order {orderId} is already {stored.Status}.");
            }

            var replaced = stored with
            {
                Quantity = amended.Quantity,
                Price = amended.Price ?? stored.Price,
                StopPrice = amended.StopPrice,
                TrailDistance = amended.TrailDistance,
                OcoGroupId = amended.OcoGroupId,
                TimeInForce = amended.TimeInForce,
                Expiry = amended.Expiry,
                ClientOrderId = amended.ClientOrderId,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            Update(replaced);
            await Task.CompletedTask;
            return replaced;
        }
    }

    #endregion

    private static string FormatArgs(object[] args) => string.Join("|", args);
}
