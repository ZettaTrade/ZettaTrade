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
/// Tests for the paper adapter's bracket assembly (T-186): the entry places
/// exactly like its standalone shape (market fill, crossing limit fill,
/// resting park) and its attached exits arm AT the entry's fill, against the
/// entry's own fill price. The exits form the shipped either-or pair — the
/// first leg that ends takes the other down — and the arm is all-or-nothing:
/// a refused leg refuses its partner with it, so a half-armed bracket never
/// stays working. All market data (reference bars and the pushed ticker
/// stream) comes from NSubstitute stubs the tests control.
/// </summary>
public sealed class PaperBracketOrderTests
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

    #region Arm On Fill

    [Fact]
    public async Task PlaceOrder_BracketMarketEntry_FillsTheEntryAndArmsThePair()
    {
        // Arrange: a long bracket — the entry buys at the market, the exits
        // sell (the take-profit limit rests above, the stop-loss arms below).
        var (marketData, _) = CreateBracketMarketData();
        var adapter = CreateAdapter(marketData);
        var groupId = OcoId();

        // Act
        var entry = await adapter.PlaceOrderAsync(BracketEntry(
            BtcUsdt(),
            OrderSide.Buy,
            OrderType.Market,
            OrderQuantity,
            entryPrice: null,
            takeProfit: 51_000m,
            stopTrigger: 49_000m,
            stopLimit: 48_900m,
            groupId));
        var open = await adapter.GetOpenOrdersAsync();

        // Assert: the entry filled at the reference close; the pair arms.
        entry.Status.Should().Be(OrderStatus.Filled);
        entry.Price!.Value.Should().Be(ReferenceClose);
        open.Should().HaveCount(2, "the take-profit and the stop-loss arm on the fill");
        open.All(leg => leg.OcoGroupId == groupId).Should().BeTrue("the exits share the either-or group");
        open.Should().Contain(leg => leg.Type == OrderType.Limit && leg.Side == OrderSide.Sell);
        open.Should().Contain(leg => leg.Type == OrderType.StopLimit && leg.Side == OrderSide.Sell);
        (await adapter.GetBalancesAsync())[PaperTradingAdapter.QuoteCurrency].Amount
            .Should().Be(PaperTradingAdapter.InitialQuoteAmount - (ReferenceClose * OrderQuantity));
        (await adapter.GetPositionsAsync()).Should().ContainSingle().Which.Quantity.Value
            .Should().Be(OrderQuantity);
        _ = marketData.Received(1).SubscribeTicker(Arg.Any<Symbol>());
    }

    [Fact]
    public async Task PlaceOrder_BracketCrossingLimitEntry_FillsAndArmsThePair()
    {
        // Arrange: a limit entry at the reference close crosses the market
        // and fills at its own limit price, like a standalone crossing limit.
        var (marketData, _) = CreateBracketMarketData();
        var adapter = CreateAdapter(marketData);
        var groupId = OcoId();

        // Act
        var entry = await adapter.PlaceOrderAsync(BracketEntry(
            BtcUsdt(),
            OrderSide.Buy,
            OrderType.Limit,
            OrderQuantity,
            entryPrice: ReferenceClose,
            takeProfit: 51_000m,
            stopTrigger: 49_000m,
            stopLimit: 48_900m,
            groupId));
        var open = await adapter.GetOpenOrdersAsync();

        // Assert
        entry.Status.Should().Be(OrderStatus.Filled);
        entry.Price!.Value.Should().Be(ReferenceClose, "a crossing limit fills at its own limit price");
        open.Should().HaveCount(2);
        open.All(leg => leg.OcoGroupId == groupId).Should().BeTrue();
    }

    [Fact]
    public async Task PlaceOrder_BracketRestingLimitEntry_ParksThePairWithoutArming()
    {
        // Arrange: a limit entry away from the market rests — the paper sim
        // runs no matching loop, so the pair must stay parked until the
        // entry fills or dies.
        var (marketData, _) = CreateBracketMarketData();
        var adapter = CreateAdapter(marketData);
        var groupId = OcoId();

        // Act
        var entry = await adapter.PlaceOrderAsync(BracketEntry(
            BtcUsdt(),
            OrderSide.Buy,
            OrderType.Limit,
            OrderQuantity,
            entryPrice: 49_000m,
            takeProfit: 51_000m,
            stopTrigger: 49_000m,
            stopLimit: 48_900m,
            groupId));
        var open = await adapter.GetOpenOrdersAsync();

        // Assert: only the entry works; the exits have not armed.
        entry.Status.Should().Be(OrderStatus.Pending);
        open.Should().ContainSingle().Which.Id.Should().Be(entry.Id);
        (await adapter.GetBalancesAsync()).Should().BeEmpty("a resting entry touches nothing");
        _ = marketData.DidNotReceive().SubscribeTicker(Arg.Any<Symbol>());
    }

    [Fact]
    public async Task CancelOrder_BracketRestingEntry_DropsTheParkedPair()
    {
        // Arrange
        var (marketData, _) = CreateBracketMarketData();
        var adapter = CreateAdapter(marketData);
        var groupId = OcoId();

        var entry = await adapter.PlaceOrderAsync(BracketEntry(
            BtcUsdt(),
            OrderSide.Buy,
            OrderType.Limit,
            OrderQuantity,
            entryPrice: 49_000m,
            takeProfit: 51_000m,
            stopTrigger: 49_000m,
            stopLimit: 48_900m,
            groupId));

        // Act: the user tears the resting entry down.
        await adapter.CancelOrderAsync(BtcUsdt(), entry.Id);

        // Assert: the parked pair died with its entry — nothing arms.
        (await adapter.GetOpenOrdersAsync()).Should().BeEmpty();
        (await adapter.GetBalancesAsync()).Should().BeEmpty();
        _ = marketData.DidNotReceive().SubscribeTicker(Arg.Any<Symbol>());
    }

    #endregion

    #region Either-Or On The Stream

    [Fact]
    public async Task PlaceOrder_BracketPair_EitherLegCancelsTheOther()
    {
        // Arrange: a market entry arms the pair; the falling market takes
        // the stop-loss out first.
        var (marketData, ticker) = CreateBracketMarketData();
        var adapter = CreateAdapter(marketData);
        var groupId = OcoId();

        var entry = await adapter.PlaceOrderAsync(BracketEntry(
            BtcUsdt(),
            OrderSide.Buy,
            OrderType.Market,
            OrderQuantity,
            entryPrice: null,
            takeProfit: 51_000m,
            stopTrigger: 49_000m,
            stopLimit: 48_900m,
            groupId));

        // Act
        ticker.OnNext(Tick(48_950m));
        await AwaitSettledAsync(async () => (await adapter.GetOpenOrdersAsync()).Count == 0);

        // Assert: the stop-loss filled, the take-profit cancelled with it,
        // the position closed whole (the exits carry the entry's quantity).
        var orders = (ConcurrentDictionary<string, Order>)typeof(PaperTradingAdapter)
            .GetField("_orders", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(adapter)!;

        orders.Values.Should().Contain(order =>
            order.OcoGroupId == groupId
            && order.Type == OrderType.StopLimit
            && order.Status == OrderStatus.Filled
            && order.Price!.Value == 48_900m);
        orders.Values.Should().Contain(order =>
            order.OcoGroupId == groupId
            && order.Type == OrderType.Limit
            && order.Status == OrderStatus.Cancelled);
        orders[entry.Id].Status.Should().Be(OrderStatus.Filled);
        (await adapter.GetOpenOrdersAsync()).Should().BeEmpty();
        (await adapter.GetBalancesAsync())[PaperTradingAdapter.QuoteCurrency].Amount
            .Should().Be(PaperTradingAdapter.InitialQuoteAmount
                - (ReferenceClose * OrderQuantity)
                + (48_900m * OrderQuantity));
        (await adapter.GetPositionsAsync()).Should().BeEmpty("the stop-loss closed the whole entry");
    }

    #endregion

    #region All-Or-Nothing Arm

    [Fact]
    public async Task PlaceOrder_BracketStopLossRefusedAtArm_TheWorkingTakeProfitCancelsWithIt()
    {
        // Arrange: the stop-loss trigger sits on the WRONG side of the
        // market for its exit (a sell stop above the reference close), so
        // the arming check refuses it — the shape still validates (the
        // intrinsic matrix has no market reference: the sell stop-limit's
        // own limit stays under its trigger), the arm does not.
        var (marketData, _) = CreateBracketMarketData();
        var adapter = CreateAdapter(marketData);
        var groupId = OcoId();

        // Act
        var entry = await adapter.PlaceOrderAsync(BracketEntry(
            BtcUsdt(),
            OrderSide.Buy,
            OrderType.Market,
            OrderQuantity,
            entryPrice: null,
            takeProfit: 51_000m,
            stopTrigger: 51_000m,
            stopLimit: 50_900m,
            groupId));
        var open = await adapter.GetOpenOrdersAsync();

        // Assert: the entry stays filled (its outcome is real), but the
        // protection is all-or-nothing — the take-profit the arm placed
        // first cancels with the refused stop-loss.
        entry.Status.Should().Be(OrderStatus.Filled);
        open.Should().BeEmpty("a half-armed bracket never stays working");
        var orders = (ConcurrentDictionary<string, Order>)typeof(PaperTradingAdapter)
            .GetField("_orders", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(adapter)!;

        orders.Values.Should().Contain(order =>
            order.OcoGroupId == groupId
            && order.Type == OrderType.Limit
            && order.Status == OrderStatus.Cancelled);
        (await adapter.GetPositionsAsync()).Should().ContainSingle("the entry's fill stands");
    }

    #endregion

    #region Atomic Refusal Matrix

    [Fact]
    public async Task PlaceOrder_BracketInvalidShape_IsRefusedBeforeAnythingRegisters()
    {
        // Arrange: a take-profit leg that is not a limit is an unparsable
        // bracket — the request's own validation refuses it whole.
        var (marketData, _) = CreateBracketMarketData();
        var adapter = CreateAdapter(marketData);
        var groupId = OcoId();

        var broken = new BracketLegs
        {
            TakeProfit = new OrderRequest
            {
                Symbol = BtcUsdt(),
                Side = OrderSide.Sell,
                Type = OrderType.StopMarket,
                Quantity = new Quantity(OrderQuantity),
                StopPrice = new Price(51_000m),
                OcoGroupId = groupId,
            },
            StopLoss = StopLimitLeg(BtcUsdt(), OrderSide.Sell, OrderQuantity, 49_000m, 48_900m, groupId),
        };
        var request = new OrderRequest
        {
            Symbol = BtcUsdt(),
            Side = OrderSide.Buy,
            Type = OrderType.Market,
            Quantity = new Quantity(OrderQuantity),
            BracketLegs = broken,
        };

        // Act
        var place = async () => await adapter.PlaceOrderAsync(request);

        // Assert
        await place.Should().ThrowAsync<InvalidOperationException>().WithMessage("*take-profit*Limit*");
        (await adapter.GetOpenOrdersAsync()).Should().BeEmpty();
        (await adapter.GetBalancesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task PlaceOrder_BracketMarketEntryRefusedForBalance_ArmsNothing()
    {
        // Arrange: the quote balance is drained first, so the bracket
        // entry's fill is refused for the portfolio effect.
        var (marketData, _) = CreateBracketMarketData();
        var adapter = CreateAdapter(marketData);
        var groupId = OcoId();
        var drainQuantity = PaperTradingAdapter.InitialQuoteAmount / ReferenceClose;

        await adapter.PlaceOrderAsync(MarketOrder(BtcUsdt(), OrderSide.Buy, drainQuantity));

        // Act
        var place = async () => await adapter.PlaceOrderAsync(BracketEntry(
            BtcUsdt(),
            OrderSide.Buy,
            OrderType.Market,
            OrderQuantity,
            entryPrice: null,
            takeProfit: 51_000m,
            stopTrigger: 49_000m,
            stopLimit: 48_900m,
            groupId));

        // Assert: the refused fill throws out of the placement; the exits
        // never armed (the arm rides the fill).
        await place.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Insufficient*");
        (await adapter.GetOpenOrdersAsync()).Should().BeEmpty();
    }

    #endregion

    #region Helpers

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
        CreateBracketMarketData()
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

    /// <summary>
    /// A bracket entry request: the entry shape (market or limit) carrying
    /// the attached exits — a take-profit limit and a stop-loss stop-limit
    /// on the opposite side, the entry's quantity, one shared group id.
    /// </summary>
    private static OrderRequest BracketEntry(
        Symbol symbol,
        OrderSide side,
        OrderType entryType,
        decimal quantity,
        decimal? entryPrice,
        decimal takeProfit,
        decimal stopTrigger,
        decimal stopLimit,
        string groupId)
    {
        var exitSide = side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
        var entry = entryType == OrderType.Market
            ? MarketOrder(symbol, side, quantity)
            : LimitOrder(symbol, side, quantity, entryPrice!.Value);

        return entry with
        {
            BracketLegs = new BracketLegs
            {
                TakeProfit = LimitLeg(symbol, exitSide, quantity, takeProfit, groupId),
                StopLoss = StopLimitLeg(symbol, exitSide, quantity, stopTrigger, stopLimit, groupId),
            },
        };
    }

    private static OrderRequest MarketOrder(Symbol symbol, OrderSide side, decimal quantity) => new()
    {
        Symbol = symbol,
        Side = side,
        Type = OrderType.Market,
        Quantity = new Quantity(quantity),
    };

    private static OrderRequest LimitOrder(
        Symbol symbol,
        OrderSide side,
        decimal quantity,
        decimal price) => new()
        {
            Symbol = symbol,
            Side = side,
            Type = OrderType.Limit,
            Quantity = new Quantity(quantity),
            Price = new Price(price),
        };

    private static OrderRequest LimitLeg(
        Symbol symbol,
        OrderSide side,
        decimal quantity,
        decimal price,
        string ocoGroupId) => new()
        {
            Symbol = symbol,
            Side = side,
            Type = OrderType.Limit,
            Quantity = new Quantity(quantity),
            Price = new Price(price),
            OcoGroupId = ocoGroupId,
        };

    private static OrderRequest StopLimitLeg(
        Symbol symbol,
        OrderSide side,
        decimal quantity,
        decimal trigger,
        decimal limit,
        string ocoGroupId) => new()
        {
            Symbol = symbol,
            Side = side,
            Type = OrderType.StopLimit,
            Quantity = new Quantity(quantity),
            Price = new Price(limit),
            StopPrice = new Price(trigger),
            OcoGroupId = ocoGroupId,
        };

    #endregion
}
