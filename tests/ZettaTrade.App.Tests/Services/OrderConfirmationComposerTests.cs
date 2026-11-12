namespace ZettaTrade.App.Tests.Services;

using FluentAssertions;
using Livery.Localization;
using NSubstitute;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Tests for the shared confirmation composer: the place confirmation must
/// keep the exact MCP-tool argument shape the new-order dialog tests lock
/// in, the cancel confirmation carries the order's own identity facts, the
/// amend confirmation (T-075) extends that identity with the honest
/// quantity and price deltas, and the bulk cancel confirmation (T-076)
/// names the whole set: symbol, count, remaining notional, counting asset
/// and the paper/live tag.
/// </summary>
public sealed class OrderConfirmationComposerTests
{
    private readonly ILocalizationManager _localization = Substitute.For<ILocalizationManager>();

    public OrderConfirmationComposerTests()
    {
        // The fake resolves dynamic messages in a predictable, test-visible
        // form: the key plus the first argument, e.g. "[Order.Buy]".
        _localization.GetString(Arg.Any<string>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}]");
        _localization.GetString(Arg.Any<string>(), Arg.Any<object[]>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}:{FormatArgs(call.ArgAt<object[]>(1))}]");
    }

    [Fact]
    public void ForPlace_LimitOrder_UsesMcpToolArgumentShape()
    {
        // Arrange
        var composer = new OrderConfirmationComposer(_localization);
        var request = new OrderRequest
        {
            Symbol = Symbol.Parse("BTC/USDT"),
            Side = OrderSide.Buy,
            Type = OrderType.Limit,
            Quantity = new Quantity(0.01m),
            Price = new Price(50_000m),
        };

        // Act
        var confirmation = composer.ForPlace(request);

        // Assert: side, type, quantity, symbol, price — in that exact order.
        confirmation.MessageKey.Should().Be("Trading.ConfirmPlaceOrderFormat");
        confirmation.Arguments.Should().HaveCount(5);
        confirmation.Arguments[0].Should().Be("[Order.Buy]");
        confirmation.Arguments[1].Should().Be("[Order.Limit]");
        confirmation.Arguments[2].Should().Be("0.01");
        confirmation.Arguments[3].Should().Be("BTC/USDT");
        confirmation.Arguments[4].Should().Be("50000.00000000");
    }

    [Fact]
    public void ForPlace_MarketOrder_SubstitutesMarketPriceLabel()
    {
        // Arrange
        var composer = new OrderConfirmationComposer(_localization);
        var request = new OrderRequest
        {
            Symbol = Symbol.Parse("BTC/USDT"),
            Side = OrderSide.Sell,
            Type = OrderType.Market,
            Quantity = new Quantity(0.02m),
        };

        // Act
        var confirmation = composer.ForPlace(request);

        // Assert: the market label takes the price slot.
        confirmation.MessageKey.Should().Be("Trading.ConfirmPlaceOrderFormat");
        confirmation.Arguments.Should().HaveCount(5);
        confirmation.Arguments[0].Should().Be("[Order.Sell]");
        confirmation.Arguments[1].Should().Be("[Order.Market]");
        confirmation.Arguments[4].Should().Be("[Trading.PriceMarket]");
    }

    /// <summary>
    /// The detailed summary (T-078): the venue tag line leads, the base
    /// question follows in its exact MCP argument shape, and the economics
    /// lines close — notional against the hard cap with its headroom, and
    /// the estimated fee. The paper tag rides the shared tag key.
    /// </summary>
    [Fact]
    public void ForPlace_WithFacts_ComposesVenueNotionalAndFeeLines()
    {
        var composer = new OrderConfirmationComposer(_localization);
        var request = new OrderRequest
        {
            Symbol = Symbol.Parse("BTC/USDT"),
            Side = OrderSide.Buy,
            Type = OrderType.Limit,
            Quantity = new Quantity(0.01m),
            Price = new Price(50_000m),
        };
        var facts = new OrderPlaceFacts(
            IsPaper: true,
            LiveVenueName: "ignored for paper",
            EstimatedNotionalUsd: 500m,
            EstimatedFee: 0.0025m);

        var confirmation = composer.ForPlace(request, facts);

        confirmation.MessageKey.Should().Be("Trading.ConfirmPlaceOrderDetailedFormat");
        confirmation.Arguments.Should().HaveCount(8);
        confirmation.Arguments[2].Should().Be("0.01");
        confirmation.Arguments[3].Should().Be("BTC/USDT");
        confirmation.Arguments[4].Should().Be("50000.00000000");
        confirmation.Arguments[5].Should().Be("[Positions.BulkTagPaper]");
        confirmation.Arguments[6].Should().Be("[Trading.ConfirmNotionalFormat:500|100000|99500]");
        confirmation.Arguments[7].Should().Be("[Trading.ConfirmFeeFormat:0.0025]");
    }

    /// <summary>
    /// The live venue keeps its own name (never the paper tag), and an
    /// absent estimate renders its honest unknown line — the summary never
    /// fabricates a notional or fee number.
    /// </summary>
    [Fact]
    public void ForPlace_WithUnknownEstimates_RendersUnknownLines()
    {
        var composer = new OrderConfirmationComposer(_localization);
        var request = new OrderRequest
        {
            Symbol = Symbol.Parse("BTC/USDT"),
            Side = OrderSide.Sell,
            Type = OrderType.Market,
            Quantity = new Quantity(0.02m),
        };
        var facts = new OrderPlaceFacts(
            IsPaper: false,
            LiveVenueName: "Binance",
            EstimatedNotionalUsd: null,
            EstimatedFee: null);

        var confirmation = composer.ForPlace(request, facts);

        confirmation.Arguments[5].Should().Be("Binance");
        confirmation.Arguments[6].Should().Be("[Trading.ConfirmNotionalUnknownFormat:100000]");
        confirmation.Arguments[7].Should().Be("[Trading.ConfirmFeeUnknownFormat]");
    }

    /// <summary>
    /// The reverse confirmation names both legs in one mandatory summary:
    /// the reduce-only exit and the opposite-side re-entry — exit side,
    /// exit quantity, symbol, entry side, entry quantity (T-077).
    /// </summary>
    [Fact]
    public void ForReverse_NamesBothLegsOfTheMandatorySummary()
    {
        // Arrange
        var composer = new OrderConfirmationComposer(_localization);
        var exit = new OrderRequest
        {
            Symbol = Symbol.Parse("BTC/USDT"),
            Side = OrderSide.Sell,
            Type = OrderType.Market,
            Quantity = new Quantity(0.01m),
            ReduceOnly = true,
        };
        var entry = new OrderRequest
        {
            Symbol = Symbol.Parse("BTC/USDT"),
            Side = OrderSide.Sell,
            Type = OrderType.Market,
            Quantity = new Quantity(0.01m),
        };

        // Act
        var confirmation = composer.ForReverse(exit, entry);

        // Assert: the reverse template over both legs' facts.
        confirmation.MessageKey.Should().Be("Positions.ConfirmReverseFormat");
        confirmation.Arguments.Should().HaveCount(5);
        confirmation.Arguments[0].Should().Be("[Order.Sell]");
        confirmation.Arguments[1].Should().Be("0.01");
        confirmation.Arguments[2].Should().Be("BTC/USDT");
        confirmation.Arguments[3].Should().Be("[Order.Sell]");
        confirmation.Arguments[4].Should().Be("0.01");
    }

    [Fact]
    public void ForPlace_StopMarketOrder_SummarizesAtTheStopTrigger()
    {
        // Arrange
        var composer = new OrderConfirmationComposer(_localization);
        var request = new OrderRequest
        {
            Symbol = Symbol.Parse("BTC/USDT"),
            Side = OrderSide.Sell,
            Type = OrderType.StopMarket,
            Quantity = new Quantity(0.02m),
            StopPrice = new Price(49_900m),
        };

        // Act
        var confirmation = composer.ForPlace(request);

        // Assert: with no limit price, the trigger anchors the summary —
        // never the market label, which meant nothing for a stop order.
        confirmation.Arguments[1].Should().Be("[Order.StopMarket]");
        confirmation.Arguments[4].Should().Be("49900.00000000");
    }

    [Fact]
    public void ForPlace_TrailingStopOrder_SummarizesAtItsTriggerSeed()
    {
        // Arrange
        var composer = new OrderConfirmationComposer(_localization);
        var request = new OrderRequest
        {
            Symbol = Symbol.Parse("BTC/USDT"),
            Side = OrderSide.Sell,
            Type = OrderType.TrailingStop,
            Quantity = new Quantity(0.02m),
            StopPrice = new Price(49_500m),
            TrailDistance = new Price(500m),
        };

        // Act
        var confirmation = composer.ForPlace(request);

        // Assert
        confirmation.Arguments[1].Should().Be("[Order.TrailingStop]");
        confirmation.Arguments[4].Should().Be("49500.00000000");
    }

    [Fact]
    public void ForCancel_BuildsPositionsTemplateWithOrderIdentity()
    {
        // Arrange
        var composer = new OrderConfirmationComposer(_localization);
        var order = BuildOrder("paper-9", OrderSide.Sell);

        // Act
        var confirmation = composer.ForCancel(order);

        // Assert: the cancellation template for the cancellation of the order's
        // own symbol and id.
        confirmation.MessageKey.Should().Be("Positions.ConfirmCancelFormat");
        confirmation.Arguments.Should().HaveCount(4);
        confirmation.Arguments[0].Should().Be("[Order.Sell]");
        confirmation.Arguments[1].Should().Be("paper-9");
        confirmation.Arguments[2].Should().Be("0.01");
        confirmation.Arguments[3].Should().Be("ETH/USDT");
    }

    [Fact]
    public void ForAmend_PriceEdit_CarriesTheIdentityAndBothEdgesOfTheDelta()
    {
        // Arrange: the amend confirmations extend the cancel identity shape
        // (side, id) with the quantity delta and — when the limit price is
        // part of the edit — the price delta too.
        var composer = new OrderConfirmationComposer(_localization);
        var order = BuildOrder("paper-9", OrderSide.Sell);
        var amended = new OrderRequest
        {
            Symbol = order.Symbol,
            Side = order.Side,
            Type = order.Type,
            Quantity = new Quantity(0.02m),
            Price = new Price(2_500m),
        };

        // Act
        var confirmation = composer.ForAmend(order, amended, priceEdited: true);

        // Assert: side, id, old quantity, new quantity, old price, new price,
        // symbol — in that exact order.
        confirmation.MessageKey.Should().Be("Positions.ConfirmAmend");
        confirmation.Arguments.Should().HaveCount(7);
        confirmation.Arguments[0].Should().Be("[Order.Sell]");
        confirmation.Arguments[1].Should().Be("paper-9");
        confirmation.Arguments[2].Should().Be("0.01");
        confirmation.Arguments[3].Should().Be("0.02");
        confirmation.Arguments[4].Should().Be("2000.00000000");
        confirmation.Arguments[5].Should().Be("2500.00000000");
        confirmation.Arguments[6].Should().Be("ETH/USDT");
    }

    [Fact]
    public void ForAmend_QuantityOnlyEdit_UsesTheCompactTemplate()
    {
        // Arrange: no price edit — the summary keeps the quantity delta only.
        var composer = new OrderConfirmationComposer(_localization);
        var order = BuildOrder("paper-9", OrderSide.Sell);
        var amended = new OrderRequest
        {
            Symbol = order.Symbol,
            Side = order.Side,
            Type = order.Type,
            Quantity = new Quantity(0.02m),
            Price = order.Price,
        };

        // Act
        var confirmation = composer.ForAmend(order, amended, priceEdited: false);

        // Assert: side, id, old quantity, new quantity, symbol.
        confirmation.MessageKey.Should().Be("Positions.ConfirmAmendQuantityOnly");
        confirmation.Arguments.Should().HaveCount(5);
        confirmation.Arguments[0].Should().Be("[Order.Sell]");
        confirmation.Arguments[1].Should().Be("paper-9");
        confirmation.Arguments[2].Should().Be("0.01");
        confirmation.Arguments[3].Should().Be("0.02");
        confirmation.Arguments[4].Should().Be("ETH/USDT");
    }

    [Fact]
    public void ForAmend_WithNulls_Throws()
    {
        var composer = new OrderConfirmationComposer(_localization);
        var order = BuildOrder("paper-9", OrderSide.Sell);
        var amended = new OrderRequest
        {
            Symbol = order.Symbol,
            Side = order.Side,
            Type = order.Type,
            Quantity = new Quantity(0.02m),
            Price = order.Price,
        };

        var actOrder = () => composer.ForAmend(null!, amended, priceEdited: false);
        var actRequest = () => composer.ForAmend(order, null!, priceEdited: false);

        actOrder.Should().Throw<ArgumentNullException>();
        actRequest.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ForPlace_WithNullRequest_Throws()
    {
        var composer = new OrderConfirmationComposer(_localization);
        var act = () => composer.ForPlace(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ForCancel_WithNullOrder_Throws()
    {
        var composer = new OrderConfirmationComposer(_localization);
        var act = () => composer.ForCancel(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ForBulkCancel_ParallelSetsTheCountNotionalQuoteAndPaperTag()
    {
        // Arrange
        var composer = new OrderConfirmationComposer(_localization);

        // Act
        var confirmation = composer.ForBulkCancel(
            Symbol.Parse("BTC/USDT"),
            orderCount: 2,
            totalNotional: 800m,
            isPaper: true,
            liveVenueName: "Live Venue");

        // Assert: symbol, count, remaining notional, quote asset, the
        // localized paper tag — one summary over the whole set.
        confirmation.MessageKey.Should().Be("Positions.ConfirmBulkCancelFormat");
        confirmation.Arguments.Should().HaveCount(5);
        confirmation.Arguments[0].Should().Be("BTC/USDT");
        confirmation.Arguments[1].Should().Be("2");
        confirmation.Arguments[2].Should().Be("800");
        confirmation.Arguments[3].Should().Be("USDT");
        confirmation.Arguments[4].Should().Be("[Positions.BulkTagPaper]");
    }

    [Fact]
    public void ForBulkCancel_LiveVenue_TagsTheVenueNameAndCountsInBaseWhenNoQuote()
    {
        // Arrange: a stock symbol has no split quote asset — the notional
        // counts in the base asset (the quantity's own asset), and a live
        // venue tags itself with its display name.
        var composer = new OrderConfirmationComposer(_localization);

        // Act
        var confirmation = composer.ForBulkCancel(
            Symbol.Parse("AAPL"),
            orderCount: 3,
            totalNotional: 450m,
            isPaper: false,
            liveVenueName: "Demo Live");

        // Assert
        confirmation.MessageKey.Should().Be("Positions.ConfirmBulkCancelFormat");
        confirmation.Arguments[0].Should().Be("AAPL");
        confirmation.Arguments[1].Should().Be("3");
        confirmation.Arguments[3].Should().Be("AAPL");
        confirmation.Arguments[4].Should().Be("Demo Live");
    }

    [Fact]
    public void ForBulkCancel_WithMissingVenueName_Throws()
    {
        var composer = new OrderConfirmationComposer(_localization);
        var act = () => composer.ForBulkCancel(
            Symbol.Parse("BTC/USDT"),
            orderCount: 1,
            totalNotional: 1m,
            isPaper: false,
            liveVenueName: " ");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ForKillSwitch_MixedVenues_TotalsThePlanAndListsEachVenue()
    {
        // Arrange: the paper venue carries one order and one long; the bybit
        // venue one short and no orders — the ONE summary over the whole
        // sweep names every venue's plan.
        var composer = new OrderConfirmationComposer(_localization);
        var venueA = Substitute.For<ITradingAdapter>();
        var planA = new KillSwitchVenuePlan(
            "[Positions.BulkTagPaper]",
            venueA,
            [BuildOrder("order-a", OrderSide.Buy)],
            [BuildPosition("BTC/USDT", OrderSide.Buy, 0.02m)]);
        var venueB = Substitute.For<ITradingAdapter>();
        var planB = new KillSwitchVenuePlan(
            "Bybit Testnet",
            venueB,
            [],
            [BuildPosition("ETH/USDT", OrderSide.Sell, 2m)]);

        // Act
        var confirmation = composer.ForKillSwitch([planA, planB]);

        // Assert: the totals lead, the per-venue listing follows, the
        // positions are named in full (side, quantity, symbol).
        confirmation.MessageKey.Should().Be("Trading.ConfirmKillSwitchFormat");
        confirmation.Arguments.Should().HaveCount(3);
        confirmation.Arguments[0].Should().Be("1");
        confirmation.Arguments[1].Should().Be("2");
        confirmation.Arguments[2].Should().Be(
            "[Trading.KillSwitchPlanVenueFormat:[Positions.BulkTagPaper]|1|1|[Trading.KillSwitchPlanPositionFormat:[Order.Buy]|0.02|BTC/USDT]]"
            + " "
            + "[Trading.KillSwitchPlanVenueFormat:Bybit Testnet|0|1|[Trading.KillSwitchPlanPositionFormat:[Order.Sell]|2|ETH/USDT]]");
    }

    [Fact]
    public void ForKillSwitch_EmptyVenue_TheListingReadsTheLocalizedNone()
    {
        // Arrange: a venue that reads empty still gets its plan line — the
        // sweep's honest nothing rides the same listing.
        var composer = new OrderConfirmationComposer(_localization);
        var venue = Substitute.For<ITradingAdapter>();
        var plan = new KillSwitchVenuePlan("Bybit Testnet", venue, [], []);

        // Act
        var confirmation = composer.ForKillSwitch([plan]);

        // Assert
        confirmation.MessageKey.Should().Be("Trading.ConfirmKillSwitchFormat");
        confirmation.Arguments[0].Should().Be("0");
        confirmation.Arguments[1].Should().Be("0");
        confirmation.Arguments[2].Should().Be("[Trading.KillSwitchPlanVenueFormat:Bybit Testnet|0|0|[Trading.KillSwitchPlanNone]]");
    }

    [Fact]
    public void ForKillSwitch_WithNullPlans_Throws()
    {
        var composer = new OrderConfirmationComposer(_localization);
        var act = () => composer.ForKillSwitch(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    private static Position BuildPosition(string symbol, OrderSide side, decimal quantity) => new()
    {
        Id = $"pos-{symbol}",
        ExchangeId = "paper",
        Symbol = Symbol.Parse(symbol),
        Side = side,
        Quantity = new Quantity(quantity),
        EntryPrice = new Price(50_000m),
        CurrentPrice = new Price(50_000m),
        OpenedAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
    };

    private static Order BuildOrder(string id, OrderSide side) => new()
    {
        Id = id,
        ExchangeId = "paper",
        Symbol = Symbol.Parse("ETH/USDT"),
        Side = side,
        Type = OrderType.Limit,
        Quantity = new Quantity(0.01m),
        Price = new Price(2_000m),
        Status = OrderStatus.Pending,
        TimeInForce = TimeInForce.GTC,
        CreatedAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
    };

    private static string FormatArgs(object[] args) => string.Join("|", args);
}

