namespace ZettaTrade.UI.Tests.Charting;

using System;
using System.Collections.Generic;
using Avalonia;
using FluentAssertions;
using Xunit;
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.UI.Charting;

/// <summary>
/// Pins for the footprint's pure placement math (T-122): the price levels
/// the entry and working-order lines anchor at through the shared price
/// scale (an out-of-range level resolves to NO line — an honest absence,
/// never a clamp), the floating tag's clamped headroom band, and the fill
/// markers' slot resolution ([open, next open) containment, the
/// time-axis' own interval conventions). Pure facts — the canvas itself is
/// UIA-invisible.
/// </summary>
public sealed class ChartFootprintLayoutTests
{
    /// <summary>Plot rect used across the facts (pixels).</summary>
    private static readonly Rect Plot = new(0, 0, 1200.0, 600.0);

    /// <summary>The plotted price window the facts share: 90 down to 110.</summary>
    private const decimal MinPrice = 90m;

    private const decimal Range = 20m;

    #region Price levels

    /// <summary>An in-range level maps through the candles' own scale — shared math, no re-scaling.</summary>
    [Fact]
    public void AnInrangedLevel_MapsThroughTheSharedScale()
    {
        ChartFootprintLayout.LevelY(Plot, 100m, MinPrice, Range)
            .Should().Be(ChartPriceScale.PriceToY(Plot, 100m, MinPrice, Range));
        ChartFootprintLayout.LevelY(Plot, MinPrice, MinPrice, Range)
            .Should().NotBeNull("the window's own boundary still anchors a level");
        ChartFootprintLayout.LevelY(Plot, MinPrice + Range, MinPrice, Range)
            .Should().NotBeNull("the top boundary still anchors a level");
    }

    /// <summary>A level outside the plotted range resolves to nothing — never clamped, never extrapolated.</summary>
    [Fact]
    public void AnOutOfRangedLevel_IsAnHonestAbsence()
    {
        ChartFootprintLayout.LevelY(Plot, 89.90m, MinPrice, Range)
            .Should().BeNull("a level below the window owns no visible boundary");
        ChartFootprintLayout.LevelY(Plot, 110.10m, MinPrice, Range)
            .Should().BeNull("a level above the window owns no visible boundary");
    }

    /// <summary>An in-band tag snaps to the price's own mapped position.</summary>
    [Fact]
    public void AnInBandTag_RidesItsOwnPrice()
    {
        ChartFootprintLayout.TagY(Plot, 100m, MinPrice, Range)
            .Should().Be(ChartPriceScale.PriceToY(Plot, 100m, MinPrice, Range));
    }

    /// <summary>A tag whose price left the window pins at the visible edge, inside the headroom band.</summary>
    [Fact]
    public void AnOutrangedTag_ClampsIntoTheHeadroomBand()
    {
        ChartFootprintLayout.TagY(Plot, 150m, MinPrice, Range)
            .Should().Be(Plot.Top + ChartPriceScale.HeadroomPx, "a fast-run-up tag stays visible at the top edge");
        ChartFootprintLayout.TagY(Plot, 80m, MinPrice, Range)
            .Should().Be(Plot.Bottom - ChartPriceScale.HeadroomPx, "a fast-run-down tag stays visible at the bottom edge");
    }

    #endregion Price levels

    #region Fill resolution

    /// <summary>The window the resolve facts share: five hourly bars, 14:00 the last open.</summary>
    private static readonly IReadOnlyList<Bar> Window =
    [
        Bar(10, 0),
        Bar(11, 0),
        Bar(12, 0),
        Bar(13, 0),
        Bar(14, 0),
    ];

    /// <summary>Empty inputs resolve to nothing — never a fabricated anchor.</summary>
    [Fact]
    public void EmptyInputs_ResolveToNothing()
    {
        ChartFootprintLayout.Resolve([], Window).Should().BeEmpty();
        ChartFootprintLayout.Resolve([At(10, 30)], []).Should().BeEmpty();
    }

    /// <summary>A fill before the window's first open resolves to nothing — never a neighbor's slot.</summary>
    [Fact]
    public void AFillBeforeTheWindow_Skips()
    {
        ChartFootprintLayout.Resolve([At(9, 30)], Window).Should().BeEmpty();
    }

    /// <summary>A fill inside a candle's own [open, next open) interval anchors at that candle's slot.</summary>
    [Fact]
    public void AContainedFill_AnchorsAtItsOwnSlot()
    {
        var hits = ChartFootprintLayout.Resolve([At(11, 30)], Window);

        hits.Should().ContainSingle();
        hits[0].Slot.Should().Be(1, "11:30 falls inside [11:00, 12:00)");
        hits[0].Price.Should().Be(11_030m);
        hits[0].Side.Should().Be(OrderSide.Sell);
    }

    /// <summary>Exactly the next open is the next candle's own start, never the previous slot's tail.</summary>
    [Fact]
    public void TheOpenBoundary_BelongsToTheNextCandle()
    {
        chartResolution(12, 0).Should().ContainSingle().Which.Slot.Should().Be(2);
    }

    /// <summary>Two fills inside one candle resolve to two hits, marker order kept — the draw stacks them.</summary>
    [Fact]
    public void TwoFillsInOneCandle_ResolvesTwoHits()
    {
        var hits = ChartFootprintLayout.Resolve([At(11, 10), At(11, 50)], Window);

        hits.Select(static h => h.Slot).Should().Equal(1, 1);
        hits.Select(static h => h.Price).Should().Equal(11_010m, 11_050m);
    }

    /// <summary>The last displayed bar borrows the previous span: inside the borrow anchors there, past it resolves nothing (the candle is forming).</summary>
    [Fact]
    public void TheLastBarsBorrowedInterval_KeepsTheTimeAxisConvention()
    {
        chartResolution(14, 30).Should().ContainSingle().Which.Slot.Should().Be(4);
        ChartFootprintLayout.Resolve([At(15, 30)], Window).Should().BeEmpty("the fill's own candle is not in the closed series");
        ChartFootprintLayout.Resolve([At(15, 0)], Window).Should().BeEmpty("the interval is [open, borrowed end)");
    }

    /// <summary>A single-bar window cannot disprove any later time: its one interval owns every time from the open on.</summary>
    [Fact]
    public void ASingleBarWindow_OwnsEveryTimeFromItsOpen()
    {
        ChartFootprintLayout.Resolve([At(10, 40)], [Bar(10, 0)])
            .Should().ContainSingle().Which.Slot.Should().Be(0);
        ChartFootprintLayout.Resolve([At(11, 40)], [Bar(10, 0)])
            .Should().ContainSingle().Which.Slot.Should().Be(0);
        ChartFootprintLayout.Resolve([At(9, 40)], [Bar(10, 0)])
            .Should().BeEmpty();
    }

    #endregion Fill resolution

    #region Helpers

    private static Bar Bar(int hour, int minute) =>
        new(new DateTimeOffset(2026, 11, 9, hour, minute, 0, TimeSpan.Zero), 90m, 91m, 89m, 90m, 1m);

    private static FillMarker At(int hour, int minute) => new()
    {
        Time = new DateTimeOffset(2026, 11, 9, hour, minute, 0, TimeSpan.Zero),
        Side = OrderSide.Sell,
        Price = hour * 1000m + minute,
    };

    private static IReadOnlyList<FillMarkerHit> chartResolution(int hour, int minute) =>
        ChartFootprintLayout.Resolve([At(hour, minute)], Window);

    #endregion Helpers
}
