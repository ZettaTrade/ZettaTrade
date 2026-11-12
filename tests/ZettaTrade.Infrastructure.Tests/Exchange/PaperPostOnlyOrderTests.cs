namespace ZettaTrade.Infrastructure.Tests.Exchange;

using System;
using System.Collections.Generic;
using System.Reactive.Subjects;
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
/// Tests for the paper adapter's post-only contract (T-191): a maker-only
/// limit must not cross the book at placement — a crossing post-only limit
/// (standalone or an OCO leg) is refused outright with no order ever
/// existing, and an away post-only limit rests WITH the flag recorded. All
/// market data comes from test-controlled substitutes, exactly like the
/// T-190 TIF battery's harness.
/// </summary>
public sealed class PaperPostOnlyOrderTests
{
    #region Test Constants

    /// <summary>Close of the simulated latest reference bar.</summary>
    private const decimal ReferenceClose = 50_000m;

    /// <summary>Common order quantity in base asset.</summary>
    private const decimal OrderQuantity = 0.2m;

    private static readonly DateTimeOffset ReferenceBarTime =
        new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    private static readonly Bar ReferenceBar =
        new(ReferenceBarTime, 49_500m, 50_500m, 49_000m, ReferenceClose, 1_000m);

    #endregion

    #region Standalone post-only limits

    [Fact]
    public async Task PlaceOrder_PostOnlyLimit_Crossing_ThrowsAndRecordsNothing()
    {
        // Arrange
        var (marketData, _) = CreatePostOnlyMarketData();
        var adapter = CreateAdapter(marketData);

        // Act: a buy limit above the reference close would cross — the
        // maker-only contract refuses the taker fill outright.
        var act = async () => await adapter.PlaceOrderAsync(
            LimitOrder(OrderSide.Buy, 50_100m, postOnly: true));

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Post-only*crosses*");
        (await adapter.GetOpenOrdersAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task PlaceOrder_PostOnlyLimit_Away_RestsWithFlag()
    {
        // Arrange
        var (marketData, _) = CreatePostOnlyMarketData();
        var adapter = CreateAdapter(marketData);

        // Act: a buy limit below the reference close rests as a maker.
        var order = await adapter.PlaceOrderAsync(
            LimitOrder(OrderSide.Buy, 49_900m, postOnly: true));

        // Assert: the resting order carries the honest post-only record.
        order.Status.Should().Be(OrderStatus.Pending);
        order.PostOnly.Should().BeTrue();
        order.ReduceOnly.Should().BeFalse();
        order.Price!.Value.Should().Be(49_900m);
        (await adapter.GetOpenOrdersAsync()).Should().ContainSingle().Which.Id.Should().Be(order.Id);
    }

    [Fact]
    public async Task PlaceOrder_PostOnlyLimit_WithReduceOnly_Away_RestsWithBothFlags()
    {
        // Arrange
        var (marketData, _) = CreatePostOnlyMarketData();
        var adapter = CreateAdapter(marketData);

        // Act: both flags ride one request.
        var order = await adapter.PlaceOrderAsync(
            LimitOrder(OrderSide.Sell, 50_100m, postOnly: true, reduceOnly: true));

        // Assert
        order.Status.Should().Be(OrderStatus.Pending);
        order.PostOnly.Should().BeTrue();
        order.ReduceOnly.Should().BeTrue();
    }

    [Fact]
    public async Task PlaceOrder_PostOnlyLimit_SellCrossing_ThrowsAndRecordsNothing()
    {
        // Arrange
        var (marketData, _) = CreatePostOnlyMarketData();
        var adapter = CreateAdapter(marketData);

        // Act: a sell limit at or below the reference close would cross.
        var act = async () => await adapter.PlaceOrderAsync(
            LimitOrder(OrderSide.Sell, 50_000m, postOnly: true));

        // Assert: the boundary case crosses too (a limit AT the market
        // takes liquidity against the reference close).
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Post-only*crosses*");
        (await adapter.GetOpenOrdersAsync()).Should().BeEmpty();
    }

    #endregion

    #region OCO limit legs

    [Fact]
    public async Task PlaceOrder_PostOnlyOcoLimitLeg_Crossing_ThrowsAndRecordsNothing()
    {
        // Arrange
        var (marketData, _) = CreatePostOnlyMarketData();
        var adapter = CreateAdapter(marketData);

        // Act: the OCO take-profit leg priced through the market is refused
        // exactly like a standalone crossing post-only limit.
        var act = async () => await adapter.PlaceOrderAsync(
            LimitOrder(OrderSide.Buy, 50_100m, postOnly: true, ocoGroupId: OcoId()));

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Post-only*crosses*");
        (await adapter.GetOpenOrdersAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task PlaceOrder_PostOnlyOcoLimitLeg_Away_ArmsWithFlag()
    {
        // Arrange
        var (marketData, _) = CreatePostOnlyMarketData();
        var adapter = CreateAdapter(marketData);

        // Act: the OCO leg away from the market arms like a plain limit.
        var order = await adapter.PlaceOrderAsync(
            LimitOrder(OrderSide.Buy, 49_900m, postOnly: true, ocoGroupId: OcoId()));

        // Assert
        order.Status.Should().Be(OrderStatus.Pending);
        order.PostOnly.Should().BeTrue();
        (await adapter.GetOpenOrdersAsync()).Should().ContainSingle().Which.Id.Should().Be(order.Id);
    }

    #endregion

    #region Harness

    private static PaperTradingAdapter CreateAdapter(IMarketDataAdapter marketData) =>
        new(marketData, PaperTradingAdapterTests.CreateFeeSchedules(), NullLogger<PaperTradingAdapter>.Instance);

    /// <summary>
    /// Market data stub serving the reference bar for every reference-close
    /// request and returning the SAME test-controlled ticker subject to
    /// every subscriber.
    /// </summary>
    private static (IMarketDataAdapter MarketData, Subject<Ticker> Ticker)
        CreatePostOnlyMarketData()
    {
        var ticker = new Subject<Ticker>();
        var marketData = Substitute.For<IMarketDataAdapter>();
        marketData.GetBarsAsync(
                Arg.Any<Symbol>(),
                Arg.Any<TimeFrame>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(new[] { ReferenceBar });

        marketData.SubscribeTicker(Arg.Any<Symbol>()).Returns(ticker);
        return (marketData, ticker);
    }

    private static Symbol BtcUsdt() => new("BTC", "USDT", AssetClass.Crypto);

    private static string OcoId() => $"oco-{Guid.NewGuid():N}";

    private static OrderRequest LimitOrder(
        OrderSide side,
        decimal price,
        bool postOnly = false,
        bool reduceOnly = false,
        string? ocoGroupId = null) => new()
        {
            Symbol = BtcUsdt(),
            Side = side,
            Type = OrderType.Limit,
            Quantity = new Quantity(OrderQuantity),
            Price = new Price(price),
            TimeInForce = TimeInForce.GTC,
            OcoGroupId = ocoGroupId,
            ReduceOnly = reduceOnly,
            PostOnly = postOnly,
        };

    #endregion
}
