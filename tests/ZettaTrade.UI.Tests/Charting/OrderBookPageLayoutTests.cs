namespace ZettaTrade.UI.Tests.Charting;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.UI.Charting;

/// <summary>
/// Pure-layout tests for the order book frame plan: the per-frame cost is
/// bounded by the visible rows (the stated 60 fps at 1000+ levels target),
/// and the geometric/labels invariants hold.
/// </summary>
/// <remarks>
/// Runs in the localization-singleton collection (the T-073 flake family):
/// <c>Build</c> resolves the header templates through the process-wide
/// <see cref="Livery.Localization.LocalizationManager"/>.Instance, so a
/// concurrently running manager-creating fact used to serve this suite a
/// test fake's template ("en:OrderBook.SpreadFormat") instead of the real
/// resx one — the observed SpreadText failure. Serializing with the
/// localization suites closes the cross-class handoff, and the singletons
/// do not survive their facts (Dispose clears the static), so this suite
/// resolves the raw Strings catalog either way.
/// </remarks>
[Collection("LocalizationManagerSingleton")]
public sealed class OrderBookPageLayoutTests
{
    #region Constants

    private const double RowHeight = 14;
    private const double HeaderHeight = 22;

    #endregion

    #region Test Output

    private readonly ITestOutputHelper _output;

    public OrderBookPageLayoutTests(ITestOutputHelper output)
    {
        _output = output;
    }

    #endregion

    #region Test Data

    /// <summary>
    /// Synthetic book: bids from best 100.00 downward, asks from 100.01 upward,
    /// quantity of every level is its index + 1 so cumulative values are
    /// predictable (triangular numbers).
    /// </summary>
    private static OrderBook Book(int levels) =>
        new()
        {
            Symbol = Symbol.Parse("BTC/USDT"),
            Bids = Enumerable.Range(0, levels)
                .Select(i => new PriceLevel(100m - i, i + 1))
                .ToList(),
            Asks = Enumerable.Range(0, levels)
                .Select(i => new PriceLevel(100.01m + i, i + 1))
                .ToList(),
            Timestamp = DateTimeOffset.UtcNow,
        };

    private static double HeightFor(int rowsPerSide) =>
        HeaderHeight + (2 * RowHeight * rowsPerSide);

    #endregion

    #region Performance

    /// <summary>Untimed frames that prime JIT paths before the measured burst.</summary>
    private const int WarmupFrames = 10;

    /// <summary>
    /// The stated performance target: with 1000+ levels per side the pure
    /// frame-plan path stays inside the 16.6 ms/16 ms frame budget at 60 fps.
    /// The burst runs 60 timed iterations behind a JIT warmup; the summary is
    /// written to the test output so the numbers stay visible in CI.
    /// </summary>
    /// <remarks>
    /// The 60 fps contract is pinned on the MEDIAN and the MEAN of the burst:
    /// a strict per-frame (or worst-frame) cap fails spuriously under the
    /// parallel test host, where an OS scheduler slice can steal tens of
    /// milliseconds from one frame while the sustained rate stays
    /// comfortably inside the budget. A genuine allocation-storm regression
    /// inflates EVERY frame, so the median and mean catch it anyway; the
    /// worst frame is reported to the test output for observation only.
    /// </remarks>
    [Fact]
    public void Build_SustainsSixtyFramesPerSecond_WithThousandLevelsPerSide()
    {
        const int LevelsPerSide = 1000;
        const int Frames = 60;
        const double FrameBudgetMs = 16.0;

        var book = Book(LevelsPerSide);
        var height = HeightFor(LevelsPerSide);

        // Prime the JIT (generic Build path, assertions) so type
        // initialization lands outside the measured window.
        for (var i = 0; i < WarmupFrames; i++)
        {
            var warm = OrderBookPageLayout.Build(book, height, RowHeight, HeaderHeight);
            warm.Bids.Count.Should().Be(LevelsPerSide);
        }

        var frames = new double[Frames];
        for (var i = 0; i < Frames; i++)
        {
            var started = Stopwatch.GetTimestamp();
            var page = OrderBookPageLayout.Build(book, height, RowHeight, HeaderHeight);
            var stopped = Stopwatch.GetTimestamp();

            page.Bids.Count.Should().Be(LevelsPerSide);
            page.Asks.Count.Should().Be(LevelsPerSide);
            frames[i] = (stopped - started) * 1000d / Stopwatch.Frequency;
        }

        Array.Sort(frames);
        var median = (frames[(Frames / 2) - 1] + frames[Frames / 2]) / 2d;
        var average = frames.Average();
        var worst = frames[Frames - 1];
        _output.WriteLine(
            "frame plan: median {0:F3} ms, avg {1:F3} ms, worst {2:F3} ms over {3} frames with {4} levels per side (budget {5} ms)",
            median,
            average,
            worst,
            Frames,
            LevelsPerSide,
            FrameBudgetMs);

        median.Should().BeLessThan(FrameBudgetMs);
        average.Should().BeLessThan(FrameBudgetMs);
    }

    #endregion

    #region Geometry

    [Fact]
    public void Build_PlansRowsAroundTheMidLine()
    {
        var page = OrderBookPageLayout.Build(
            Book(50),
            HeightFor(50),
            RowHeight,
            HeaderHeight);

        page.MidY.Should().BeApproximately((float)(HeaderHeight + (50 * RowHeight)), 0.001f);

        // Bids hang below the mid: the best on the mid, deeper rows further down.
        page.Bids[0].Y.Should().Be(page.MidY);
        page.Bids[49].Y.Should().Be(page.MidY + (49 * (float)RowHeight));
        page.Bids[49].Y.Should().BeGreaterThan(page.Bids[0].Y);

        // Asks stack above the mid: the best right above it, deepest at the top.
        page.Asks[0].Y.Should().Be(page.MidY - (float)RowHeight);
        page.Asks[49].Y.Should().Be(page.MidY - (50 * (float)RowHeight));
        page.Asks[49].Y.Should().BeLessThan(page.Asks[0].Y);
    }

    [Fact]
    public void Build_CumulativeVolumesGrowAwayFromTheMid()
    {
        var page = OrderBookPageLayout.Build(
            Book(50),
            HeightFor(50),
            RowHeight,
            HeaderHeight);

        page.Bids.Select(row => row.PriceText)
            .Should().StartWith("100.00").And.EndWith("51.00");
        page.Bids[0].CumulativeText.Should().Be("1.00");
        page.Bids[49].CumulativeText.Should().Be("1275.00"); // triangular: 50*51/2
        page.Bids[0].CumulativeFraction.Should().Be(1f / 1275f);
        page.Bids[49].CumulativeFraction.Should().Be(1f, "the deepest row carries the full bar");

        page.Asks[49].CumulativeText.Should().Be("1275.00");
    }

    [Fact]
    public void Build_TrimsTheLevelCountToVisibleRows()
    {
        var book = Book(1000);

        var page = OrderBookPageLayout.Build(book, 400, RowHeight, HeaderHeight);

        var expectedRows = (int)(378 / (2 * RowHeight));
        page.Bids.Count.Should().Be(expectedRows);
        page.Asks.Count.Should().Be(expectedRows);
        page.Bids.Count.Should().BeLessThan(1000, "per-frame work is bounded by the control size, not the depth");
    }

    [Fact]
    public void Build_EmptySide_YieldsNoRowsAndDegrades()
    {
        var book = new OrderBook
        {
            Symbol = Symbol.Parse("BTC/USDT"),
            Bids = [new PriceLevel(100m, 1)],
            Asks = [],
            Timestamp = DateTimeOffset.UtcNow,
        };

        var page = OrderBookPageLayout.Build(
            book,
            HeightFor(10),
            RowHeight,
            HeaderHeight);

        page.Bids.Should().ContainSingle();
        page.Asks.Should().BeEmpty();
    }

    [Fact]
    public void Build_HandlesDegenerateHeight()
    {
        var page = OrderBookPageLayout.Build(Book(10), 1, RowHeight, HeaderHeight);

        page.Bids.Should().ContainSingle("at least one row keeps the sane degenerate state");
        page.Asks.Should().ContainSingle();
    }

    #endregion

    #region Header

    [Fact]
    public void Build_CarriesTheSymbolAndSpread()
    {
        var page = OrderBookPageLayout.Build(
            Book(10),
            HeightFor(10),
            RowHeight,
            HeaderHeight);

        page.SymbolLabel.Should().Be("BTC/USDT");
        page.SpreadText.Should().NotBeNull().And.Contain("0.01"); // 100.01 - 100.00, invariant
        page.PriceHeader.Should().NotBeNullOrWhiteSpace();
        page.CumulativeHeader.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Build_OneSidedBook_HasNoSpread()
    {
        var book = new OrderBook
        {
            Symbol = Symbol.Parse("BTC/USDT"),
            Bids = [new PriceLevel(100m, 1)],
            Asks = [],
            Timestamp = DateTimeOffset.UtcNow,
        };

        var page = OrderBookPageLayout.Build(book, HeightFor(10), RowHeight, HeaderHeight);

        page.SpreadText.Should().BeNull();
    }

    #endregion

    #region Hit Resolution

    /// <summary>
    /// Click geometry mirrors the plan: for a widget planned for
    /// <c>rowsPerSide</c>, the mid line sits one row band below center,
    /// exactly where <see cref="OrderBookPageLayout.Build"/> puts it.
    /// </summary>
    private static double MidYFor(int rowsPerSide) =>
        HeaderHeight + (RowHeight * rowsPerSide);

    [Fact]
    public void ResolveLevel_NullBook_YieldsNull()
    {
        var hit = OrderBookPageLayout.ResolveLevel(
            book: null,
            widgetHeight: HeightFor(10),
            cursorY: MidYFor(10),
            rowHeight: RowHeight,
            headerHeight: HeaderHeight);

        hit.Should().BeNull();
    }

    [Fact]
    public void ResolveLevel_ClickInHeaderBand_YieldsNull()
    {
        var hit = OrderBookPageLayout.ResolveLevel(
            Book(10),
            HeightFor(10),
            cursorY: HeaderHeight / 2,
            RowHeight,
            HeaderHeight);

        hit.Should().BeNull();
    }

    [Fact]
    public void ResolveLevel_AskRowCenters_MapSellAscending()
    {
        const int Depth = 10;
        var book = Book(Depth);
        var midY = MidYFor(Depth);

        for (var i = 0; i < Depth; i++)
        {
            var hit = OrderBookPageLayout.ResolveLevel(
                book,
                HeightFor(Depth),
                cursorY: midY - ((i + 0.5) * RowHeight),
                RowHeight,
                HeaderHeight);

            hit.Should().NotBeNull($"ask row {i} is planned and clickable");
            hit!.Value.Side.Should().Be(OrderSide.Sell);
            hit.Value.Price.Should().Be(100.01m + i, $"ask row {i} shows 100.01+{i}");
        }
    }

    [Fact]
    public void ResolveLevel_BidRowCenters_MapBuyDescending()
    {
        const int Depth = 10;
        var book = Book(Depth);
        var midY = MidYFor(Depth);

        for (var i = 0; i < Depth; i++)
        {
            var hit = OrderBookPageLayout.ResolveLevel(
                book,
                HeightFor(Depth),
                cursorY: midY + ((i + 0.5) * RowHeight),
                RowHeight,
                HeaderHeight);

            hit.Should().NotBeNull($"bid row {i} is planned and clickable");
            hit!.Value.Side.Should().Be(OrderSide.Buy);
            hit.Value.Price.Should().Be(100m - i, $"bid row {i} shows 100-{i}");
        }
    }

    [Fact]
    public void ResolveLevel_MidLineBelongsToBestBid()
    {
        var hit = OrderBookPageLayout.ResolveLevel(
            Book(10),
            HeightFor(10),
            cursorY: MidYFor(10),
            RowHeight,
            HeaderHeight);

        hit!.Value.Side.Should().Be(OrderSide.Buy);
        hit.Value.Price.Should().Be(100m);
    }

    /// <summary>
    /// Row spans are half-open toward the mid line: a row's own top edge
    /// (the pixel away from the mid line) belongs to it, so the exact row
    /// boundary resolves to the closer row — no dead pixel, no off-by-one.
    /// </summary>
    [Fact]
    public void ResolveLevel_ExactRowBoundaries_ResolveToTheCloserRow()
    {
        var book = Book(10);
        var midY = MidYFor(10);

        // One row height above the mid line: the top edge of ask row 0.
        var askEdge = OrderBookPageLayout.ResolveLevel(
            book,
            HeightFor(10),
            cursorY: midY - RowHeight,
            RowHeight,
            HeaderHeight);
        askEdge!.Value.Side.Should().Be(OrderSide.Sell);
        askEdge.Value.Price.Should().Be(100.01m);

        // Two row heights above: the top edge of ask row 1 (the synthetic
        // ladder steps by 1.00, so row 1 shows 101.01).
        var askSecondEdge = OrderBookPageLayout.ResolveLevel(
            book,
            HeightFor(10),
            cursorY: midY - (2 * RowHeight),
            RowHeight,
            HeaderHeight);
        askSecondEdge!.Value.Price.Should().Be(101.01m);

        // One row height below the mid line: the top edge of bid row 1
        // (the mid line itself already belongs to bid row 0).
        var bidEdge = OrderBookPageLayout.ResolveLevel(
            book,
            HeightFor(10),
            cursorY: midY + RowHeight,
            RowHeight,
            HeaderHeight);
        bidEdge!.Value.Side.Should().Be(OrderSide.Buy);
        bidEdge.Value.Price.Should().Be(99m);
    }

    [Fact]
    public void ResolveLevel_BeyondPlannedRows_YieldsNull()
    {
        // A deep book planned for 5 rows plus a 7 px stub: the widget shows
        // a sliver of the unplanned bid row 5, which stays unclickable.
        const int PlannedRows = 5;
        var book = Book(100);
        var midY = MidYFor(PlannedRows);

        var headerMostPlanned = OrderBookPageLayout.ResolveLevel(
            book,
            HeightFor(PlannedRows) + 7,
            cursorY: midY - (4.5 * RowHeight),
            RowHeight,
            HeaderHeight);
        headerMostPlanned!.Value.Side.Should().Be(OrderSide.Sell);
        headerMostPlanned.Value.Price.Should().Be(104.01m, "ask row 4, the last planned one");

        var unplannedStub = OrderBookPageLayout.ResolveLevel(
            book,
            HeightFor(PlannedRows) + 7,
            cursorY: midY + 73, // inside the sliver of unplanned bid row 5
            RowHeight,
            HeaderHeight);
        unplannedStub.Should().BeNull();
    }

    [Fact]
    public void ResolveLevel_BookSmallerThanPlan_LevelsBeyondContentYieldNull()
    {
        const int PlannedRows = 5;
        var book = Book(2);
        var midY = MidYFor(PlannedRows);

        var belowContent = OrderBookPageLayout.ResolveLevel(
            book,
            HeightFor(PlannedRows),
            cursorY: midY + (3.5 * RowHeight),
            RowHeight,
            HeaderHeight);
        belowContent.Should().BeNull("only two bids exist; row 3 is empty space");

        var aboveContent = OrderBookPageLayout.ResolveLevel(
            book,
            HeightFor(PlannedRows),
            cursorY: midY - (3.5 * RowHeight),
            RowHeight,
            HeaderHeight);
        aboveContent.Should().BeNull();

        var deepestBid = OrderBookPageLayout.ResolveLevel(
            book,
            HeightFor(PlannedRows),
            cursorY: midY + (1.5 * RowHeight),
            RowHeight,
            HeaderHeight);
        deepestBid!.Value.Side.Should().Be(OrderSide.Buy);
        deepestBid.Value.Price.Should().Be(99m);
    }

    #endregion
}
