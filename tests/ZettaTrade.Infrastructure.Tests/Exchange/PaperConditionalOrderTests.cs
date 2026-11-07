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
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

/// <summary>
/// Tests for the paper adapter's conditional-order simulation (T-074): a
/// stop-limit arms, rides the wrapped adapter's live ticker stream and fills
/// at its limit on the trigger price; a trailing stop ratchets its trigger
/// along the favorable direction and fills at the ratcheted level; an OCO
/// pair keeps the either-or contract — the first leg that fills or is
/// cancelled takes the remaining legs down with it, no orphan stays working,
/// and a leg joining a half pair is rejected explicitly. All market data
/// (reference bars and the pushed ticker stream) comes from NSubstitute
/// stubs the tests control.
/// </summary>
public sealed class PaperConditionalOrderTests
{
    #region Test Constants

    /// <summary>Close of the simulated latest reference bar.</summary>
    private const decimal ReferenceClose = 50_000m;

    /// <summary>Common order quantity in base asset.</summary>
    private const decimal OrderQuantity = 0.2m;

    /// <summary>Notional of a 0.2 base fill at the trailing-stop level 50_100.</summary>
    private const decimal TrailingSellNotional = 10_020m;

    /// <summary>Spread between the tick's bid and ask legs.</summary>
    private const decimal TickSpread = 1m;

    /// <summary>Notional-ish ticker volume value the simulation never reads.</summary>
    private const decimal TickerVolume = 1_000m;

    /// <summary>Bounded wait for an asynchronous tick evaluation to settle.</summary>
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Grace wait for a tick that must NOT change any state: the evaluation
    /// lane is asynchronous, so a negative assertion is only meaningful once
    /// the pushed evaluations have had their window to run.
    /// </summary>
    private static readonly TimeSpan TickerGraceWait = TimeSpan.FromMilliseconds(250);

    /// <summary>Interval between polls of an asynchronous settle condition.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    private static readonly DateTimeOffset ReferenceBarTime =
        new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private const TimeFrame ReferenceTimeFrame = TimeFrame.H1;

    private static readonly Bar ReferenceBar =
        new(ReferenceBarTime, 49_500m, 50_500m, 49_000m, ReferenceClose, 1_000m);

    #endregion

    #region Stop-Limit Simulation

    [Fact]
    public async Task PlaceOrder_BuyStopLimit_ArmsWorkingWithoutTouchingThePortfolio()
    {
        // Arrange
        var (marketData, ticker) = CreateConditionalMarketData();
        var adapter = CreateAdapter(marketData);

        // Act
        var order = await adapter.PlaceOrderAsync(StopLimitOrder(
            BtcUsdt(),
            OrderSide.Buy,
            OrderQuantity,
            trigger: 50_800m,
            limit: 50_900m));
        var open = await adapter.GetOpenOrdersAsync();
        var balances = await adapter.GetBalancesAsync();
        var positions = await adapter.GetPositionsAsync();

        // Assert: the conditional arms as a working order and waits for the stream.
        order.Status.Should().Be(OrderStatus.Pending);
        Dec(order.StopPrice).Should().Be(50_800m);
        order.Price!.Value.Should().Be(50_900m, "the working record shows its limit price");
        open.Should().ContainSingle().Which.Id.Should().Be(order.Id);
        balances.Should().BeEmpty("an armed conditional must not touch the portfolio");
        positions.Should().BeEmpty();
        _ = marketData.Received(1).SubscribeTicker(Arg.Any<Symbol>());
        ticker.HasObservers.Should().BeTrue("the adapter must ride the live price stream");
    }

    [Fact]
    public async Task PlaceOrder_BuyStopLimit_NeutralTick_KeepsTheOrderWorking()
    {
        // Arrange
        var (marketData, ticker) = CreateConditionalMarketData();
        var adapter = CreateAdapter(marketData);
        var order = await adapter.PlaceOrderAsync(StopLimitOrder(
            BtcUsdt(),
            OrderSide.Buy,
            OrderQuantity,
            trigger: 50_800m,
            limit: 50_900m));

        // Act: a price inside the trigger never fires it.
        ticker.OnNext(Tick(50_400m));
        await Task.Delay(TickerGraceWait);
        var open = await adapter.GetOpenOrdersAsync();
        var positions = await adapter.GetPositionsAsync();

        // Assert
        open.Should().ContainSingle().Which.Id.Should().Be(order.Id);
        positions.Should().BeEmpty();
    }

    [Fact]
    public async Task PlaceOrder_BuyStopLimit_TriggeredTick_FillsAtTheLimitPrice()
    {
        // Arrange
        var (marketData, ticker) = CreateConditionalMarketData();
        var adapter = CreateAdapter(marketData);
        var order = await adapter.PlaceOrderAsync(StopLimitOrder(
            BtcUsdt(),
            OrderSide.Buy,
            OrderQuantity,
            trigger: 50_800m,
            limit: 50_900m));

        // Act: the trigger fires; the limit leg buys at its own limit price.
        ticker.OnNext(Tick(50_820m));
        await AwaitSettledAsync(async () => (await adapter.GetOpenOrdersAsync()).Count == 0);

        // Assert
        var again = async () => await adapter.CancelOrderAsync(BtcUsdt(), order.Id);
        await again.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Filled*");
        (await adapter.GetPositionsAsync()).Should().ContainSingle().Which.EntryPrice.Value
            .Should().Be(50_900m, "a stop-limit fills at its limit price");
        (await adapter.GetBalancesAsync())[PaperTradingAdapter.QuoteCurrency].Amount
            .Should().Be(PaperTradingAdapter.InitialQuoteAmount - (50_900m * OrderQuantity));
    }

    [Fact]
    public async Task PlaceOrder_SellStopLimit_TriggeredTick_FillsAtTheLimitPrice()
    {
        // Arrange
        var (marketData, ticker) = CreateConditionalMarketData();
        var adapter = CreateAdapter(marketData);
        await adapter.PlaceOrderAsync(StopLimitOrder(
            BtcUsdt(),
            OrderSide.Sell,
            OrderQuantity,
            trigger: 49_200m,
            limit: 49_100m));

        // Act: the falling market reaches the stop; the sell executes at its limit.
        ticker.OnNext(Tick(49_180m));
        await AwaitSettledAsync(async () => (await adapter.GetOpenOrdersAsync()).Count == 0);

        // Assert
        (await adapter.GetPositionsAsync()).Should().ContainSingle().Which.Side
            .Should().Be(OrderSide.Sell);
        (await adapter.GetBalancesAsync())[PaperTradingAdapter.QuoteCurrency].Amount
            .Should().Be(PaperTradingAdapter.InitialQuoteAmount + (49_100m * OrderQuantity));
    }

    [Fact]
    public async Task PlaceOrder_BuyStopLimit_ImmediatelyTriggeringStop_IsRejectedExplicitly()
    {
        // Arrange: the reference close is 50_000; a buy trigger at or below it
        // would fire immediately — the order is refused, never degraded.
        var (marketData, ticker) = CreateConditionalMarketData();
        var adapter = CreateAdapter(marketData);

        // Act
        var below = async () => await adapter.PlaceOrderAsync(StopLimitOrder(
            BtcUsdt(),
            OrderSide.Buy,
            OrderQuantity,
            trigger: 49_900m,
            limit: 50_000m));
        var at = async () => await adapter.PlaceOrderAsync(StopLimitOrder(
            BtcUsdt(),
            OrderSide.Buy,
            OrderQuantity,
            trigger: ReferenceClose,
            limit: 50_100m));

        // Assert
        await below.Should().ThrowAsync<InvalidOperationException>().WithMessage("*immediately*");
        await at.Should().ThrowAsync<InvalidOperationException>().WithMessage("*immediately*");
        (await adapter.GetOpenOrdersAsync()).Should().BeEmpty();
        ticker.HasObservers.Should().BeFalse("no working conditional stays behind a rejection");
    }

    [Fact]
    public async Task PlaceOrder_SellStopLimit_ImmediatelyTriggeringStop_IsRejectedExplicitly()
    {
        // Arrange: a sell trigger at or above the reference close fires immediately.
        var (marketData, _) = CreateConditionalMarketData();
        var adapter = CreateAdapter(marketData);

        // Act
        var above = async () => await adapter.PlaceOrderAsync(StopLimitOrder(
            BtcUsdt(),
            OrderSide.Sell,
            OrderQuantity,
            trigger: 50_100m,
            limit: 50_000m));
        var at = async () => await adapter.PlaceOrderAsync(StopLimitOrder(
            BtcUsdt(),
            OrderSide.Sell,
            OrderQuantity,
            trigger: ReferenceClose,
            limit: 49_900m));

        // Assert
        await above.Should().ThrowAsync<InvalidOperationException>().WithMessage("*immediately*");
        await at.Should().ThrowAsync<InvalidOperationException>().WithMessage("*immediately*");
        (await adapter.GetOpenOrdersAsync()).Should().BeEmpty();
    }

    #endregion

    #region Trailing Stop Simulation

    [Fact]
    public async Task PlaceOrder_SellTrailingStop_ArmsBelowTheMarketWithoutPortfolioEffect()
    {
        // Arrange
        var (marketData, _) = CreateConditionalMarketData();
        var adapter = CreateAdapter(marketData);

        // Act: the seed trigger sits strictly below the reference close.
        var order = await adapter.PlaceOrderAsync(TrailingStopOrder(
            BtcUsdt(),
            OrderSide.Sell,
            OrderQuantity,
            trigger: 49_500m,
            trail: 500m));

        // Assert
        order.Status.Should().Be(OrderStatus.Pending);
        Dec(order.TrailDistance).Should().Be(500m);
        order.Price!.Value.Should().Be(49_500m, "the trigger seed is the working record's display price");
        (await adapter.GetBalancesAsync()).Should().BeEmpty();
        (await adapter.GetPositionsAsync()).Should().BeEmpty();
        (await adapter.GetOpenOrdersAsync()).Should().ContainSingle().Which.Id.Should().Be(order.Id);
    }

    [Fact]
    public async Task PlaceOrder_SellTrailingStop_RatchetsUpAndFillsAtTheRatchetedTrigger()
    {
        // Arrange
        var (marketData, ticker) = CreateConditionalMarketData();
        var adapter = CreateAdapter(marketData);
        var order = await adapter.PlaceOrderAsync(TrailingStopOrder(
            BtcUsdt(),
            OrderSide.Sell,
            OrderQuantity,
            trigger: 49_500m,
            trail: 500m));

        // Act: the market rises to 50_600 (the trigger drags to 50_100), then a
        // retrace to 50_050 crosses the ratcheted level — the stop fires there.
        ticker.OnNext(Tick(50_600m));
        ticker.OnNext(Tick(50_050m));
        await AwaitSettledAsync(async () => (await adapter.GetOpenOrdersAsync()).Count == 0);

        // Assert: the fill happened at the RATCHETED trigger level, not the seed.
        var again = async () => await adapter.CancelOrderAsync(BtcUsdt(), order.Id);
        await again.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Filled*");
        (await adapter.GetPositionsAsync()).Should().ContainSingle().Which.EntryPrice.Value
            .Should().Be(50_100m, "a trailing stop fills at its ratcheted trigger level");
        (await adapter.GetBalancesAsync())[PaperTradingAdapter.QuoteCurrency].Amount
            .Should().Be(PaperTradingAdapter.InitialQuoteAmount + TrailingSellNotional);
    }

    [Fact]
    public async Task PlaceOrder_BuyTrailingStop_RatchetsDownAndFillsAtTheRatchetedTrigger()
    {
        // Arrange
        var (marketData, ticker) = CreateConditionalMarketData();
        var adapter = CreateAdapter(marketData);

        // Act: the buy stop arms above the market and drags down with it.
        await adapter.PlaceOrderAsync(TrailingStopOrder(
            BtcUsdt(),
            OrderSide.Buy,
            OrderQuantity,
            trigger: 50_500m,
            trail: 500m));
        ticker.OnNext(Tick(49_400m));
        ticker.OnNext(Tick(49_950m));
        await AwaitSettledAsync(async () => (await adapter.GetOpenOrdersAsync()).Count == 0);

        // Assert: the trigger dragged to 49_900; the bounce above it fires there.
        (await adapter.GetPositionsAsync()).Should().ContainSingle().Which.EntryPrice.Value
            .Should().Be(49_900m);
    }

    [Fact]
    public async Task PlaceOrder_SellTrailingStop_WithinTheTrail_KeepsWorking()
    {
        // Arrange
        var (marketData, ticker) = CreateConditionalMarketData();
        var adapter = CreateAdapter(marketData);
        var order = await adapter.PlaceOrderAsync(TrailingStopOrder(
            BtcUsdt(),
            OrderSide.Sell,
            OrderQuantity,
            trigger: 49_500m,
            trail: 500m));

        // Act: a rise to 50_600 ratchets the trigger to 50_100; the retrace to
        // 50_350 stays 250 above the level — shallower than the trail, no fire.
        ticker.OnNext(Tick(50_600m));
        ticker.OnNext(Tick(50_350m));
        await Task.Delay(TickerGraceWait);
        var open = await adapter.GetOpenOrdersAsync();
        var positions = await adapter.GetPositionsAsync();

        // Assert
        open.Should().ContainSingle().Which.Id.Should().Be(order.Id);
        positions.Should().BeEmpty();
    }

    [Fact]
    public async Task PlaceOrder_SellTrailingStop_ImmediatelyTriggeringStop_IsRejectedExplicitly()
    {
        // Arrange
        var (marketData, _) = CreateConditionalMarketData();
        var adapter = CreateAdapter(marketData);

        // Act: a sell trigger at or above the reference close fires immediately.
        var above = async () => await adapter.PlaceOrderAsync(TrailingStopOrder(
            BtcUsdt(),
            OrderSide.Sell,
            OrderQuantity,
            trigger: 50_100m,
            trail: 500m));
        var at = async () => await adapter.PlaceOrderAsync(TrailingStopOrder(
            BtcUsdt(),
            OrderSide.Sell,
            OrderQuantity,
            trigger: ReferenceClose,
            trail: 500m));

        // Assert
        await above.Should().ThrowAsync<InvalidOperationException>().WithMessage("*immediately*");
        await at.Should().ThrowAsync<InvalidOperationException>().WithMessage("*immediately*");
        (await adapter.GetOpenOrdersAsync()).Should().BeEmpty();
    }

    #endregion

    #region OCO Semantics

    [Fact]
    public async Task PlaceOrder_OcoPair_BothLegsWorkOnTheStream()
    {
        // Arrange: a long-exit bracket — the take-profit limit rests above the
        // market, the stop-loss leg arms below it.
        var (marketData, _) = CreateConditionalMarketData();
        var adapter = CreateAdapter(marketData);
        var groupId = OcoId();

        // Act
        await adapter.PlaceOrderAsync(LimitOrder(
            BtcUsdt(),
            OrderSide.Sell,
            OrderQuantity,
            51_000m,
            groupId));
        await adapter.PlaceOrderAsync(StopLimitOrder(
            BtcUsdt(),
            OrderSide.Sell,
            OrderQuantity,
            trigger: 49_000m,
            limit: 48_900m,
            groupId));
        var open = await adapter.GetOpenOrdersAsync();

        // Assert: both legs work, nothing filled, one shared subscription.
        open.Should().HaveCount(2);
        open.All(leg => leg.OcoGroupId == groupId).Should().BeTrue("the legs share the either-or group");
        (await adapter.GetBalancesAsync()).Should().BeEmpty();
        _ = marketData.Received(1).SubscribeTicker(Arg.Any<Symbol>());
    }

    [Fact]
    public async Task PlaceOrder_OcoPair_TriggeredStopLeg_FillsAndCancelsTheSibling()
    {
        // Arrange
        var (marketData, ticker) = CreateConditionalMarketData();
        var adapter = CreateAdapter(marketData);
        var groupId = OcoId();

        var takeProfit = await adapter.PlaceOrderAsync(LimitOrder(
            BtcUsdt(),
            OrderSide.Sell,
            OrderQuantity,
            51_000m,
            groupId));
        var stopLoss = await adapter.PlaceOrderAsync(StopLimitOrder(
            BtcUsdt(),
            OrderSide.Sell,
            OrderQuantity,
            trigger: 49_000m,
            limit: 48_900m,
            groupId));

        // Act: the falling market takes the stop leg out.
        ticker.OnNext(Tick(48_950m));
        await AwaitSettledAsync(async () => (await adapter.GetOpenOrdersAsync()).Count == 0);

        // Assert: one fills, the sibling cancels — no orphans, no double fills.
        var cancelStop = async () => await adapter.CancelOrderAsync(BtcUsdt(), stopLoss.Id);
        await cancelStop.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Filled*");
        var cancelTakeProfit = async () => await adapter.CancelOrderAsync(BtcUsdt(), takeProfit.Id);
        await cancelTakeProfit.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Cancelled*");
        (await adapter.GetBalancesAsync())[PaperTradingAdapter.QuoteCurrency].Amount
            .Should().Be(PaperTradingAdapter.InitialQuoteAmount + (48_900m * OrderQuantity));
    }

    [Fact]
    public async Task PlaceOrder_OcoPair_FilledTakeProfitCancelsTheStopLeg()
    {
        // Arrange
        var (marketData, ticker) = CreateConditionalMarketData();
        var adapter = CreateAdapter(marketData);
        var groupId = OcoId();

        var takeProfit = await adapter.PlaceOrderAsync(LimitOrder(
            BtcUsdt(),
            OrderSide.Sell,
            OrderQuantity,
            51_000m,
            groupId));
        var stopLoss = await adapter.PlaceOrderAsync(StopLimitOrder(
            BtcUsdt(),
            OrderSide.Sell,
            OrderQuantity,
            trigger: 49_000m,
            limit: 48_900m,
            groupId));

        // Act: the rising market reaches the take-profit first.
        ticker.OnNext(Tick(51_050m));
        await AwaitSettledAsync(async () => (await adapter.GetOpenOrdersAsync()).Count == 0);

        // Assert
        var cancelTakeProfit = async () => await adapter.CancelOrderAsync(BtcUsdt(), takeProfit.Id);
        await cancelTakeProfit.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Filled*");
        var cancelStopLoss = async () => await adapter.CancelOrderAsync(BtcUsdt(), stopLoss.Id);
        await cancelStopLoss.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Cancelled*");
        (await adapter.GetBalancesAsync())[PaperTradingAdapter.QuoteCurrency].Amount
            .Should().Be(PaperTradingAdapter.InitialQuoteAmount + (51_000m * OrderQuantity));
    }

    [Fact]
    public async Task CancelOrder_OcoLeg_CancelsTheSiblingBestEffort()
    {
        // Arrange
        var (marketData, ticker) = CreateConditionalMarketData();
        var adapter = CreateAdapter(marketData);
        var groupId = OcoId();

        var takeProfit = await adapter.PlaceOrderAsync(LimitOrder(
            BtcUsdt(),
            OrderSide.Sell,
            OrderQuantity,
            51_000m,
            groupId));
        var stopLoss = await adapter.PlaceOrderAsync(StopLimitOrder(
            BtcUsdt(),
            OrderSide.Sell,
            OrderQuantity,
            trigger: 49_000m,
            limit: 48_900m,
            groupId));

        // Act: the user tears the pair down from the stop leg.
        await adapter.CancelOrderAsync(BtcUsdt(), stopLoss.Id);
        var accounting = async () => await adapter.CancelOrderAsync(BtcUsdt(), stopLoss.Id);

        // Assert: the sibling cannot outlive its cancelled partner.
        await accounting.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Cancelled*");
        (await adapter.GetOpenOrdersAsync()).Should().BeEmpty(
            $"take-profit leg {takeProfit.Id} must cancel with its partner");
        (await adapter.GetBalancesAsync()).Should().BeEmpty("nothing ever filled");
    }

    [Fact]
    public async Task PlaceOrder_OcoSecondLegAfterTerminalPartner_IsRejectedExplicitly()
    {
        // Arrange: the first leg crosses the market and fills at placement
        // (a sell limit at or below the reference close).
        var (marketData, _) = CreateConditionalMarketData();
        var adapter = CreateAdapter(marketData);
        var groupId = OcoId();

        await adapter.PlaceOrderAsync(LimitOrder(
            BtcUsdt(),
            OrderSide.Sell,
            OrderQuantity,
            49_500m,
            groupId));

        // Act: the stop leg joins a group whose partner already filled.
        var act = async () => await adapter.PlaceOrderAsync(StopLimitOrder(
            BtcUsdt(),
            OrderSide.Sell,
            OrderQuantity,
            trigger: 49_000m,
            limit: 48_900m,
            groupId));

        // Assert: an explicit rejection, never a degraded orphan.
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*OCO group*");
        (await adapter.GetOpenOrdersAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task EvaluateTick_OcoBuyTriggerRefusedForBalance_RejectsTheLegAndCancelsTheSibling()
    {
        // Arrange: only BUY fills spend the simulated quote balance, so the
        // refused trigger is a buy stop-limit whose fill would exceed the
        // drained balance. The sibling rests above both the market and the
        // fired tick, so this very tick cannot reach it either.
        var (marketData, ticker) = CreateConditionalMarketData();
        var adapter = CreateAdapter(marketData);
        var groupId = OcoId();
        var drainQuantity = PaperTradingAdapter.InitialQuoteAmount / ReferenceClose;

        await adapter.PlaceOrderAsync(MarketOrder(BtcUsdt(), OrderSide.Buy, drainQuantity));
        var stopLeg = await adapter.PlaceOrderAsync(StopLimitOrder(
            BtcUsdt(),
            OrderSide.Buy,
            OrderQuantity,
            trigger: 51_000m,
            limit: 51_100m,
            groupId));
        var takeProfit = await adapter.PlaceOrderAsync(LimitOrder(
            BtcUsdt(),
            OrderSide.Sell,
            OrderQuantity,
            53_000m,
            groupId));

        // Act: the tick fires the stop leg but never reaches the sibling.
        ticker.OnNext(Tick(51_050m));
        await AwaitSettledAsync(async () => (await adapter.GetOpenOrdersAsync()).Count == 0);

        // Assert: the trigger leg is REJECTED with the refusal reason, the
        // sibling is CANCELLED, and nothing of the pair changed the portfolio.
        var orders = (ConcurrentDictionary<string, Order>)typeof(PaperTradingAdapter)
            .GetField("_orders", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(adapter)!;

        var rejectedLeg = orders[stopLeg.Id];
        rejectedLeg.Status.Should().Be(OrderStatus.Rejected);
        rejectedLeg.RejectReason.Should().Contain("Insufficient",
            "the refused trigger becomes an order status, not a crash");
        orders[takeProfit.Id].Status.Should().Be(OrderStatus.Cancelled,
            "the either-or contract holds even when a leg dies refusing its fill");
        (await adapter.GetBalancesAsync())[PaperTradingAdapter.QuoteCurrency].Amount
            .Should().Be(0m, "the refused leg filled nothing and the sibling filled nothing");
        (await adapter.GetPositionsAsync()).Should().ContainSingle().Which.Quantity.Value
            .Should().Be(drainQuantity, "the position is exactly the balance drain");
    }

    #endregion

    #region Helpers

    /// <summary>Unwraps a nullable price value object to its decimal value.</summary>
    private static decimal Dec(Price? price) => price!.Value.Value;

    private static PaperTradingAdapter CreateAdapter(IMarketDataAdapter marketData) =>
        new(marketData, PaperTradingAdapterTests.CreateFeeSchedules(), NullLogger<PaperTradingAdapter>.Instance);

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
        CreateConditionalMarketData()
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

    /// <summary>A limit order; an OCO group id makes it a working either-or leg.</summary>
    private static OrderRequest LimitOrder(
        Symbol symbol,
        OrderSide side,
        decimal quantity,
        decimal price,
        string? ocoGroupId = null) => new()
        {
            Symbol = symbol,
            Side = side,
            Type = OrderType.Limit,
            Quantity = new Quantity(quantity),
            Price = new Price(price),
            OcoGroupId = ocoGroupId,
        };

    private static OrderRequest StopLimitOrder(
        Symbol symbol,
        OrderSide side,
        decimal quantity,
        decimal trigger,
        decimal limit,
        string? ocoGroupId = null) => new()
        {
            Symbol = symbol,
            Side = side,
            Type = OrderType.StopLimit,
            Quantity = new Quantity(quantity),
            Price = new Price(limit),
            StopPrice = new Price(trigger),
            OcoGroupId = ocoGroupId,
        };

    private static OrderRequest TrailingStopOrder(
        Symbol symbol,
        OrderSide side,
        decimal quantity,
        decimal trigger,
        decimal trail) => new()
        {
            Symbol = symbol,
            Side = side,
            Type = OrderType.TrailingStop,
            Quantity = new Quantity(quantity),
            StopPrice = new Price(trigger),
            TrailDistance = new Price(trail),
        };

    #endregion
}
