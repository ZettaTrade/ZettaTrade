namespace ZettaTrade.UI.Tests.Charting;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.UI.Charting;

/// <summary>
/// Pure-layout tests for the trade tape frame plan: the newest trade is the
/// top row, the per-frame cost is bounded by the visible rows (the stated
/// 60 fps target with a burst feed), the aggressor side rides the row and
/// the empty/paused states degrade visibly.
/// </summary>
/// <remarks>
/// Runs in the localization-singleton collection (the T-073 flake family):
/// <c>Build</c> resolves the headers through the process-wide
/// <see cref="Livery.Localization.LocalizationManager"/>.Instance, so a
/// concurrently running manager-creating fact used to serve this suite a
/// test fake's template instead of the real resx one. Serializing with the
/// localization suites closes the cross-class handoff; the singletons do not
/// survive their facts (Dispose clears the static), so this suite resolves
/// the raw Strings catalog either way.
/// </remarks>
[Collection("LocalizationManagerSingleton")]
public sealed class TradeTapePageLayoutTests
{
    #region Constants

    private const double RowHeight = 14;
    private const double HeaderHeight = 22;

    #endregion

    #region Test Output

    private readonly ITestOutputHelper _output;

    public TradeTapePageLayoutTests(ITestOutputHelper output)
    {
        _output = output;
    }

    #endregion

    #region Test Data

    /// <summary>
    /// Synthetic tape newest first: trade i has a price of 100.5 - 0.5*i and
    /// a quantity of i + 1, alternating sides, stamps a whole minute apart so
    /// the column texts stay decimals-and-digits only.
    /// </summary>
    private static List<MarketTrade> NewestFirst(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new MarketTrade
            {
                Id = $"t{i}",
                Symbol = Symbol.Parse("BTC/USDT"),
                Side = i % 2 == 0 ? OrderSide.Buy : OrderSide.Sell,
                Price = new Price(100.5m - (0.5m * i)),
                Quantity = new Quantity(2.5m + i),
                Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(60 * (count - i)),
            })
            .ToList();

    private static double HeightFor(int rows) => HeaderHeight + (RowHeight * rows);

    private static string TimeText(MarketTrade trade) =>
        trade.Timestamp.LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    #endregion

    #region Geometry

    [Fact]
    public void Build_PlansTheNewestTradeAtTheTopOfTheBody()
    {
        var page = TradeTapePageLayout.Build(
            NewestFirst(10),
            HeightFor(10),
            RowHeight,
            HeaderHeight,
            isPaused: false);

        page.Rows[0].Y.Should().Be((float)HeaderHeight, "the newest trade starts right below the header band");
        page.Rows[9].Y.Should().Be((float)(HeaderHeight + (9 * RowHeight)));
        page.Rows.Select(row => row.Y).Should().BeInAscendingOrder("the tape grows downward");
    }

    [Fact]
    public void Build_TrimsTheTradeCountToVisibleRows()
    {
        var page = TradeTapePageLayout.Build(
            NewestFirst(1000),
            HeightFor(40),
            RowHeight,
            HeaderHeight,
            isPaused: false);

        page.Rows.Count.Should().Be(40, "per-frame work is bounded by the control size, not the ring fill");
        page.Rows[0].TimeText.Should().Be(TimeText(NewestFirst(1000)[0]), "the visible cut keeps the newest trades overlaid");
    }

    [Fact]
    public void Build_ShortTape_ShowsOnlyWhatArrived()
    {
        var page = TradeTapePageLayout.Build(
            NewestFirst(3),
            HeightFor(40),
            RowHeight,
            HeaderHeight,
            isPaused: false);

        page.Rows.Should().HaveCount(3);
        page.Rows[0].Y.Should().Be((float)HeaderHeight);
    }

    #endregion

    #region Columns & Sides

    [Fact]
    public void Build_CarriesTheTimePriceAndSizeTexts()
    {
        var trades = NewestFirst(2);
        var page = TradeTapePageLayout.Build(
            trades,
            HeightFor(10),
            RowHeight,
            HeaderHeight,
            isPaused: false);

        var newest = trades[0];
        page.Rows[0].TimeText.Should().Be(TimeText(newest), "the time column prints the trade's local wall clock");
        page.Rows[0].PriceText.Should().Be("100.50", "format trims to two decimals minimum, invariant");
        page.Rows[0].SizeText.Should().Be("2.50");

        var older = trades[1];
        page.Rows[1].PriceText.Should().Be("100.00");
        page.Rows[1].SizeText.Should().Be("3.50");
    }

    [Fact]
    public void Build_AssignsTheSideColors()
    {
        var page = TradeTapePageLayout.Build(
            NewestFirst(6),
            HeightFor(6),
            RowHeight,
            HeaderHeight,
            isPaused: false);

        // The aggressor side rides the row: the widget colors buys with the
        // bullish candle color, sells with the bearish one.
        page.Rows.Select(row => row.Side)
            .Should().Equal(
                OrderSide.Buy,
                OrderSide.Sell,
                OrderSide.Buy,
                OrderSide.Sell,
                OrderSide.Buy,
                OrderSide.Sell);
    }

    [Fact]
    public void Build_CarriesTheColumnHeaders()
    {
        var page = TradeTapePageLayout.Build(
            NewestFirst(2),
            HeightFor(10),
            RowHeight,
            HeaderHeight,
            isPaused: false);

        page.TimeHeader.Should().NotBeNullOrWhiteSpace();
        page.PriceHeader.Should().NotBeNullOrWhiteSpace();
        page.SizeHeader.Should().NotBeNullOrWhiteSpace();
        page.PausedHint.Should().BeNull("a running tape carries no paused hint");
    }

    #endregion

    #region Paused & Empty States

    [Fact]
    public void Build_Paused_KeepsTheRowsAndCarriesTheHint()
    {
        var trades = NewestFirst(4);

        var frozen = TradeTapePageLayout.Build(trades, HeightFor(4), RowHeight, HeaderHeight, isPaused: true);
        var running = TradeTapePageLayout.Build(trades, HeightFor(4), RowHeight, HeaderHeight, isPaused: false);

        frozen.Rows.Select(r => r.PriceText).Should().Equal(running.Rows.Select(r => r.PriceText),
            "a paused tape renders exactly the rows it froze with");
        frozen.PausedHint.Should().NotBeNullOrWhiteSpace("the pause must be visible while trades stay buffered");
    }

    [Fact]
    public void Build_EmptyTape_YieldsNoRows()
    {
        var page = TradeTapePageLayout.Build(new List<MarketTrade>(), HeightFor(10), RowHeight, HeaderHeight, isPaused: false);

        page.Rows.Should().BeEmpty("the widget draws the no-data state for an empty ring instead of guessed rows");
    }

    [Fact]
    public void Build_HandlesDegenerateHeight()
    {
        var page = TradeTapePageLayout.Build(NewestFirst(10), 1, RowHeight, HeaderHeight, isPaused: false);

        page.Rows.Should().ContainSingle("at least one row keeps the sane degenerate state");
    }

    #endregion

    #region Performance

    /// <summary>Untimed frames that prime JIT paths before the measured burst.</summary>
    private const int WarmupFrames = 10;

    /// <summary>
    /// The stated performance target: a burst feed's trades stay in the ring,
    /// but every frame plans ONLY the rows its height can show — a plan with
    /// 1000+ trades available stays inside the 16 ms frame budget at 60 fps.
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
    public void Build_SustainsSixtyFramesPerSecond_WithThousandTradesBuffered()
    {
        const int BufferedTrades = 1000;
        const int Frames = 60;
        const double FrameBudgetMs = 16.0;

        var trades = NewestFirst(BufferedTrades);
        var height = HeightFor(60);

        // Prime the JIT (generic Build path, assertions) so type
        // initialization lands outside the measured window.
        for (var i = 0; i < WarmupFrames; i++)
        {
            var warm = TradeTapePageLayout.Build(trades, height, RowHeight, HeaderHeight, isPaused: false);
            warm.Rows.Count.Should().Be(60);
        }

        var frames = new double[Frames];
        for (var i = 0; i < Frames; i++)
        {
            var started = Stopwatch.GetTimestamp();
            var page = TradeTapePageLayout.Build(trades, height, RowHeight, HeaderHeight, isPaused: false);
            var stopped = Stopwatch.GetTimestamp();

            page.Rows.Count.Should().Be(60);
            page.PausedHint.Should().BeNull();
            frames[i] = (stopped - started) * 1000d / Stopwatch.Frequency;
        }

        Array.Sort(frames);
        var median = (frames[(Frames / 2) - 1] + frames[Frames / 2]) / 2d;
        var average = frames.Average();
        var worst = frames[Frames - 1];
        _output.WriteLine(
            "frame plan: median {0:F3} ms, avg {1:F3} ms, worst {2:F3} ms over {3} frames with {4} trades buffered (budget {5} ms)",
            median,
            average,
            worst,
            Frames,
            BufferedTrades,
            FrameBudgetMs);

        median.Should().BeLessThan(FrameBudgetMs);
        average.Should().BeLessThan(FrameBudgetMs);
    }

    #endregion
}
