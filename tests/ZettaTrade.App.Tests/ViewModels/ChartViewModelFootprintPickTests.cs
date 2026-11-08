namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Charting;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Goldens for the chart footprint's pure pick pass (T-122): the workspace
/// symbol leads every pick — exactly the matching symbol's own overlays (a
/// lane holds one account per symbol), every matching order and fill in
/// feed order, and honest absences (nothing when the symbol is unset or
/// carries no row). The symbol comparison is ordinal, exactly the
/// normalized key shape the feed publishes.
/// </summary>
public sealed class ChartViewModelFootprintPickTests
{
    #region Position pick

    /// <summary>The unset workspace symbol owns nothing: null, never an invented stance.</summary>
    [Fact]
    public void ANullSymbol_PicksNothing()
    {
        ChartViewModel.SelectTradingPosition(
                null,
                [Footprint("BTC/USDT", PositionDirection.Long, 90_000m)])
            .Should().BeNull("the chart draws no stance while no workspace symbol is picked");
    }

    /// <summary>The matching symbol's overlay rides the pick: every payload field carried.</summary>
    [Fact]
    public void TheMatchingSymbol_PicksItsOwnOverlay()
    {
        var overlay = new PositionOverlay
        {
            Direction = PositionDirection.Short,
            EntryPrice = 3_500m,
            LatestPrice = 3_300m,
            UnrealizedPnl = new Money(400m, "USDT"),
        };
        var picked = ChartViewModel.SelectTradingPosition(
            "ETH/USDT",
            [
                Footprint("BTC/USDT", PositionDirection.Long, 90_000m),
                new FootprintPosition("ETH/USDT", overlay),
            ]);

        picked.Should().BeSameAs(overlay, "the pick never restates the feed's payload");
    }

    /// <summary>A symbol without an account resolves to no entry line at all.</summary>
    [Fact]
    public void ASymbolWithoutAnAccount_PicksNothing()
    {
        ChartViewModel.SelectTradingPosition(
                "BTC/USDT",
                [Footprint("ETH/USDT", PositionDirection.Short, 3_500m)])
            .Should().BeNull();
    }

    /// <summary>The feed carries normalized symbols; a differently-cased query is no match (ordinal).</summary>
    [Fact]
    public void TheSymbolComparison_IsOrdinal()
    {
        ChartViewModel.SelectTradingPosition(
                "btc/usdt",
                [Footprint("BTC/USDT", PositionDirection.Long, 90_000m)])
            .Should().BeNull("the pick compares against the normalized key, never a loose case");
    }

    #endregion Position pick

    #region Working-order pick

    /// <summary>Every order of the matching symbol, the feed's own order kept — others stay out.</summary>
    [Fact]
    public void TheOrderPick_KeepsTheFeedOrder_AndFiltersOthers()
    {
        var first = new WorkingOrderLine { Side = OrderSide.Buy, Price = 99_000m };
        var second = new WorkingOrderLine { Side = OrderSide.Sell, Price = 101_500m };
        var picked = ChartViewModel.SelectTradingOrders(
            "BTC/USDT",
            [
                new FootprintOrder("BTC/USDT", first),
                new FootprintOrder("ETH/USDT", new WorkingOrderLine { Side = OrderSide.Sell, Price = 3_500m }),
                new FootprintOrder("BTC/USDT", second),
            ]);

        picked.Should().Equal([first, second], "the feed's order is the draw order");
    }

    /// <summary>No matching order and no matching symbol both resolve to an honest empty list.</summary>
    [Fact]
    public void NoWorkingOrder_IsHonestAbsence()
    {
        ChartViewModel.SelectTradingOrders(null, [new FootprintOrder("BTC/USDT", new WorkingOrderLine { Side = OrderSide.Buy, Price = 90_000m })])
            .Should().BeEmpty("an unset symbol draws no lines");
        ChartViewModel.SelectTradingOrders("SOL/USDT", [new FootprintOrder("BTC/USDT", new WorkingOrderLine { Side = OrderSide.Buy, Price = 90_000m })])
            .Should().BeEmpty();
    }

    #endregion Working-order pick

    #region Fill pick

    /// <summary>Every executed fill of the matching symbol in feed (ascending) order — others stay out.</summary>
    [Fact]
    public void TheFillPick_KeepsTheFeedOrder_AndFiltersOthers()
    {
        var first = At(10, 31);
        var second = At(10, 52);
        var picked = ChartViewModel.SelectTradingFills(
            "BTC/USDT",
            [
                new FootprintFill("BTC/USDT", first),
                new FootprintFill("ETH/USDT", At(10, 40)),
                new FootprintFill("BTC/USDT", second),
            ]);

        picked.Should().Equal([first, second]);
    }

    /// <summary>No fill and no symbol both resolve to an honest empty list.</summary>
    [Fact]
    public void NoFill_IsHonestAbsence()
    {
        ChartViewModel.SelectTradingFills(null, [new FootprintFill("BTC/USDT", At(10, 30))])
            .Should().BeEmpty("an unset symbol draws no diamonds");
        ChartViewModel.SelectTradingFills("SOL/USDT", [new FootprintFill("BTC/USDT", At(10, 30))])
            .Should().BeEmpty();
    }

    #endregion Fill pick

    #region Helpers

    private static FootprintPosition Footprint(
        string symbol,
        PositionDirection direction,
        decimal entryPrice) => new(symbol, new PositionOverlay { Direction = direction, EntryPrice = entryPrice });

    private static FillMarker At(int minute, int second) => new()
    {
        Time = new DateTimeOffset(2026, 11, 9, 10, minute, second, TimeSpan.Zero),
        Side = OrderSide.Buy,
        Price = 90_000m,
    };

    #endregion Helpers
}
