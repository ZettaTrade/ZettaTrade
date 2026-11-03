namespace ZettaTrade.Infrastructure.Tests.Exchange;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Entities;
using Core.Enums;
using Core.Interfaces;
using Core.ValueObjects;
using FluentAssertions;
using Infrastructure.Exchange;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

/// <summary>
/// Tests for the paper trading adapter: immediate fills for market, stop and
/// market-crossing limit orders, resting (Pending) limits placed away from the
/// market plus their cancellation, and balance and netted-position bookkeeping.
/// All market data comes from an NSubstitute <see cref="IMarketDataAdapter"/>, so
/// these tests are exchange-agnostic by construction.
/// </summary>
public sealed class PaperTradingAdapterTests
{
    #region Test Constants

    /// <summary>Close of the simulated latest reference bar.</summary>
    private const decimal ReferenceClose = 50_000m;

    /// <summary>Buy limit placed BELOW the reference close — it cannot cross, so it rests.</summary>
    private const decimal RestingBuyLimitPrice = 49_000m;

    /// <summary>Buy limit placed ABOVE the reference close — it crosses and fills immediately.</summary>
    private const decimal CrossingBuyLimitPrice = 50_500m;

    /// <summary>Sell limit placed ABOVE the reference close — it cannot cross, so it rests.</summary>
    private const decimal RestingSellLimitPrice = 51_000m;

    /// <summary>Sell limit placed BELOW the reference close — it crosses and fills immediately.</summary>
    private const decimal CrossingSellLimitPrice = 49_500m;

    /// <summary>Common order quantity in base asset.</summary>
    private const decimal OrderQuantity = 0.2m;

    /// <summary>Notional of a market fill at the reference close.</summary>
    private const decimal MarketNotional = 10_000m;

    /// <summary>Notional of a crossing buy limit fill at its limit price.</summary>
    private const decimal CrossingBuyNotional = 10_100m;

    /// <summary>Notional of a crossing sell limit fill at its limit price.</summary>
    private const decimal CrossingSellNotional = 9_900m;

    /// <summary>Multiplier producing a buy order that exceeds the simulated balance.</summary>
    private const decimal ExcessiveQuantityFactor = 25m;

    private static readonly DateTimeOffset ReferenceBarTime =
        new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private const TimeFrame ReferenceTimeFrame = TimeFrame.H1;

    #endregion

    #region Happy Path

    [Fact]
    public async Task PlaceOrder_MarketBuy_FillsAtReferenceBarClose()
    {
        // Arrange
        var marketData = CreateMarketData();
        var adapter = CreateAdapter(marketData);
        var symbol = BtcUsdt();

        // Act
        var order = await adapter.PlaceOrderAsync(MarketOrder(symbol, OrderSide.Buy, OrderQuantity));

        // Assert
        order.Status.Should().Be(OrderStatus.Filled);
        order.ExchangeId.Should().Be(PaperTradingAdapter.ExchangeId);
        order.Symbol.Should().Be(symbol);
        order.Side.Should().Be(OrderSide.Buy);
        order.Quantity.Value.Should().Be(OrderQuantity);
        order.Price!.Value.Should().Be(ReferenceClose);

        // The reference price is the latest H1 bar close requested from the wrapped adapter.
        await marketData.Received(1).GetBarsAsync(
            symbol,
            ReferenceTimeFrame,
            Arg.Any<DateTimeOffset>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlaceOrder_MarketBuy_UpdatesBalanceAndPosition()
    {
        // Arrange
        var adapter = CreateAdapter(CreateMarketData());

        // Act
        var order = await adapter.PlaceOrderAsync(MarketOrder(BtcUsdt(), OrderSide.Buy, OrderQuantity));
        var balances = await adapter.GetBalancesAsync();
        var positions = await adapter.GetPositionsAsync();

        // Assert
        balances[PaperTradingAdapter.QuoteCurrency].Amount
            .Should().Be(PaperTradingAdapter.InitialQuoteAmount - MarketNotional);

        var position = positions.Should().ContainSingle().Which;
        position.Symbol.Normalized.Should().Be(order.Symbol.Normalized);
        position.Side.Should().Be(OrderSide.Buy);
        position.Quantity.Value.Should().Be(OrderQuantity);
        position.EntryPrice.Value.Should().Be(ReferenceClose);
        position.CurrentPrice.Value.Should().Be(ReferenceClose);
    }

    [Fact]
    public async Task PlaceOrder_MarketSell_OpensShortPositionAndCreditsQuote()
    {
        // Arrange
        var adapter = CreateAdapter(CreateMarketData());

        // Act
        var order = await adapter.PlaceOrderAsync(MarketOrder(BtcUsdt(), OrderSide.Sell, OrderQuantity));
        var balances = await adapter.GetBalancesAsync();
        var positions = await adapter.GetPositionsAsync();

        // Assert
        order.Side.Should().Be(OrderSide.Sell);
        balances[PaperTradingAdapter.QuoteCurrency].Amount
            .Should().Be(PaperTradingAdapter.InitialQuoteAmount + MarketNotional);

        var position = positions.Should().ContainSingle().Which;
        position.Side.Should().Be(OrderSide.Sell);
        position.Quantity.Value.Should().Be(OrderQuantity);
    }

    [Fact]
    public async Task PlaceOrder_SellThenBuySameQuantity_NetsPositionToZero()
    {
        // Arrange: the round-trip buy limit sits exactly at the reference close,
        // so it crosses the market and fills immediately at its limit price.
        var adapter = CreateAdapter(CreateMarketData());

        // Act
        await adapter.PlaceOrderAsync(MarketOrder(BtcUsdt(), OrderSide.Sell, OrderQuantity));
        await adapter.PlaceOrderAsync(LimitOrder(BtcUsdt(), OrderSide.Buy, OrderQuantity, ReferenceClose));
        var positions = await adapter.GetPositionsAsync();
        var balances = await adapter.GetBalancesAsync();

        // Assert: full round trip restores the starting quote balance and removes the position.
        positions.Should().BeEmpty();
        balances[PaperTradingAdapter.QuoteCurrency].Amount
            .Should().Be(PaperTradingAdapter.InitialQuoteAmount);
    }

    #endregion

    #region Limit Resting

    [Fact]
    public async Task PlaceOrder_CrossingLimitBuy_FillsAtLimitPrice()
    {
        // Arrange
        var marketData = CreateMarketData();
        var adapter = CreateAdapter(marketData);
        var symbol = BtcUsdt();

        // Act: a buy at or above the reference close would execute at once.
        var order = await adapter.PlaceOrderAsync(LimitOrder(symbol, OrderSide.Buy, OrderQuantity, CrossingBuyLimitPrice));
        var balances = await adapter.GetBalancesAsync();
        var positions = await adapter.GetPositionsAsync();

        // Assert
        order.Status.Should().Be(OrderStatus.Filled);
        order.Price!.Value.Should().Be(CrossingBuyLimitPrice);
        order.FilledQuantity.Value.Should().Be(OrderQuantity);
        balances[PaperTradingAdapter.QuoteCurrency].Amount
            .Should().Be(PaperTradingAdapter.InitialQuoteAmount - CrossingBuyNotional);
        positions.Should().ContainSingle().Which.EntryPrice.Value.Should().Be(CrossingBuyLimitPrice);

        // The crossing decision reads the reference close from the wrapped adapter.
        await marketData.Received(1).GetBarsAsync(
            symbol,
            ReferenceTimeFrame,
            Arg.Any<DateTimeOffset>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlaceOrder_RestingLimitBuy_StaysPendingWithoutTouchingPortfolio()
    {
        // Arrange
        var marketData = CreateMarketData();
        var adapter = CreateAdapter(marketData);
        var symbol = BtcUsdt();

        // Act: a buy below the reference close cannot cross, so the order rests.
        var order = await adapter.PlaceOrderAsync(LimitOrder(symbol, OrderSide.Buy, OrderQuantity, RestingBuyLimitPrice));
        var balances = await adapter.GetBalancesAsync();
        var positions = await adapter.GetPositionsAsync();
        var open = await adapter.GetOpenOrdersAsync();

        // Assert
        order.Status.Should().Be(OrderStatus.Pending);
        order.Price!.Value.Should().Be(RestingBuyLimitPrice);
        order.FilledQuantity.Value.Should().Be(Quantity.Zero.Value);
        balances.Should().BeEmpty("a resting order must not touch balances or positions");
        positions.Should().BeEmpty();
        open.Should().ContainSingle().Which.Id.Should().Be(order.Id);

        // The resting decision also reads the reference close from the wrapped adapter.
        await marketData.Received(1).GetBarsAsync(
            symbol,
            ReferenceTimeFrame,
            Arg.Any<DateTimeOffset>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlaceOrder_CrossingLimitSell_FillsAtLimitPrice()
    {
        // Arrange
        var adapter = CreateAdapter(CreateMarketData());

        // Act: a sell at or below the reference close would execute at once.
        var order = await adapter.PlaceOrderAsync(LimitOrder(BtcUsdt(), OrderSide.Sell, OrderQuantity, CrossingSellLimitPrice));
        var balances = await adapter.GetBalancesAsync();
        var positions = await adapter.GetPositionsAsync();

        // Assert
        order.Status.Should().Be(OrderStatus.Filled);
        order.Price!.Value.Should().Be(CrossingSellLimitPrice);
        order.FilledQuantity.Value.Should().Be(OrderQuantity);
        balances[PaperTradingAdapter.QuoteCurrency].Amount
            .Should().Be(PaperTradingAdapter.InitialQuoteAmount + CrossingSellNotional);
        positions.Should().ContainSingle().Which.Side.Should().Be(OrderSide.Sell);
        positions.Should().ContainSingle().Which.EntryPrice.Value.Should().Be(CrossingSellLimitPrice);
    }

    [Fact]
    public async Task PlaceOrder_RestingLimitSell_StaysPendingWithoutTouchingPortfolio()
    {
        // Arrange
        var adapter = CreateAdapter(CreateMarketData());

        // Act: a sell above the reference close cannot cross, so the order rests.
        var order = await adapter.PlaceOrderAsync(LimitOrder(BtcUsdt(), OrderSide.Sell, OrderQuantity, RestingSellLimitPrice));
        var balances = await adapter.GetBalancesAsync();
        var positions = await adapter.GetPositionsAsync();

        // Assert
        order.Status.Should().Be(OrderStatus.Pending);
        order.Price!.Value.Should().Be(RestingSellLimitPrice);
        order.FilledQuantity.Value.Should().Be(Quantity.Zero.Value);
        balances.Should().BeEmpty("a resting order must not touch balances or positions");
        positions.Should().BeEmpty();
    }

    [Fact]
    public async Task CancelOrder_RestingLimit_CancelsAndNeverTouchedPortfolio()
    {
        // Arrange
        var adapter = CreateAdapter(CreateMarketData());
        var order = await adapter.PlaceOrderAsync(LimitOrder(BtcUsdt(), OrderSide.Buy, OrderQuantity, RestingBuyLimitPrice));

        // Act
        await adapter.CancelOrderAsync(BtcUsdt(), order.Id);

        // Assert: the order left the working list and never carried a portfolio effect.
        (await adapter.GetOpenOrdersAsync()).Should().BeEmpty();
        (await adapter.GetBalancesAsync()).Should().BeEmpty();
        (await adapter.GetPositionsAsync()).Should().BeEmpty();

        // There is no order getter, so a second cancel serves as the status probe:
        // only Pending/Submitted orders can be cancelled, and Cancelled is neither.
        var again = async () => await adapter.CancelOrderAsync(BtcUsdt(), order.Id);
        await again.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already Cancelled*");
    }

    #endregion

    #region Failure Paths

    [Fact]
    public async Task PlaceOrder_MarketBuyWithoutReferenceBars_Throws()
    {
        // Arrange
        var marketData = Substitute.For<IMarketDataAdapter>();
        marketData.GetBarsAsync(
                Arg.Any<Symbol>(),
                Arg.Any<TimeFrame>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns([]);
        var adapter = CreateAdapter(marketData);

        // Act
        var act = async () => await adapter.PlaceOrderAsync(MarketOrder(BtcUsdt(), OrderSide.Buy, OrderQuantity));

        // Assert: a market order without reference data must fail loudly, not silently fill.
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*reference bar*");
    }

    [Fact]
    public async Task PlaceOrder_LimitWithoutReferenceBars_Throws()
    {
        // Arrange: a limit order needs the reference close to decide whether it crosses.
        var marketData = Substitute.For<IMarketDataAdapter>();
        marketData.GetBarsAsync(
                Arg.Any<Symbol>(),
                Arg.Any<TimeFrame>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns([]);
        var adapter = CreateAdapter(marketData);

        // Act
        var act = async () => await adapter.PlaceOrderAsync(
            LimitOrder(BtcUsdt(), OrderSide.Buy, OrderQuantity, CrossingBuyLimitPrice));

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*reference bar*");
    }

    [Fact]
    public async Task PlaceOrder_InsufficientQuoteBalance_ThrowsAndLeavesPortfolioUntouched()
    {
        // Arrange
        var adapter = CreateAdapter(CreateMarketData());
        var hugeQuantity = (PaperTradingAdapter.InitialQuoteAmount / ReferenceClose) * ExcessiveQuantityFactor;

        // Act
        var act = async () => await adapter.PlaceOrderAsync(MarketOrder(BtcUsdt(), OrderSide.Buy, hugeQuantity));

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Insufficient*");

        var balances = await adapter.GetBalancesAsync();
        balances.Should().BeEmpty("a rejected order must not create balance entries");
        (await adapter.GetPositionsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task CancelOrder_UnknownOrderId_Throws()
    {
        // Arrange
        var adapter = CreateAdapter(CreateMarketData());

        // Act
        var act = async () => await adapter.CancelOrderAsync(BtcUsdt(), "paper-unknown");

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*unknown to the paper adapter*");
    }

    [Fact]
    public async Task CancelOrder_MismatchedSymbol_Throws()
    {
        // Arrange: the limit rests (it does not cross the reference close), so it exists.
        var adapter = CreateAdapter(CreateMarketData());
        var order = await adapter.PlaceOrderAsync(LimitOrder(BtcUsdt(), OrderSide.Buy, OrderQuantity, RestingBuyLimitPrice));

        // Act
        var act = async () => await adapter.CancelOrderAsync(OtherSymbol(), order.Id);

        // Assert: cancelling requires the exact symbol the order belongs to.
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*belongs to*");
    }

    [Fact]
    public async Task CancelOrder_AlreadyFilledOrder_Throws()
    {
        // Arrange: market orders fill immediately, so a filled order is no longer cancellable.
        var adapter = CreateAdapter(CreateMarketData());
        var order = await adapter.PlaceOrderAsync(MarketOrder(BtcUsdt(), OrderSide.Buy, OrderQuantity));

        // Act
        var act = async () => await adapter.CancelOrderAsync(BtcUsdt(), order.Id);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already*");
    }

    [Fact]
    public async Task GetOpenOrders_AfterImmediateFills_IsAlwaysEmpty()
    {
        // Arrange
        var adapter = CreateAdapter(CreateMarketData());
        await adapter.PlaceOrderAsync(MarketOrder(BtcUsdt(), OrderSide.Buy, OrderQuantity));

        // Act
        var open = await adapter.GetOpenOrdersAsync();

        // Assert
        open.Should().BeEmpty("a market order fills immediately and leaves nothing working");
    }

    #endregion

    #region Helpers

    private static PaperTradingAdapter CreateAdapter(IMarketDataAdapter marketData) =>
        new(marketData, NullLogger<PaperTradingAdapter>.Instance);

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
        new(ReferenceBarTime, 49_500m, 50_500m, 49_000m, ReferenceClose, 1_000m);

    private static Symbol BtcUsdt() => new("BTC", "USDT", AssetClass.Crypto);

    private static Symbol OtherSymbol() => new("ETH", "USDT", AssetClass.Crypto);

    private static OrderRequest MarketOrder(Symbol symbol, OrderSide side, decimal quantity) =>
        new() { Symbol = symbol, Side = side, Type = OrderType.Market, Quantity = new Quantity(quantity) };

    private static OrderRequest LimitOrder(Symbol symbol, OrderSide side, decimal quantity, decimal price) =>
        new()
        {
            Symbol = symbol,
            Side = side,
            Type = OrderType.Limit,
            Quantity = new Quantity(quantity),
            Price = new Price(price),
        };

    #endregion
}
