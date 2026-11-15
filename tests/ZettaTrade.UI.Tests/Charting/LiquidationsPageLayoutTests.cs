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
/// Pure-layout tests for the liquidation tape's frame plan (T-242): the
/// newest forced order is the top row, the per-frame cost is bounded by the
/// visible rows (the stated 60 fps target with a storm feed), the
/// force-closed POSITION side rides the row, and the empty/paused states
/// degrade visibly — the open cascade rides the header band's badge, the
/// badge absent when no cascade qualifies.
/// </summary>
/// <remarks>
/// Runs in the localization-singleton collection (the T-073 flake family):
/// <c>Build</c> resolves the headers and the badge template through the
/// process-wide <see cref="Livery.Localization.LocalizationManager"/>'s
/// Instance, so a concurrently running manager-creating fact used to serve
/// this suite a test fake's template instead of the real resx one.
/// </remarks>
[Collection("LocalizationManagerSingleton")]
public sealed class LiquidationsPageLayoutTests
{
    #region Constants

    private const double RowHeight = 14;

    private const double HeaderHeight = 26;

    #endregion

    #region Test Output

    private readonly ITestOutputHelper _output;

    public LiquidationsPageLayoutTests(ITestOutputHelper output)
    {
        _output = output;
    }

    #endregion

    #region Test Data

    /// <summary>
    /// Synthetic liquidation tape newest first: event i has a price of
    /// 100.5 - 0.5*i and a size of i + 1, alternating force-closed sides,
    /// stamps a whole minute apart so the column texts stay
    /// decimals-and-digits only.
    /// </summary>
    private static List<LiquidationEvent> NewestFirst(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new LiquidationEvent
            {
                Symbol = Symbol.Parse("BTC/USDT"),
                LiquidatedSide = i % 2 == 0 ? LiquidationSide.Long : LiquidationSide.Short,
                Price = new Price(100.5m - (0.5m * i)),
                Quantity = new Quantity(2.5m + i),
                Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(60 * (count - i)),
            })
            .ToList();

    private static double HeightFor(int rows) => HeaderHeight + (RowHeight * rows);

    private static string TimeText(LiquidationEvent ev) =>
        ev.Timestamp.LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    private static LiquidationCascade Cascade(int orderCount, decimal notional) => new(
        Symbol.Parse("BTC/USDT"),
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch.AddSeconds(30),
        orderCount,
        notional,
        LiquidationSide.Long);

    #endregion

    #region Geometry

    [Fact]
    public void Build_PlansTheNewestEventAtTheTopOfTheBody()
    {
        var page = LiquidationsPageLayout.Build(
            NewestFirst(10),
            HeightFor(10),
            RowHeight,
            HeaderHeight,
            isPaused: false,
            cascade: null);

        page.Rows[0].Y.Should().Be((float)HeaderHeight, "the newest forced order starts right below the header band");
        page.Rows[9].Y.Should().Be((float)(HeaderHeight + (9 * RowHeight)));
        page.Rows.Select(row => row.Y).Should().BeInAscendingOrder("the tape grows downward");
    }

    [Fact]
    public void Build_TrimsTheEventCountToVisibleRows()
    {
        var page = LiquidationsPageLayout.Build(
            NewestFirst(1000),
            HeightFor(40),
            RowHeight,
            HeaderHeight,
            isPaused: false,
            cascade: null);

        page.Rows.Count.Should().Be(40, "per-frame work is bounded by the control size, not the ring fill");
        page.Rows[0].TimeText.Should().Be(TimeText(NewestFirst(1000)[0]), "the visible cut keeps the newest events overlaid");
    }

    [Fact]
    public void Build_ShortTape_ShowsOnlyWhatArrived()
    {
        var page = LiquidationsPageLayout.Build(
            NewestFirst(3),
            HeightFor(40),
            RowHeight,
            HeaderHeight,
            isPaused: false,
            cascade: null);

        page.Rows.Should().HaveCount(3);
        page.Rows[0].Y.Should().Be((float)HeaderHeight);
    }

    #endregion

    #region Columns & Sides

    [Fact]
    public void Build_CarriesTheTimePriceAndSizeTexts()
    {
        var events = NewestFirst(2);
        var page = LiquidationsPageLayout.Build(
            events,
            HeightFor(10),
            RowHeight,
            HeaderHeight,
            isPaused: false,
            cascade: null);

        var newest = events[0];
        page.Rows[0].TimeText.Should().Be(TimeText(newest), "the time column prints the event's local wall clock");
        page.Rows[0].PriceText.Should().Be("100.50", "invariant decimal formatting, trailing zeros trimmed from the fractional tail");
        page.Rows[0].SizeText.Should().Be("2.50");

        var older = events[1];
        page.Rows[1].PriceText.Should().Be("100.00");
        page.Rows[1].SizeText.Should().Be("3.50");
    }

    [Fact]
    public void Build_CarriesTheForceClosedPositionSide()
    {
        var page = LiquidationsPageLayout.Build(
            NewestFirst(6),
            HeightFor(6),
            RowHeight,
            HeaderHeight,
            isPaused: false,
            cascade: null);

        // The force-closed POSITION side rides the row: a liquidated long
        // paints the bearish color, a liquidated short the bullish one —
        // the venue-normalized side, never a raw dialect field.
        page.Rows.Select(row => row.Side)
            .Should().Equal(
                LiquidationSide.Long,
                LiquidationSide.Short,
                LiquidationSide.Long,
                LiquidationSide.Short,
                LiquidationSide.Long,
                LiquidationSide.Short);
    }

    [Fact]
    public void Build_CarriesTheColumnHeaders()
    {
        var page = LiquidationsPageLayout.Build(
            NewestFirst(2),
            HeightFor(10),
            RowHeight,
            HeaderHeight,
            isPaused: false,
            cascade: null);

        page.TimeHeader.Should().NotBeNullOrWhiteSpace();
        page.PriceHeader.Should().NotBeNullOrWhiteSpace();
        page.SizeHeader.Should().NotBeNullOrWhiteSpace();
        page.PausedHint.Should().BeNull("a running tape carries no paused hint");
        page.Badge.Should().BeNull("a window with no qualifying cascade carries no badge");
    }

    #endregion

    #region Cascade Badge

    [Fact]
    public void Build_AnOpenCascade_BadgesTheHeaderBand()
    {
        var page = LiquidationsPageLayout.Build(
            NewestFirst(4),
            HeightFor(10),
            RowHeight,
            HeaderHeight,
            isPaused: false,
            cascade: Cascade(3, 250_000m));

        page.Badge.Should().NotBeNull("the open cascade badges the header band");
        page.Badge.Should().Contain("3", "the badge names the run's served order count");
        page.Badge.Should().Contain("250,000", "the badge names the run's summed notional (invariant grouping)");
    }

    [Fact]
    public void Build_APausedPanel_BadgesAndHintsTogether()
    {
        var page = LiquidationsPageLayout.Build(
            NewestFirst(4),
            HeightFor(10),
            RowHeight,
            HeaderHeight,
            isPaused: true,
            cascade: Cascade(2, 300_000m));

        page.Badge.Should().NotBeNull("the badge survives the pause — the state is real while the rows are frozen");
        page.PausedHint.Should().NotBeNullOrWhiteSpace("the pause must be visible while events stay buffered");
    }

    #endregion

    #region Paused & Empty States

    [Fact]
    public void Build_Paused_KeepsTheRowsAndCarriesTheHint()
    {
        var events = NewestFirst(4);

        var frozen = LiquidationsPageLayout.Build(events, HeightFor(4), RowHeight, HeaderHeight, isPaused: true, cascade: null);
        var running = LiquidationsPageLayout.Build(events, HeightFor(4), RowHeight, HeaderHeight, isPaused: false, cascade: null);

        frozen.Rows.Select(r => r.PriceText).Should().Equal(running.Rows.Select(r => r.PriceText),
            "a paused tape renders exactly the rows it froze with");
        frozen.PausedHint.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Build_EmptyTape_YieldsNoRows()
    {
        var page = LiquidationsPageLayout.Build(
            new List<LiquidationEvent>(),
            HeightFor(10),
            RowHeight,
            HeaderHeight,
            isPaused: false,
            cascade: null);

        page.Rows.Should().BeEmpty("the widget draws the no-data state for an empty ring instead of guessed rows");
    }

    [Fact]
    public void Build_HandlesDegenerateHeight()
    {
        var page = LiquidationsPageLayout.Build(NewestFirst(10), 1, RowHeight, HeaderHeight, isPaused: false, cascade: null);

        page.Rows.Should().ContainSingle("at least one row keeps the sane degenerate state");
    }

    #endregion

    #region Performance

    /// <summary>Untimed frames that prime JIT paths before the measured burst.</summary>
    private const int WarmupFrames = 10;

    /// <summary>
    /// The stated performance target: a forced-order storm's events stay in
    /// the ring, but every frame plans ONLY the rows its height can show —
    /// a plan with 1000+ events available stays inside the 16 ms frame
    /// budget at 60 fps. The burst runs 60 timed iterations behind a JIT
    /// warmup; the summary is written to the test output so the numbers
    /// stay visible in CI. The median and the mean carry the pin (the
    /// trade tape's own reasoning); the worst frame is reported for
    /// observation only.
    /// </summary>
    [Fact]
    public void Build_SustainsSixtyFramesPerSecond_WithThousandEventsBuffered()
    {
        const int BufferedEvents = 1000;
        const int Frames = 60;
        const double FrameBudgetMs = 16.0;

        var events = NewestFirst(BufferedEvents);
        var height = HeightFor(60);

        // Prime the JIT (generic Build path, assertions) so type
        // initialization lands outside the measured window.
        for (var i = 0; i < WarmupFrames; i++)
        {
            var warm = LiquidationsPageLayout.Build(events, height, RowHeight, HeaderHeight, isPaused: false, cascade: null);
            warm.Rows.Count.Should().Be(60);
        }

        var frames = new double[Frames];
        for (var i = 0; i < Frames; i++)
        {
            var started = Stopwatch.GetTimestamp();
            var page = LiquidationsPageLayout.Build(events, height, RowHeight, HeaderHeight, isPaused: false, cascade: null);
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
            "frame plan: median {0:F3} ms, avg {1:F3} ms, worst {2:F3} ms over {3} frames with {4} events buffered (budget {5} ms)",
            median,
            average,
            worst,
            Frames,
            BufferedEvents,
            FrameBudgetMs);

        median.Should().BeLessThan(FrameBudgetMs);
        average.Should().BeLessThan(FrameBudgetMs);
    }

    #endregion
}
