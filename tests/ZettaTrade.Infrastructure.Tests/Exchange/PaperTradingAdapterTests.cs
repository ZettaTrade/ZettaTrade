namespace ZettaTrade.Infrastructure.Tests.Exchange;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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

    #region T-051 Ledger Constants

    /// <summary>Bounded wait for a cancel that must abort immediately.</summary>
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(10);

    /// <summary>Close of the bar the reducing (partial-close) fill executes at.</summary>
    private const decimal ReduceFillPrice = 51_000m;

    /// <summary>Notional of that reducing fill (0.1 base at 51_000).</summary>
    private const decimal ReduceFillNotional = 5_100m;

    /// <summary>Close of the bar the second extending fill executes at.</summary>
    private const decimal ExtensionFillPrice = 52_000m;

    /// <summary>Bars in the fake 24-hour H1 history (one per hour).</summary>
    private const int HistoryBarCount = 25;

    /// <summary>Close of the OLDEST fake history bar (the anchor).</summary>
    private const decimal HistoryBarsBaseClose = 46_000m;

    /// <summary>Close step between consecutive fake history bars.</summary>
    private const decimal HistoryCloseStep = 100m;

    /// <summary>Close of the NEWEST fake history bar (<see cref="HistoryBarsBaseClose"/> + 24 steps).</summary>
    private const decimal NewestHistoryClose = 48_400m;

    #endregion

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

    #region T-051 Ledger Rules

    /// <summary>
    /// BUG-005: a reducing fill closes part of the position at the fill price
    /// and must never re-average the remaining size — the pre-fix branch fed
    /// the fill into the weighted average with the WRONG denominator and
    /// inflated the entry of what is left.
    /// </summary>
    [Fact]
    public async Task PlaceOrder_PartialClose_KeepsTheAverageEntryOfTheRemainingSize()
    {
        var marketData = Substitute.For<IMarketDataAdapter>();
        marketData.GetBarsAsync(
                Arg.Any<Symbol>(),
                Arg.Any<TimeFrame>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(new[] { OpeningBar }, new[] { NewCloseBar(ReduceFillPrice) });
        var adapter = CreateAdapter(marketData);

        // Act: long 0.2 @ 50_000, then sell half @ 51_000.
        await adapter.PlaceOrderAsync(MarketOrder(BtcUsdt(), OrderSide.Buy, OrderQuantity));
        await adapter.PlaceOrderAsync(MarketOrder(BtcUsdt(), OrderSide.Sell, OrderQuantity / 2m));

        // Assert: the remaining 0.1 long keeps the 50_000 entry (pre-fix: 151_000).
        var remaining = (await adapter.GetPositionsAsync()).Should().ContainSingle().Which;
        remaining.Side.Should().Be(OrderSide.Buy);
        remaining.Quantity.Value.Should().Be(OrderQuantity / 2m);
        remaining.EntryPrice.Value.Should().Be(
            ReferenceClose, "a partial close never re-averages the remaining size");
        remaining.CurrentPrice.Value.Should().Be(ReduceFillPrice);

        (await adapter.GetBalancesAsync())[PaperTradingAdapter.QuoteCurrency].Amount
            .Should().Be(PaperTradingAdapter.InitialQuoteAmount - MarketNotional + ReduceFillNotional);
    }

    /// <summary>
    /// The companion invariance for the SAME branch: genuinely extending fills
    /// DO keep streaming the entry toward a weighted average over the whole
    /// new size.
    /// </summary>
    [Fact]
    public async Task PlaceOrder_ExtendingFills_WeightTheAverageAcrossTheWholeSize()
    {
        var marketData = Substitute.For<IMarketDataAdapter>();
        marketData.GetBarsAsync(
                Arg.Any<Symbol>(),
                Arg.Any<TimeFrame>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(new[] { OpeningBar }, new[] { NewCloseBar(ExtensionFillPrice) });
        var adapter = CreateAdapter(marketData);

        await adapter.PlaceOrderAsync(MarketOrder(BtcUsdt(), OrderSide.Buy, OrderQuantity));
        await adapter.PlaceOrderAsync(MarketOrder(BtcUsdt(), OrderSide.Buy, OrderQuantity));

        var position = (await adapter.GetPositionsAsync()).Should().ContainSingle().Which;
        position.Quantity.Value.Should().Be(OrderQuantity * 2m);
        position.EntryPrice.Value.Should().Be(
            (ReferenceClose + ExtensionFillPrice) / 2m,
            "two equal-size extensions weigh their fills equally");
    }

    /// <summary>
    /// BUG-006, ascending backend: Binance-style klines are oldest-first, so a
    /// fixed <c>limit: 1</c> from <c>now - 24h</c> paged back the OLDEST bar in
    /// the window — a ~24-hour-old close. The adapter must fetch the whole
    /// window and select the reference close BY TIME.
    /// </summary>
    [Fact]
    public async Task PlaceOrder_AscendingHistory_UsesTheNewestCloseNotTheOldest()
    {
        var marketData = CreateBackend(BuildHistory(), newestFirst: false);
        var adapter = CreateAdapter(marketData);

        // Act
        var order = await adapter.PlaceOrderAsync(MarketOrder(BtcUsdt(), OrderSide.Buy, OrderQuantity));

        // Assert: the fill rides the NEWEST close (pre-fix: the oldest, 46_000).
        order.Price!.Value.Should().Be(NewestHistoryClose, "the reference close is the newest bar's close whatever the page order");
        (await adapter.GetPositionsAsync()).Should().ContainSingle().Which.EntryPrice.Value
            .Should().Be(NewestHistoryClose);

        // The window is fetched in full, not page-capped to a single bar.
        await marketData.Received(1).GetBarsAsync(
            Arg.Any<Symbol>(),
            Arg.Any<TimeFrame>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Is<int>(limit => limit >= HistoryBarCount),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// BUG-006, descending backend: the same contract on a Bybit-style
    /// newest-first history — still the newest close, never a list-position
    /// guess.
    /// </summary>
    [Fact]
    public async Task PlaceOrder_DescendingHistory_UsesTheNewestCloseToo()
    {
        var marketData = CreateBackend(BuildHistory(), newestFirst: true);
        var adapter = CreateAdapter(marketData);

        // Act
        var order = await adapter.PlaceOrderAsync(MarketOrder(BtcUsdt(), OrderSide.Buy, OrderQuantity));

        // Assert
        order.Price!.Value.Should().Be(NewestHistoryClose, "the selection is by the bar's time, never by list position");
    }

    /// <summary>
    /// BUG-033: a fill rejected for insufficient balance leaves NO phantom
    /// Filled record — the portfolio applies before anything is recorded, and
    /// the adapter keeps trading normally afterwards.
    /// </summary>
    [Fact]
    public async Task PlaceOrder_BalanceRejection_LeavesNoPhantomFilledRecord()
    {
        var adapter = CreateAdapter(CreateMarketData());
        var hugeQuantity = (PaperTradingAdapter.InitialQuoteAmount / ReferenceClose) * ExcessiveQuantityFactor;

        var act = async () => await adapter.PlaceOrderAsync(MarketOrder(BtcUsdt(), OrderSide.Buy, hugeQuantity));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Insufficient*");

        // The private order store is the only surface that shows a phantom
        // Filled record (fill orders never reappear in visible lists): it must
        // stay empty after the rejection.
        var orders = (ConcurrentDictionary<string, Order>)typeof(PaperTradingAdapter)
            .GetField("_orders", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(adapter)!;
        orders.Count.Should().Be(0, "a balance-rejected fill must not leave a phantom Filled record");

        // And the adapter is still fully usable.
        var followUp = await adapter.PlaceOrderAsync(MarketOrder(BtcUsdt(), OrderSide.Buy, OrderQuantity));
        followUp.Status.Should().Be(OrderStatus.Filled);
        (await adapter.GetBalancesAsync())[PaperTradingAdapter.QuoteCurrency].Amount
            .Should().Be(PaperTradingAdapter.InitialQuoteAmount - MarketNotional);
    }

    /// <summary>
    /// BUG-034: crossing zero opens a FRESH opposite position, which carries
    /// its own fill-time stamp — never the closed position's OpenedAt.
    /// </summary>
    [Fact]
    public async Task PlaceOrder_CrossingZeroFill_StampsTheFreshPositionWithItsOwnFillTime()
    {
        var adapter = CreateAdapter(CreateMarketData());

        var buy = await adapter.PlaceOrderAsync(MarketOrder(BtcUsdt(), OrderSide.Buy, OrderQuantity));
        (await adapter.GetPositionsAsync()).Should().ContainSingle().Which.OpenedAt
            .Should().Be(buy.CreatedAt);

        // Act: sell twice the size — the position crosses zero into a short.
        var sell = await adapter.PlaceOrderAsync(MarketOrder(BtcUsdt(), OrderSide.Sell, OrderQuantity * 2m));

        // Assert
        var crossed = (await adapter.GetPositionsAsync()).Should().ContainSingle().Which;
        crossed.Side.Should().Be(OrderSide.Sell);
        crossed.Quantity.Value.Should().Be(OrderQuantity);
        crossed.EntryPrice.Value.Should().Be(ReferenceClose);
        crossed.OpenedAt.Should().Be(
            sell.CreatedAt,
            "the fresh opposite position is stamped at its own fill, never at the closed position's open");
    }

    /// <summary>
    /// BUG-035: a cancel waiting on the trading lock honours the caller's
    /// cancellation — a pre-cancelled token aborts the wait immediately even
    /// while a wedged holder keeps the lock, and the wedged release normal
    /// cancelling still works.
    /// </summary>
    [Fact]
    public async Task CancelOrder_WithACancelledToken_AbortsTheWaitInsteadOfQueueingBehindAWedgedLock()
    {
        var adapter = CreateAdapter(CreateMarketData());
        var order = await adapter.PlaceOrderAsync(
            LimitOrder(BtcUsdt(), OrderSide.Buy, OrderQuantity, RestingBuyLimitPrice));

        // Wedge the trading lock exactly where the pre-fix cancel waited with
        // no token at all.
        var tradingLock = (SemaphoreSlim)typeof(PaperTradingAdapter)
            .GetField("_tradingLock", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(adapter)!;
        await tradingLock.WaitAsync();
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var act = () => adapter.CancelOrderAsync(BtcUsdt(), order.Id, cts.Token).WaitAsync(EventWait);
            await act.Should().ThrowAsync<OperationCanceledException>(
                "the caller's cancellation aborts the lock wait immediately");
        }
        finally
        {
            tradingLock.Release();
        }

        // The untouched pending order survives, and cancelling still works.
        await adapter.CancelOrderAsync(BtcUsdt(), order.Id);
        (await adapter.GetOpenOrdersAsync()).Should().BeEmpty();
    }

    #endregion

    #region Helpers

    private static PaperTradingAdapter CreateAdapter(IMarketDataAdapter marketData) =>
        new(marketData, CreateFeeSchedules(), NullLogger<PaperTradingAdapter>.Instance);

    /// <summary>
    /// The fee registry the harness builds on: an unconfigured settings
    /// snapshot means the shipped paper schedule answers (the market-fill
    /// commission goldens in this file pin those numbers).
    /// </summary>
    internal static FeeScheduleService CreateFeeSchedules()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Changes.Returns(System.Reactive.Linq.Observable.Empty<SettingsChange>());
        return new(settings, NullLogger<FeeScheduleService>.Instance);
    }

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

    /// <summary>The reference bar of the OPENING fill (the entry build moment).</summary>
    private static readonly Bar OpeningBar = ReferenceBar;

    /// <summary>A reference bar with another close (the next fill's price source).</summary>
    private static Bar NewCloseBar(decimal close) =>
        new(ReferenceBarTime, close, close, close, close, 1_000m);

    /// <summary>The fake 24-hour H1 history: hourly bars, closes rising per hour.</summary>
    private static IReadOnlyList<Bar> BuildHistory() =>
        Enumerable.Range(0, HistoryBarCount)
            .Select(i => NewCloseBar(HistoryBarsBaseClose + (i * HistoryCloseStep)) with
            {
                Time = ReferenceBarTime.AddHours(i),
            })
            .ToList();

    /// <summary>
    /// A paging-faithful history backend: serves the given bars in the given
    /// page order, capped at the caller's <c>limit</c> — the ascending
    /// Binance-klines shape or the descending Bybit/Demo shape. The from-window
    /// is deliberately NOT modeled: the fixture's bars are stale relative to
    /// the live clock anyway, and the tested contract is the ordering-vs-limit
    /// interplay the reference-close selection must survive (BUG-006).
    /// </summary>
    private static IMarketDataAdapter CreateBackend(IReadOnlyList<Bar> bars, bool newestFirst)
    {
        var marketData = Substitute.For<IMarketDataAdapter>();
        marketData.GetBarsAsync(
                Arg.Any<Symbol>(),
                Arg.Any<TimeFrame>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var limit = call.ArgAt<int>(4);
                var paged = bars.Take(limit);
                return (IReadOnlyList<Bar>)(newestFirst
                    ? paged.OrderByDescending(bar => bar.Time).ToList()
                    : paged.OrderBy(bar => bar.Time).ToList());
            });

        return marketData;
    }

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
