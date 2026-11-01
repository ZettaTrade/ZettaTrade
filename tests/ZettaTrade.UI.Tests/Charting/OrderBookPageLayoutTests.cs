namespace ZettaTrade.UI.Tests.Charting;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.UI.Charting;

/// <summary>
/// Pure-layout tests for the order book frame plan: the per-frame cost is
/// bounded by the visible rows (the stated 60 fps at 1000+ levels target),
/// and the geometric/labels invariants hold.
/// </summary>
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

    /// <summary>
    /// The stated performance target: with 1000+ levels per side the pure
    /// frame-plan path stays inside the 16.6 ms/16 ms frame budget at 60 fps.
    /// The burst runs 60 iterations; the measured average is written to the
    /// test output so the number stays visible in CI.
    /// </summary>
    [Fact]
    public void Build_SustainsSixtyFramesPerSecond_WithThousandLevelsPerSide()
    {
        const int LevelsPerSide = 1000;
        const int Frames = 60;
        const double FrameBudgetMs = 16.0;

        var book = Book(LevelsPerSide);
        var height = HeightFor(LevelsPerSide);

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

        var average = frames.Average();
        var worst = frames.Max();
        _output.WriteLine(
            "frame plan: avg {0:F3} ms, worst {1:F3} ms over {2} frames with {3} levels per side (budget {4} ms)",
            average,
            worst,
            Frames,
            LevelsPerSide,
            FrameBudgetMs);

        average.Should().BeLessThan(FrameBudgetMs);
        worst.Should().BeLessThan(FrameBudgetMs);
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
}
