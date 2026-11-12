namespace ZettaTrade.Infrastructure.Tests.Exchange;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Core.Entities;
using Core.Enums;
using Core.Interfaces;
using Core.ValueObjects;
using FluentAssertions;
using Infrastructure.Exchange;
using Infrastructure.Fees;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

/// <summary>
/// Tests for the paper adapter's protective-level semantics (T-187): a
/// position's SL/TP edit places the protective pair as an either-or group of
/// reduce-only legs through the existing conditional machinery, the
/// position's own read surfaces the working legs' levels, a touched level
/// closes the position through the normal fill path (the sibling tears down
/// with it), and the reduce-only honor clamps every protective fill at the
/// position it protects — a protective fill can shrink the position, never
/// open or flip exposure. All market data comes from NSubstitute stubs the
/// tests control.
/// </summary>
public sealed class PaperProtectiveLevelsTests
{
    #region Test Constants

    /// <summary>Close of the simulated latest reference bar.</summary>
    private const decimal ReferenceClose = 50_000m;

    /// <summary>Common order quantity in base asset.</summary>
    private const decimal OrderQuantity = 0.2m;

    /// <summary>Spread between the tick's bid and ask legs.</summary>
    private const decimal TickSpread = 1m;

    /// <summary>Notional-ish ticker volume value the simulation never reads.</summary>
    private const decimal TickerVolume = 1_000m;

    /// <summary>Bounded wait for an asynchronous tick evaluation to settle.</summary>
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(10);

    /// <summary>Interval between polls of an asynchronous settle condition.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    private static readonly DateTimeOffset ReferenceBarTime =
        new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private const TimeFrame ReferenceTimeFrame = TimeFrame.H1;

    private static readonly Bar ReferenceBar =
        new(ReferenceBarTime, 49_500m, 50_500m, 49_000m, ReferenceClose, 1_000m);

    #endregion

    #region Surfacing

    [Fact]
    public async Task GetPositions_ProtectivePairWorking_SurfacesBothLevels()
    {
        // Arrange: a long from a market buy, protected by the edit's pair —
        // the take-profit limit above the market, the stop-loss stop-limit
        // below it, both reduce-only under one either-or group.
        var (marketData, _) = CreateMarketData();
        var adapter = CreateAdapter(marketData);
        var groupId = OcoId();

        await adapter.PlaceOrderAsync(MarketOrder(BtcUsdt(), OrderSide.Buy, OrderQuantity));
        await adapter.PlaceOrderAsync(TakeProfitLeg(BtcUsdt(), OrderQuantity, 51_000m, groupId));
        await adapter.PlaceOrderAsync(StopLossLeg(BtcUsdt(), OrderQuantity, 49_000m, groupId));

        // Act
        var positions = await adapter.GetPositionsAsync();

        // Assert: the position reads the levels it carries.
        var position = positions.Should().ContainSingle().Subject;
        position.StopLoss!.Value.Value.Should().Be(49_000m, "the stop-loss leg's trigger is the position's SL");
        position.TakeProfit!.Value.Value.Should().Be(51_000m, "the take-profit leg's limit is the position's TP");
        (await adapter.GetOpenOrdersAsync()).Should().HaveCount(2, "both protective legs stay working");
    }

    [Fact]
    public async Task GetPositions_WrongSideOrStandaloneReduceOnlyLegs_NeverSurfaceAsProtection()
    {
        // Arrange: a long carrying a proper pair, plus two decoys riding the
        // same conditional store — a same-side reduce-only buy stop (no
        // protection direction for a long) and a non-reduce-only sell limit
        // (a bracket-style leg, not an edit's pair).
        var (marketData, _) = CreateMarketData();
        var adapter = CreateAdapter(marketData);
        var groupId = OcoId();

        await adapter.PlaceOrderAsync(MarketOrder(BtcUsdt(), OrderSide.Buy, OrderQuantity));
        await adapter.PlaceOrderAsync(TakeProfitLeg(BtcUsdt(), OrderQuantity, 51_000m, groupId));
        await adapter.PlaceOrderAsync(StopLossLeg(BtcUsdt(), OrderQuantity, 49_000m, groupId));
        await adapter.PlaceOrderAsync(Leg(
            BtcUsdt(),
            OrderSide.Buy,
            OrderQuantity,
            51_100m,
            reduceOnly: true,
            type: OrderType.StopLimit,
            trigger: 51_000m));
        await adapter.PlaceOrderAsync(Leg(
            BtcUsdt(),
            OrderSide.Sell,
            OrderQuantity,
            52_000m,
            reduceOnly: false,
            ocoGroupId: OcoId()));

        // Act
        var positions = await adapter.GetPositionsAsync();

        // Assert: only the opposite-side reduce-only legs read as protection.
        var position = positions.Should().ContainSingle().Subject;
        position.StopLoss!.Value.Value.Should().Be(49_000m);
        position.TakeProfit!.Value.Value.Should().Be(51_000m);
    }

    #endregion

    #region Touch

    [Fact]
    public async Task StopTouch_ClosesThePositionThroughTheNormalFillPath_AndCancelsTheTakeProfit()
    {
        // Arrange
        var (marketData, ticker) = CreateMarketData();
        var adapter = CreateAdapter(marketData);
        var groupId = OcoId();

        await adapter.PlaceOrderAsync(MarketOrder(BtcUsdt(), OrderSide.Buy, OrderQuantity));
        var takeProfit = await adapter.PlaceOrderAsync(TakeProfitLeg(BtcUsdt(), OrderQuantity, 51_000m, groupId));
        var stopLoss = await adapter.PlaceOrderAsync(StopLossLeg(BtcUsdt(), OrderQuantity, 49_000m, groupId));

        // Act: the falling market takes the stop leg out.
        ticker.OnNext(Tick(48_900m));
        await AwaitSettledAsync(async () => (await adapter.GetOpenOrdersAsync()).Count == 0);

        // Assert: the position closed through the normal fill path, the
        // sibling cancelled with it, no orphan stays working.
        (await adapter.GetPositionsAsync()).Should().BeEmpty("the stop fill closed the whole long");
        var cancelStop = async () => await adapter.CancelOrderAsync(BtcUsdt(), stopLoss.Id);
        await cancelStop.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Filled*");
        var cancelTakeProfit = async () => await adapter.CancelOrderAsync(BtcUsdt(), takeProfit.Id);
        await cancelTakeProfit.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Cancelled*");
        (await adapter.GetBalancesAsync())[PaperTradingAdapter.QuoteCurrency].Amount
            .Should().Be(
                PaperTradingAdapter.InitialQuoteAmount - (ReferenceClose * OrderQuantity) + (49_000m * OrderQuantity),
                "the entry spent the opening notional, the stop fill returned the closing proceeds");
    }

    [Fact]
    public async Task TakeProfitTouch_ClosesThePosition_AndCancelsTheStop()
    {
        // Arrange
        var (marketData, ticker) = CreateMarketData();
        var adapter = CreateAdapter(marketData);
        var groupId = OcoId();

        await adapter.PlaceOrderAsync(MarketOrder(BtcUsdt(), OrderSide.Buy, OrderQuantity));
        var takeProfit = await adapter.PlaceOrderAsync(TakeProfitLeg(BtcUsdt(), OrderQuantity, 51_000m, groupId));
        var stopLoss = await adapter.PlaceOrderAsync(StopLossLeg(BtcUsdt(), OrderQuantity, 49_000m, groupId));

        // Act: the rising market fills the take-profit limit.
        ticker.OnNext(Tick(51_100m));
        await AwaitSettledAsync(async () => (await adapter.GetOpenOrdersAsync()).Count == 0);

        // Assert
        (await adapter.GetPositionsAsync()).Should().BeEmpty("the take-profit fill closed the whole long");
        var cancelStop = async () => await adapter.CancelOrderAsync(BtcUsdt(), stopLoss.Id);
        await cancelStop.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Cancelled*");
        (await adapter.GetBalancesAsync())[PaperTradingAdapter.QuoteCurrency].Amount
            .Should().Be(
                PaperTradingAdapter.InitialQuoteAmount - (ReferenceClose * OrderQuantity) + (51_000m * OrderQuantity),
                "the entry spent the opening notional, the take-profit fill returned the closing proceeds");
    }

    #endregion

    #region Clear

    [Fact]
    public async Task ClearProtection_CancelsOneLeg_ThePairAndTheLevelsVanishTogether()
    {
        // Arrange: the clear path cancels the working protective legs; the
        // either-or teardown takes the sibling down with the first cancel.
        var (marketData, _) = CreateMarketData();
        var adapter = CreateAdapter(marketData);
        var groupId = OcoId();

        await adapter.PlaceOrderAsync(MarketOrder(BtcUsdt(), OrderSide.Buy, OrderQuantity));
        var takeProfit = await adapter.PlaceOrderAsync(TakeProfitLeg(BtcUsdt(), OrderQuantity, 51_000m, groupId));
        await adapter.PlaceOrderAsync(StopLossLeg(BtcUsdt(), OrderQuantity, 49_000m, groupId));

        // Act
        await adapter.CancelOrderAsync(BtcUsdt(), takeProfit.Id);
        var positions = await adapter.GetPositionsAsync();

        // Assert: the position remains, unprotected and honestly read.
        var position = positions.Should().ContainSingle().Subject;
        position.StopLoss.Should().BeNull("the protective pair cancelled with its first leg");
        position.TakeProfit.Should().BeNull();
        (await adapter.GetOpenOrdersAsync()).Should().BeEmpty();
    }

    #endregion

    #region Reduce-Only Honor

    [Fact]
    public async Task ReduceOnlyStop_OversizedFill_ClampsAtThePosition_NeverFlips()
    {
        // Arrange: a short of 0.2 protected by a buy stop sized 0.5 — the
        // stale oversized protective leg must close the short, not flip it
        // into a fresh long.
        var (marketData, ticker) = CreateMarketData();
        var adapter = CreateAdapter(marketData);

        await adapter.PlaceOrderAsync(MarketOrder(BtcUsdt(), OrderSide.Sell, OrderQuantity));
        var oversized = await adapter.PlaceOrderAsync(Leg(
            BtcUsdt(),
            OrderSide.Buy,
            0.5m,
            51_000m,
            reduceOnly: true,
            type: OrderType.StopLimit,
            trigger: 51_000m));

        // Act: the rising market fires the buy stop.
        ticker.OnNext(Tick(51_100m));
        await AwaitSettledAsync(async () =>
        {
            var orders = ReadOrders(adapter);
            return orders.TryGetValue(oversized.Id, out var record)
                && record.Status is OrderStatus.Filled;
        });

        // Assert: the fill trimmed to the position it closed; no flip.
        (await adapter.GetPositionsAsync()).Should().BeEmpty("the clamped fill closed the whole short");
        var orders = ReadOrders(adapter);
        orders[oversized.Id].FilledQuantity.Value.Should().Be(OrderQuantity, "the fill recorded what the position allowed");
        (await adapter.GetBalancesAsync())[PaperTradingAdapter.QuoteCurrency].Amount
            .Should().Be(
                PaperTradingAdapter.InitialQuoteAmount + (ReferenceClose * OrderQuantity) - (51_000m * OrderQuantity),
                "the short entry credited the opening notional, the clamped fill spent the closing cost");
    }

    [Fact]
    public async Task ReduceOnlyLeg_NothingToClose_RejectsOnTrigger_AndSelfCleansThePair()
    {
        // Arrange: a protected long closed by another route — the stale pair
        // stays working until its first trigger attempt, which refuses
        // honestly and takes the sibling down with it.
        var (marketData, ticker) = CreateMarketData();
        var adapter = CreateAdapter(marketData);
        var groupId = OcoId();

        await adapter.PlaceOrderAsync(MarketOrder(BtcUsdt(), OrderSide.Buy, OrderQuantity));
        var takeProfit = await adapter.PlaceOrderAsync(TakeProfitLeg(BtcUsdt(), OrderQuantity, 51_000m, groupId));
        var stopLoss = await adapter.PlaceOrderAsync(StopLossLeg(BtcUsdt(), OrderQuantity, 49_000m, groupId));
        await adapter.PlaceOrderAsync(MarketOrder(BtcUsdt(), OrderSide.Sell, OrderQuantity));
        (await adapter.GetPositionsAsync()).Should().BeEmpty("the manual close flattened the position first");

        // Act: the falling market reaches the stale stop's trigger.
        ticker.OnNext(Tick(48_900m));
        await AwaitSettledAsync(async () => (await adapter.GetOpenOrdersAsync()).Count == 0);

        // Assert: the leg rejected with the honest reason, the sibling
        // cancelled, nothing filled, no phantom position.
        var orders = ReadOrders(adapter);
        orders[stopLoss.Id].Status.Should().Be(OrderStatus.Rejected);
        orders[stopLoss.Id].RejectReason.Should().Contain("no opposite-side position");
        orders[takeProfit.Id].Status.Should().Be(OrderStatus.Cancelled);
        (await adapter.GetPositionsAsync()).Should().BeEmpty();
    }

    #endregion

    #region Helpers

    private static PaperTradingAdapter CreateAdapter(IMarketDataAdapter marketData) =>
        new(marketData, PaperTradingAdapterTests.CreateFeeSchedules(), NullLogger<PaperTradingAdapter>.Instance);

    /// <summary>The adapter's order store, read for terminal-state assertions.</summary>
    private static ConcurrentDictionary<string, Order> ReadOrders(PaperTradingAdapter adapter) =>
        (ConcurrentDictionary<string, Order>)typeof(PaperTradingAdapter)
            .GetField("_orders", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(adapter)!;

    /// <summary>
    /// Bounded wait until the asynchronous tick evaluations settle: polls the
    /// condition on the poll interval, reporting a timeout instead of a
    /// swallowed state.
    /// </summary>
    private static async Task AwaitSettledAsync(Func<Task<bool>> condition)
    {
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start <= EventWait)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(PollInterval);
        }

        throw new TimeoutException("The tick evaluation did not settle within the wait.");
    }

    /// <summary>
    /// Market data stub serving the reference bar for every reference-close
    /// request and returning the SAME test-controlled ticker subject to every
    /// subscriber.
    /// </summary>
    private static (IMarketDataAdapter MarketData, Subject<Ticker> Ticker)
        CreateMarketData()
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

    /// <summary>A ticker update; the simulation reads only the last price.</summary>
    private static Ticker Tick(decimal lastPrice) => new(
        BtcUsdt(),
        LastPrice: lastPrice,
        Bid: lastPrice,
        Ask: lastPrice + TickSpread,
        Volume24h: TickerVolume,
        PriceChange24hPercent: 0m,
        Timestamp: DateTimeOffset.UtcNow);

    private static Symbol BtcUsdt() => new("BTC", "USDT", AssetClass.Crypto);

    private static string OcoId() => $"oco-{Guid.NewGuid():N}";

    private static OrderRequest MarketOrder(Symbol symbol, OrderSide side, decimal quantity) => new()
    {
        Symbol = symbol,
        Side = side,
        Type = OrderType.Market,
        Quantity = new Quantity(quantity),
    };

    /// <summary>The take-profit leg of a protective pair: a resting reduce-only limit.</summary>
    private static OrderRequest TakeProfitLeg(Symbol symbol, decimal quantity, decimal price, string ocoGroupId) =>
        Leg(symbol, OrderSide.Sell, quantity, price, reduceOnly: true, ocoGroupId: ocoGroupId);

    /// <summary>
    /// The stop-loss leg of a protective pair: a reduce-only stop-limit whose
    /// limit equals its trigger (the paper fill executes at the level — the
    /// stop-market reading of the protective stop).
    /// </summary>
    private static OrderRequest StopLossLeg(Symbol symbol, decimal quantity, decimal trigger, string ocoGroupId) =>
        Leg(
            symbol,
            OrderSide.Sell,
            quantity,
            trigger,
            reduceOnly: true,
            type: OrderType.StopLimit,
            trigger: trigger,
            ocoGroupId: ocoGroupId);

    private static OrderRequest Leg(
        Symbol symbol,
        OrderSide side,
        decimal quantity,
        decimal price,
        bool reduceOnly = false,
        OrderType type = OrderType.Limit,
        decimal? trigger = null,
        string? ocoGroupId = null) => new()
        {
            Symbol = symbol,
            Side = side,
            Type = type,
            Quantity = new Quantity(quantity),
            Price = new Price(price),
            StopPrice = trigger is { } triggerValue ? new Price(triggerValue) : null,
            OcoGroupId = ocoGroupId,
            ReduceOnly = reduceOnly,
        };

    #endregion
}
