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
/// Tests for the paper trading adapter: immediate fills against reference prices,
/// balance and netted-position bookkeeping, and cancellation validation.
/// All market data comes from an NSubstitute <see cref="IMarketDataAdapter"/>, so
/// these tests are exchange-agnostic by construction.
/// </summary>
public sealed class PaperTradingAdapterTests
{
    #region Test Constants

    /// <summary>Close of the simulated latest reference bar.</summary>
    private const decimal ReferenceClose = 50_000m;

    /// <summary>Limit price used for limit orders.</summary>
    private const decimal LimitPrice = 49_000m;

    /// <summary>Common order quantity in base asset.</summary>
    private const decimal OrderQuantity = 0.2m;

    /// <summary>Notional of a market fill at the reference close.</summary>
    private const decimal MarketNotional = 10_000m;

    /// <summary>Notional of a limit fill at the limit price.</summary>
    private const decimal LimitNotional = 9_800m;

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
    public async Task PlaceOrder_LimitBuy_FillsAtLimitPriceWithoutMarketData()
    {
        // Arrange
        var marketData = CreateMarketData();
        var adapter = CreateAdapter(marketData);

        // Act
        var order = await adapter.PlaceOrderAsync(LimitOrder(BtcUsdt(), OrderSide.Buy, OrderQuantity, LimitPrice));
        var balances = await adapter.GetBalancesAsync();

        // Assert
        order.Price!.Value.Should().Be(LimitPrice);
        balances[PaperTradingAdapter.QuoteCurrency].Amount
            .Should().Be(PaperTradingAdapter.InitialQuoteAmount - LimitNotional);

        await marketData.DidNotReceive().GetBarsAsync(
            Arg.Any<Symbol>(),
            Arg.Any<TimeFrame>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
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
        // Arrange
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
        // Arrange
        var adapter = CreateAdapter(CreateMarketData());
        var order = await adapter.PlaceOrderAsync(LimitOrder(BtcUsdt(), OrderSide.Buy, OrderQuantity, LimitPrice));

        // Act
        var act = async () => await adapter.CancelOrderAsync(OtherSymbol(), order.Id);

        // Assert: cancelling requires the exact symbol the order belongs to.
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*belongs to*");
    }

    [Fact]
    public async Task CancelOrder_AlreadyFilledOrder_Throws()
    {
        // Arrange: the paper adapter fills immediately, so any placed order is no longer cancellable.
        var adapter = CreateAdapter(CreateMarketData());
        var order = await adapter.PlaceOrderAsync(LimitOrder(BtcUsdt(), OrderSide.Buy, OrderQuantity, LimitPrice));

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
        open.Should().BeEmpty("paper fills are immediate; there are no resting orders");
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
