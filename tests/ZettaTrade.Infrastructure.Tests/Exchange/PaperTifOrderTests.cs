namespace ZettaTrade.Infrastructure.Tests.Exchange;

using System;
using System.Collections;
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
/// Tests for the paper adapter's honest time-in-force simulation (T-190):
/// IOC and FOK live entirely at the placement moment — an IOC that crosses
/// nothing records the terminal Cancelled and never rests, a FOK that
/// cannot fill whole refuses outright with no order ever existing — and a
/// GTD rests or arms like a GTC and expires against the adapter's own
/// injectable clock, its sweep mirroring the cancel-path teardown (the
/// conditional state, the price stream, the OCO siblings, the parked
/// bracket pair). All market data and the clock come from test-controlled
/// substitutes.
/// </summary>
public sealed class PaperTifOrderTests
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
        new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    private const TimeFrame ReferenceTimeFrame = TimeFrame.H1;

    private static readonly Bar ReferenceBar =
        new(ReferenceBarTime, 49_500m, 50_500m, 49_000m, ReferenceClose, 1_000m);

    #endregion

    #region IOC: the placement-moment cancel

    [Fact]
    public async Task PlaceOrder_IocMarket_FillsWhole()
    {
        // Arrange
        var (marketData, _) = CreateTifMarketData();
        var adapter = CreateAdapter(marketData);

        // Act: a market order crosses the market by definition — IOC's
        // immediacy contract is satisfied whole.
        var order = await adapter.PlaceOrderAsync(MarketOrder(OrderSide.Buy, TimeInForce.IOC));

        // Assert
        order.Status.Should().Be(OrderStatus.Filled);
        (await adapter.GetBalancesAsync())[PaperTradingAdapter.QuoteCurrency].Amount
            .Should().Be(PaperTradingAdapter.InitialQuoteAmount - (ReferenceClose * OrderQuantity));
    }

    [Fact]
    public async Task PlaceOrder_IocLimit_Crossing_FillsWhole()
    {
        // Arrange
        var (marketData, _) = CreateTifMarketData();
        var adapter = CreateAdapter(marketData);

        // Act: a buy limit at or above the reference close crosses at placement.
        var order = await adapter.PlaceOrderAsync(LimitOrder(
            OrderSide.Buy,
            50_100m,
            TimeInForce.IOC));

        // Assert: the fill executes at the limit's own price, exactly like GTC.
        order.Status.Should().Be(OrderStatus.Filled);
        order.Price!.Value.Should().Be(50_100m);
        (await adapter.GetBalancesAsync())[PaperTradingAdapter.QuoteCurrency].Amount
            .Should().Be(PaperTradingAdapter.InitialQuoteAmount - (50_100m * OrderQuantity));
    }

    [Fact]
    public async Task PlaceOrder_IocLimit_Away_IsTerminalCancelledAndNeverRests()
    {
        // Arrange
        var (marketData, ticker) = CreateTifMarketData();
        var adapter = CreateAdapter(marketData);

        // Act: a buy limit below the reference close crosses nothing.
        var order = await adapter.PlaceOrderAsync(LimitOrder(
            OrderSide.Buy,
            49_000m,
            TimeInForce.IOC));

        // Assert: the honest terminal Cancelled is the placement answer —
        // the order never rests, no price stream rides it, the portfolio
        // is untouched, and the history keeps the terminal status.
        order.Status.Should().Be(OrderStatus.Cancelled);
        (await adapter.GetOpenOrdersAsync()).Should().BeEmpty();
        Orders(adapter)[order.Id].Status.Should().Be(OrderStatus.Cancelled);
        (await adapter.GetBalancesAsync()).Should().BeEmpty();
        (await adapter.GetPositionsAsync()).Should().BeEmpty();
        ticker.HasObservers.Should().BeFalse("a never-resting order subscribes no stream");
    }

    [Fact]
    public async Task PlaceOrder_IocStopLimit_Armed_IsTerminalCancelledAndNeverArms()
    {
        // Arrange: the stop arms beyond the market (a buy trigger above the
        // close), so the immediate-trigger refusal does not speak — the TIF
        // contract does: nothing is immediately executable at placement.
        var (marketData, ticker) = CreateTifMarketData();
        var adapter = CreateAdapter(marketData);

        // Act
        var order = await adapter.PlaceOrderAsync(StopLimitOrder(
            OrderSide.Buy,
            trigger: 50_800m,
            limit: 50_900m,
            TimeInForce.IOC));

        // Assert
        order.Status.Should().Be(OrderStatus.Cancelled);
        (await adapter.GetOpenOrdersAsync()).Should().BeEmpty();
        Orders(adapter)[order.Id].Status.Should().Be(OrderStatus.Cancelled);
        ConditionalCount(adapter).Should().Be(0, "an IOC conditional never arms");
        (await adapter.GetBalancesAsync()).Should().BeEmpty();
        ticker.HasObservers.Should().BeFalse("no working conditional stays behind the cancel");
    }

    [Fact]
    public async Task PlaceOrder_IocOcoLimitLeg_Away_IsTerminalCancelledAndTheSiblingStillArms()
    {
        // Arrange
        var (marketData, ticker) = CreateTifMarketData();
        var adapter = CreateAdapter(marketData);
        var groupId = OcoId();

        // Act: the IOC leg ends at placement without ever joining the pair;
        // the sibling placed after it finds no terminal member in the group
        // (the cancelled leg never registered) and arms normally.
        var iocLeg = await adapter.PlaceOrderAsync(LimitOrder(
            OrderSide.Sell,
            53_000m,
            TimeInForce.IOC,
            ocoGroupId: groupId));
        var sibling = await adapter.PlaceOrderAsync(StopLimitOrder(
            OrderSide.Buy,
            trigger: 51_000m,
            limit: 51_100m,
            ocoGroupId: groupId));

        // Assert
        iocLeg.Status.Should().Be(OrderStatus.Cancelled);
        Orders(adapter)[iocLeg.Id].Status.Should().Be(OrderStatus.Cancelled);
        sibling.Status.Should().Be(OrderStatus.Pending, "the sibling keeps working");
        (await adapter.GetOpenOrdersAsync()).Should().ContainSingle().Which.Id.Should().Be(sibling.Id);
        ConditionalCount(adapter).Should().Be(1);
        ticker.HasObservers.Should().BeTrue("the working sibling rides its stream");
    }

    #endregion

    #region FOK: the placement-moment refusal

    [Fact]
    public async Task PlaceOrder_FokMarket_FillsWhole()
    {
        // Arrange
        var (marketData, _) = CreateTifMarketData();
        var adapter = CreateAdapter(marketData);

        // Act
        var order = await adapter.PlaceOrderAsync(MarketOrder(OrderSide.Buy, TimeInForce.FOK));

        // Assert
        order.Status.Should().Be(OrderStatus.Filled);
        (await adapter.GetBalancesAsync())[PaperTradingAdapter.QuoteCurrency].Amount
            .Should().Be(PaperTradingAdapter.InitialQuoteAmount - (ReferenceClose * OrderQuantity));
    }

    [Fact]
    public async Task PlaceOrder_FokLimit_Crossing_FillsWhole()
    {
        // Arrange
        var (marketData, _) = CreateTifMarketData();
        var adapter = CreateAdapter(marketData);

        // Act
        var order = await adapter.PlaceOrderAsync(LimitOrder(
            OrderSide.Buy,
            50_100m,
            TimeInForce.FOK));

        // Assert
        order.Status.Should().Be(OrderStatus.Filled);
        (await adapter.GetBalancesAsync())[PaperTradingAdapter.QuoteCurrency].Amount
            .Should().Be(PaperTradingAdapter.InitialQuoteAmount - (50_100m * OrderQuantity));
    }

    [Fact]
    public async Task PlaceOrder_FokLimit_Away_IsRefusedOutrightWithNoOrderEverExisting()
    {
        // Arrange
        var (marketData, ticker) = CreateTifMarketData();
        var adapter = CreateAdapter(marketData);

        // Act
        var refused = async () => await adapter.PlaceOrderAsync(LimitOrder(
            OrderSide.Buy,
            49_000m,
            TimeInForce.FOK));

        // Assert: the FOK kill is the placement answer — nothing rests,
        // nothing enters the history, the portfolio is untouched.
        await refused.Should().ThrowAsync<InvalidOperationException>().WithMessage("*refused outright*");
        Orders(adapter).Should().BeEmpty("a killed order never exists");
        (await adapter.GetOpenOrdersAsync()).Should().BeEmpty();
        (await adapter.GetBalancesAsync()).Should().BeEmpty();
        ticker.HasObservers.Should().BeFalse();
    }

    [Fact]
    public async Task PlaceOrder_FokStopLimit_Armed_IsRefusedOutrightWithNoOrderEverExisting()
    {
        // Arrange: an armed stop has nothing immediately executable — FOK
        // kills it at placement instead of arming a promise it cannot keep.
        var (marketData, ticker) = CreateTifMarketData();
        var adapter = CreateAdapter(marketData);

        // Act
        var refused = async () => await adapter.PlaceOrderAsync(StopLimitOrder(
            OrderSide.Buy,
            trigger: 50_800m,
            limit: 50_900m,
            TimeInForce.FOK));

        // Assert
        await refused.Should().ThrowAsync<InvalidOperationException>().WithMessage("*refused outright*");
        Orders(adapter).Should().BeEmpty();
        ConditionalCount(adapter).Should().Be(0);
        ticker.HasObservers.Should().BeFalse();
    }

    #endregion

    #region GTD: expiry against the adapter's clock

    [Fact]
    public async Task PlaceOrder_GtdLimit_Crossing_FillsWhole()
    {
        // Arrange
        var (marketData, _) = CreateTifMarketData();
        var adapter = CreateAdapter(marketData);

        // Act
        var order = await adapter.PlaceOrderAsync(LimitOrder(
            OrderSide.Buy,
            50_100m,
            TimeInForce.GTD,
            expiry: DateTimeOffset.UtcNow + TimeSpan.FromHours(1)));

        // Assert: the fill completes before any expiry could speak.
        order.Status.Should().Be(OrderStatus.Filled);
        (await adapter.GetBalancesAsync())[PaperTradingAdapter.QuoteCurrency].Amount
            .Should().Be(PaperTradingAdapter.InitialQuoteAmount - (50_100m * OrderQuantity));
    }

    [Fact]
    public async Task PlaceOrder_GtdLimit_Away_RestsThenExpiresOnTheNextRead()
    {
        // Arrange
        var (marketData, _) = CreateTifMarketData();
        var clock = new SettableTimeProvider { Now = DateTimeOffset.UtcNow };
        var adapter = CreateAdapter(marketData, clock);
        var expiry = clock.Now + TimeSpan.FromHours(1);

        var order = await adapter.PlaceOrderAsync(LimitOrder(
            OrderSide.Buy,
            49_000m,
            TimeInForce.GTD,
            expiry: expiry));
        order.Status.Should().Be(OrderStatus.Pending, "a live GTD rests like a GTC");

        // Act: the clock moves past the expiry; the next read sweeps.
        clock.Now = expiry + TimeSpan.FromMinutes(1);
        var open = await adapter.GetOpenOrdersAsync();

        // Assert: the honest Expired replaces the working status and the
        // history keeps the truthfulness of the record.
        open.Should().BeEmpty();
        var expired = Orders(adapter)[order.Id];
        expired.Status.Should().Be(OrderStatus.Expired);
        expired.Expiry.Should().Be(expiry, "the record keeps its own expiry");
        (await adapter.GetBalancesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task PlaceOrder_GtdStopLimit_ArmsThenExpiresOnTheNextRead()
    {
        // Arrange
        var (marketData, _) = CreateTifMarketData();
        var clock = new SettableTimeProvider { Now = DateTimeOffset.UtcNow };
        var adapter = CreateAdapter(marketData, clock);
        var expiry = clock.Now + TimeSpan.FromHours(1);

        var order = await adapter.PlaceOrderAsync(StopLimitOrder(
            OrderSide.Buy,
            trigger: 50_800m,
            limit: 50_900m,
            TimeInForce.GTD,
            expiry: expiry));
        order.Status.Should().Be(OrderStatus.Pending, "a live GTD arms like a GTC");

        // Act
        clock.Now = expiry + TimeSpan.FromMinutes(1);
        var open = await adapter.GetOpenOrdersAsync();

        // Assert
        open.Should().BeEmpty();
        Orders(adapter)[order.Id].Status.Should().Be(OrderStatus.Expired);
        ConditionalCount(adapter).Should().Be(0, "the expiry teardown mirrors the cancel path");
        (await adapter.GetBalancesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task PlaceOrder_GtdStopLimit_TheExpiryRidesTheTickBeforeAnyTrigger()
    {
        // Arrange: the order arms; the clock moves past its expiry; the next
        // tick is the tick that would have triggered it.
        var (marketData, ticker) = CreateTifMarketData();
        var clock = new SettableTimeProvider { Now = DateTimeOffset.UtcNow };
        var adapter = CreateAdapter(marketData, clock);
        var expiry = clock.Now + TimeSpan.FromHours(1);

        var order = await adapter.PlaceOrderAsync(StopLimitOrder(
            OrderSide.Buy,
            trigger: 50_800m,
            limit: 50_900m,
            TimeInForce.GTD,
            expiry: expiry));

        // Act: advance the clock, then push the triggering tick. The sweep
        // rides the tick's lock BEFORE the trigger evaluation — the honest
        // Expired precedes any fill decision. The settle condition polls
        // the conditional store by reflection: a read would sweep too.
        clock.Now = expiry + TimeSpan.FromMinutes(1);
        ticker.OnNext(Tick(50_820m));
        await AwaitSettledAsync(async () => ConditionalCount(adapter) == 0);

        // Assert
        Orders(adapter)[order.Id].Status.Should().Be(
            OrderStatus.Expired,
            "the expiry precedes the trigger the same tick would have fired");
        (await adapter.GetOpenOrdersAsync()).Should().BeEmpty();
        (await adapter.GetBalancesAsync()).Should().BeEmpty("the expired order filled nothing");
    }

    [Fact]
    public async Task PlaceOrder_GtdPastExpiryAtPlacement_IsTerminalExpiredAndNeverRests()
    {
        // Arrange: the adapter's clock runs ahead of the wall clock the
        // request validation reads — the expiry is future to the validation
        // and already past to the adapter.
        var (marketData, _) = CreateTifMarketData();
        var clock = new SettableTimeProvider { Now = DateTimeOffset.UtcNow + TimeSpan.FromHours(2) };
        var adapter = CreateAdapter(marketData, clock);
        var expiry = DateTimeOffset.UtcNow + TimeSpan.FromHours(1);

        // Act
        var order = await adapter.PlaceOrderAsync(LimitOrder(
            OrderSide.Buy,
            49_000m,
            TimeInForce.GTD,
            expiry: expiry));

        // Assert
        order.Status.Should().Be(OrderStatus.Expired);
        (await adapter.GetOpenOrdersAsync()).Should().BeEmpty();
        Orders(adapter)[order.Id].Status.Should().Be(OrderStatus.Expired);
        (await adapter.GetBalancesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task PlaceOrder_GtdOcoLeg_Expiry_TakesTheWorkingSiblingWithIt()
    {
        // Arrange: an either-or pair — the GTD stop leg expires by the clock,
        // the GTC sibling must not outlive it.
        var (marketData, _) = CreateTifMarketData();
        var clock = new SettableTimeProvider { Now = DateTimeOffset.UtcNow };
        var adapter = CreateAdapter(marketData, clock);
        var expiry = clock.Now + TimeSpan.FromHours(1);
        var groupId = OcoId();

        var stopLeg = await adapter.PlaceOrderAsync(StopLimitOrder(
            OrderSide.Buy,
            trigger: 50_800m,
            limit: 50_900m,
            TimeInForce.GTD,
            expiry: expiry,
            ocoGroupId: groupId));
        var limitLeg = await adapter.PlaceOrderAsync(LimitOrder(
            OrderSide.Sell,
            53_000m,
            ocoGroupId: groupId));
        (await adapter.GetOpenOrdersAsync()).Should().HaveCount(2);

        // Act
        clock.Now = expiry + TimeSpan.FromMinutes(1);
        var open = await adapter.GetOpenOrdersAsync();

        // Assert
        open.Should().BeEmpty();
        Orders(adapter)[stopLeg.Id].Status.Should().Be(OrderStatus.Expired);
        Orders(adapter)[limitLeg.Id].Status.Should().Be(
            OrderStatus.Cancelled,
            "the either-or contract holds for an expiry exactly like a cancel");
        ConditionalCount(adapter).Should().Be(0);
    }

    [Fact]
    public async Task PlaceOrder_GtdBracketEntry_RestingEntryExpiry_TakesTheParkedPairWithIt()
    {
        // Arrange: the bracket entry rests away and parks its exits; the
        // expiry must drop the park — a pair whose entry never filled
        // never arms.
        var (marketData, _) = CreateTifMarketData();
        var clock = new SettableTimeProvider { Now = DateTimeOffset.UtcNow };
        var adapter = CreateAdapter(marketData, clock);
        var expiry = clock.Now + TimeSpan.FromHours(1);

        var entry = await adapter.PlaceOrderAsync(BracketLimitEntry(
            OrderSide.Buy,
            49_000m,
            TimeInForce.GTD,
            expiry));
        entry.Status.Should().Be(OrderStatus.Pending);
        PendingBracketCount(adapter).Should().Be(1, "the resting entry parks its exits");

        // Act
        clock.Now = expiry + TimeSpan.FromMinutes(1);
        var open = await adapter.GetOpenOrdersAsync();

        // Assert
        open.Should().BeEmpty();
        Orders(adapter)[entry.Id].Status.Should().Be(OrderStatus.Expired);
        PendingBracketCount(adapter).Should().Be(0, "the parked pair dies with the expired entry");
        (await adapter.GetBalancesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task PlaceOrder_GtcAway_StillRests_AfterTheClockMoves()
    {
        // Arrange: the regression guard — a GTC carries no expiry, so the
        // sweep's predicate never touches it.
        var (marketData, _) = CreateTifMarketData();
        var clock = new SettableTimeProvider { Now = DateTimeOffset.UtcNow };
        var adapter = CreateAdapter(marketData, clock);

        var order = await adapter.PlaceOrderAsync(LimitOrder(OrderSide.Buy, 49_000m));

        // Act
        clock.Now = clock.Now + TimeSpan.FromHours(2);
        var open = await adapter.GetOpenOrdersAsync();

        // Assert
        order.Expiry.Should().BeNull();
        open.Should().ContainSingle().Which.Id.Should().Be(order.Id);
        Orders(adapter)[order.Id].Status.Should().Be(OrderStatus.Pending);
    }

    #endregion

    #region Helpers

    /// <summary>The test clock: a settable stand-in for the adapter's time.</summary>
    private sealed class SettableTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static PaperTradingAdapter CreateAdapter(IMarketDataAdapter marketData) =>
        new(marketData, PaperTradingAdapterTests.CreateFeeSchedules(), NullLogger<PaperTradingAdapter>.Instance);

    private static PaperTradingAdapter CreateAdapter(IMarketDataAdapter marketData, SettableTimeProvider clock) =>
        new(marketData, PaperTradingAdapterTests.CreateFeeSchedules(), NullLogger<PaperTradingAdapter>.Instance, clock);

    /// <summary>
    /// Market data stub serving the reference bar for every reference-close
    /// request and returning the SAME test-controlled ticker subject to
    /// every subscriber.
    /// </summary>
    private static (IMarketDataAdapter MarketData, Subject<Ticker> Ticker)
        CreateTifMarketData()
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

    private static OrderRequest MarketOrder(OrderSide side, TimeInForce tif = TimeInForce.GTC) => new()
    {
        Symbol = BtcUsdt(),
        Side = side,
        Type = OrderType.Market,
        Quantity = new Quantity(OrderQuantity),
        TimeInForce = tif,
    };

    private static OrderRequest LimitOrder(
        OrderSide side,
        decimal price,
        TimeInForce tif = TimeInForce.GTC,
        DateTimeOffset? expiry = null,
        string? ocoGroupId = null) => new()
        {
            Symbol = BtcUsdt(),
            Side = side,
            Type = OrderType.Limit,
            Quantity = new Quantity(OrderQuantity),
            Price = new Price(price),
            TimeInForce = tif,
            Expiry = expiry,
            OcoGroupId = ocoGroupId,
        };

    private static OrderRequest StopLimitOrder(
        OrderSide side,
        decimal trigger,
        decimal limit,
        TimeInForce tif = TimeInForce.GTC,
        DateTimeOffset? expiry = null,
        string? ocoGroupId = null) => new()
        {
            Symbol = BtcUsdt(),
            Side = side,
            Type = OrderType.StopLimit,
            Quantity = new Quantity(OrderQuantity),
            Price = new Price(limit),
            StopPrice = new Price(trigger),
            TimeInForce = tif,
            Expiry = expiry,
            OcoGroupId = ocoGroupId,
        };

    /// <summary>
    /// A bracket entry (T-186): the buy entry rests away with its protective
    /// exits attached — the take-profit limit and the stop-loss stop-limit
    /// on the opposite side, same quantity, one shared either-or group.
    /// </summary>
    private static OrderRequest BracketLimitEntry(
        OrderSide side,
        decimal price,
        TimeInForce tif,
        DateTimeOffset? expiry)
    {
        var group = OcoId();
        var exitSide = side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;

        return new()
        {
            Symbol = BtcUsdt(),
            Side = side,
            Type = OrderType.Limit,
            Quantity = new Quantity(OrderQuantity),
            Price = new Price(price),
            TimeInForce = tif,
            Expiry = expiry,
            BracketLegs = new BracketLegs
            {
                TakeProfit = new()
                {
                    Symbol = BtcUsdt(),
                    Side = exitSide,
                    Type = OrderType.Limit,
                    Quantity = new Quantity(OrderQuantity),
                    Price = new Price(55_000m),
                    OcoGroupId = group,
                },
                StopLoss = new()
                {
                    Symbol = BtcUsdt(),
                    Side = exitSide,
                    Type = OrderType.StopLimit,
                    Quantity = new Quantity(OrderQuantity),
                    Price = new Price(44_000m),
                    StopPrice = new Price(45_000m),
                    OcoGroupId = group,
                },
            },
        };
    }

    /// <summary>The adapter's order history, read the tests' way.</summary>
    private static ConcurrentDictionary<string, Order> Orders(PaperTradingAdapter adapter) =>
        (ConcurrentDictionary<string, Order>)typeof(PaperTradingAdapter)
            .GetField("_orders", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(adapter)!;

    private static int ConditionalCount(PaperTradingAdapter adapter) =>
        ((ICollection)typeof(PaperTradingAdapter)
            .GetField("_conditionals", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(adapter)!).Count;

    private static int PendingBracketCount(PaperTradingAdapter adapter) =>
        ((ICollection)typeof(PaperTradingAdapter)
            .GetField("_pendingBrackets", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(adapter)!).Count;

    /// <summary>
    /// Bounded wait until the asynchronous tick evaluations settle: polls
    /// the condition on the poll interval, reporting a timeout instead of
    /// a swallowed state.
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

    #endregion
}
