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
/// Tests for the paper adapter's simulation reset (T-192): the confirmed
/// reset wipes the whole simulation scope — balances, open positions,
/// working orders, the parked brackets and the conditional/OCO registries —
/// and restarts it at the chosen starting balance; the parked conditionals
/// never arm after the reset (their price-stream subscriptions die with the
/// wipe), a non-positive balance is an explicit refusal (never a default),
/// and the adapter keeps serving afterwards — a fresh conditional arms and
/// fills again on the re-subscribed stream.
/// </summary>
public sealed class PaperResetTests
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

    #region The Wipe

    [Fact]
    public async Task Reset_RestartsTheBalanceAtTheChosenAmount()
    {
        // Arrange: a long from a market buy scattered the balance set
        // (quote remainder + the base asset) across two entries.
        var (marketData, _) = CreateMarketData();
        var adapter = CreateAdapter(marketData);
        await adapter.PlaceOrderAsync(MarketOrder(BtcUsdt(), OrderSide.Buy, OrderQuantity));

        // Act
        await adapter.ResetSimulationAsync(25_000m);

        // Assert: the whole set is replaced by the single seeded quote entry.
        var balances = await adapter.GetBalancesAsync();
        balances.Keys.Should().Equal(PaperTradingAdapter.QuoteCurrency);
        balances[PaperTradingAdapter.QuoteCurrency].Amount.Should().Be(25_000m);
    }

    [Fact]
    public async Task Reset_WipesWorkingOrdersAndOpenPositions()
    {
        // Arrange: an open long and a working reduce-only exit resting above
        // the market.
        var (marketData, _) = CreateMarketData();
        var adapter = CreateAdapter(marketData);
        await adapter.PlaceOrderAsync(MarketOrder(BtcUsdt(), OrderSide.Buy, OrderQuantity));
        await adapter.PlaceOrderAsync(Leg(
            BtcUsdt(),
            OrderSide.Sell,
            OrderQuantity,
            51_000m,
            reduceOnly: true));
        (await adapter.GetOpenOrdersAsync()).Should().ContainSingle("the arrange placed a working order");
        (await adapter.GetPositionsAsync()).Should().ContainSingle("the arrange opened a position");

        // Act
        await adapter.ResetSimulationAsync(25_000m);

        // Assert
        (await adapter.GetOpenOrdersAsync()).Should().BeEmpty();
        (await adapter.GetPositionsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Reset_TheParkedStopNeverArms()
    {
        // Arrange: a stop-loss conditional armed below the market — its
        // trigger evaluation rides the price stream the reset disposes.
        var (marketData, ticker) = CreateMarketData();
        var adapter = CreateAdapter(marketData);
        await adapter.PlaceOrderAsync(MarketOrder(BtcUsdt(), OrderSide.Buy, OrderQuantity));
        await adapter.PlaceOrderAsync(Leg(
            BtcUsdt(),
            OrderSide.Sell,
            OrderQuantity,
            49_000m,
            reduceOnly: true,
            type: OrderType.StopLimit,
            trigger: 49_000m));
        (await adapter.GetOpenOrdersAsync()).Should().ContainSingle("the arrange armed the stop");

        // Act
        await adapter.ResetSimulationAsync(25_000m);
        ticker.OnNext(Tick(48_900m));
        await AwaitSettledAsync(async () => (await adapter.GetPositionsAsync()).Count == 0);

        // Assert: the wiped stop fired nothing — no fill, no phantom position.
        (await adapter.GetOpenOrdersAsync()).Should().BeEmpty();
        (await adapter.GetPositionsAsync()).Should().BeEmpty();
        (await adapter.GetBalancesAsync())[PaperTradingAdapter.QuoteCurrency].Amount
            .Should().Be(25_000m, "the reset's seed stands untouched by the dead trigger");
    }

    [Fact]
    public async Task Reset_TheParkedBracketEntryNeverFills()
    {
        // Arrange: a resting bracket entry below the market — the pair stays
        // parked in the entry's own registry until the entry fills or dies.
        var (marketData, ticker) = CreateMarketData();
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
        entry.Status.Should().Be(OrderStatus.Pending, "the arrange parked the entry away from the market");

        // Act
        await adapter.ResetSimulationAsync(25_000m);
        var restingId = entry.Id;
        ticker.OnNext(Tick(48_900m));
        await AwaitSettledAsync(async () => (await adapter.GetPositionsAsync()).Count == 0);

        // Assert: the parked entry died with the reset — its id is gone from
        // the adapter's store, and the parked pair armed nothing.
        var orders = ReadOrders(adapter);
        orders.ContainsKey(restingId).Should().BeFalse("the reset wiped the parked entry");
        (await adapter.GetOpenOrdersAsync()).Should().BeEmpty();
        (await adapter.GetBalancesAsync())[PaperTradingAdapter.QuoteCurrency].Amount
            .Should().Be(25_000m, "a parked pair touches no balance");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Reset_NonPositiveBalance_IsRefusedAndTouchesNothing(int startingBalance)
    {
        // Arrange: a live long — the refusal must never interpret it away.
        var (marketData, _) = CreateMarketData();
        var adapter = CreateAdapter(marketData);
        await adapter.PlaceOrderAsync(MarketOrder(BtcUsdt(), OrderSide.Buy, OrderQuantity));

        // Act
        var reset = async () => await adapter.ResetSimulationAsync(startingBalance);

        // Assert: the explicit refusal, and the simulation stands exactly as
        // it was (trading safety: never a substituted default).
        await reset.Should().ThrowAsync<ArgumentOutOfRangeException>();
        (await adapter.GetPositionsAsync()).Should().ContainSingle("the refusal left the position standing");
        (await adapter.GetBalancesAsync())[PaperTradingAdapter.QuoteCurrency].Amount
            .Should().Be(PaperTradingAdapter.InitialQuoteAmount - (ReferenceClose * OrderQuantity));
    }

    #endregion

    #region Life After The Reset

    [Fact]
    public async Task Reset_KeepsTheAdapterUsable_AFreshConditionalArmsAndFills()
    {
        // Arrange: the reset disposed the price-stream subscriptions — the
        // next conditional must re-subscribe and arm again on the stream.
        var (marketData, ticker) = CreateMarketData();
        var adapter = CreateAdapter(marketData);
        await adapter.ResetSimulationAsync(60_000m);

        // Act: a fresh short entry stop arms below the market and fills when
        // the market falls through its trigger.
        var stop = await adapter.PlaceOrderAsync(Leg(
            BtcUsdt(),
            OrderSide.Sell,
            OrderQuantity,
            49_000m,
            type: OrderType.StopLimit,
            trigger: 49_000m));
        ticker.OnNext(Tick(48_900m));
        await AwaitSettledAsync(async () => (await adapter.GetPositionsAsync()).Count == 1);

        // Assert: the position opened through the normal fill path.
        var position = (await adapter.GetPositionsAsync()).Should().ContainSingle().Subject;
        position.Side.Should().Be(OrderSide.Sell, "the fresh short entry filled");
        position.Quantity.Value.Should().Be(OrderQuantity);
        var orders = ReadOrders(adapter);
        orders[stop.Id].Status.Should().Be(OrderStatus.Filled);
        (await adapter.GetBalancesAsync())[PaperTradingAdapter.QuoteCurrency].Amount
            .Should().Be(60_000m + (49_000m * OrderQuantity), "the short entry credited the opening notional from the new seed");
    }

    [Fact]
    public async Task Reset_Twice_RestartsAtEachChosenBalance()
    {
        // Arrange
        var (marketData, _) = CreateMarketData();
        var adapter = CreateAdapter(marketData);

        // Act
        await adapter.ResetSimulationAsync(25_000m);
        await adapter.ResetSimulationAsync(60_000m);

        // Assert: the second reset re-seeds from the wiped state, not from
        // the first seed.
        (await adapter.GetBalancesAsync())[PaperTradingAdapter.QuoteCurrency].Amount
            .Should().Be(60_000m);
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

    /// <summary>The take-profit leg of a protective pair: a resting reduce-only limit.</summary>
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

    /// <summary>
    /// The stop-loss leg of a protective pair: a reduce-only stop-limit whose
    /// limit equals its trigger.
    /// </summary>
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
