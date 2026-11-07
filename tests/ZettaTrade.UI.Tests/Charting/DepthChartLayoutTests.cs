namespace ZettaTrade.UI.Tests.Charting;

using System;
using System.Diagnostics;
using System.Linq;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.UI.Charting;

/// <summary>
/// Pure-layout tests for the depth chart frame plan: the per-frame cost is
/// bounded by the visible band count (the stated 60 fps at 1000+ levels
/// target), the cumulative geometry invariants hold, and the served vs
/// displayed depth discipline is shared with the ladder.
/// </summary>
/// <remarks>
/// Runs in the localization-singleton collection (the T-073 flake family,
/// the OrderBookPageLayoutTests discipline): the mid label resolves nothing
/// localized, but the Charting layout suites serialize their
/// process-wide-singleton hygiene in one family.
/// </remarks>
[Collection("LocalizationManagerSingleton")]
public sealed class DepthChartLayoutTests
{
    #region Constants

    private const double BandHeight = 14;
    private const double HeaderHeight = 22;
    private const double Width = 400;

    private const int WarmupFrames = 10;

    #endregion

    #region Test Output

    private readonly ITestOutputHelper _output;

    public DepthChartLayoutTests(ITestOutputHelper output)
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

    /// <summary>
    /// A planned band mirrors a planned ladder row one for one: the widget
    /// height yields the same <c>rowsPerSide</c> for both components.
    /// </summary>
    private static double HeightFor(int bandsPerSide) =>
        HeaderHeight + (2 * BandHeight * bandsPerSide);

    #endregion

    #region Performance

    /// <summary>
    /// The stated performance target: with 1000+ levels per side the pure
    /// frame-plan path stays inside the 16 ms frame budget at 60 fps — the
    /// plan walks the visible bands, not the served depth. The burst runs 60
    /// timed iterations behind a JIT warmup; the worst frame is observation
    /// output only (the scheduler-slice class of flakiness).
    /// </summary>
    [Fact]
    public void Build_SustainsSixtyFramesPerSecond_WithThousandLevelsPerSide()
    {
        const int LevelsPerSide = 1000;
        const int Frames = 60;
        const double FrameBudgetMs = 16.0;

        var book = Book(LevelsPerSide);
        var height = HeightFor(LevelsPerSide);

        // Prime the JIT (the generic plan path, assertions) so type
        // initialization lands outside the measured window.
        for (var i = 0; i < WarmupFrames; i++)
        {
            var warm = DepthChartLayout.Build(book, Width, height, HeaderHeight, BandHeight);
            warm.Bids.Count.Should().Be(LevelsPerSide);
        }

        var frames = new double[Frames];
        for (var i = 0; i < Frames; i++)
        {
            var started = Stopwatch.GetTimestamp();
            var page = DepthChartLayout.Build(book, Width, height, HeaderHeight, BandHeight);
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
            "depth plan: median {0:F3} ms, avg {1:F3} ms, worst {2:F3} ms over {3} frames with {4} levels per side (budget {5} ms)",
            median,
            average,
            worst,
            Frames,
            LevelsPerSide,
            FrameBudgetMs);

        median.Should().BeLessThan(FrameBudgetMs);
        average.Should().BeLessThan(FrameBudgetMs);
    }

    #endregion Performance

    #region Geometry

    [Fact]
    public void Build_PlansBandsAroundTheMidLine()
    {
        var page = DepthChartLayout.Build(
            Book(50),
            Width,
            HeightFor(50),
            HeaderHeight,
            BandHeight);

        page.MidY.Should().BeApproximately((float)(HeaderHeight + (50 * BandHeight)), 0.001f);
        page.SliceHeightPx.Should().Be((float)BandHeight);
        page.LeftX.Should().BeGreaterThan(0f);

        // Bids stack downward from the mid: the best band starts on the mid
        // line, deeper bands one step further down.
        page.Bids[0].Y.Should().Be(page.MidY);
        page.Bids[49].Y.Should().Be(page.MidY + (49 * (float)BandHeight));

        // Asks mirror above the mid: the best band sits directly under the
        // line looking at it from above (its span reaches up to the mid),
        // deeper bands stack upward.
        page.Asks[0].Y.Should().Be(page.MidY);
        page.Asks[49].Y.Should().Be(page.MidY - (49 * (float)BandHeight));
    }

    [Fact]
    public void Build_CumulativeSharesGrowAwayFromTheMid_Golden()
    {
        const int Served = 50;
        var page = DepthChartLayout.Build(Book(Served), Width, HeightFor(Served), HeaderHeight, BandHeight);

        var interior = WritableInterior(Width);
        var total = (decimal)Served * (Served + 1) / 2m; // triangular: 50*51/2
        var cumulative = 0m;
        for (var i = 0; i < Served; i++)
        {
            // Triangular cumulative: level quantity is i+1, so level i
            // carries (i+1)(i+2)/2 of the side's visible total 50*51/2 and
            // the band's fill width grows level by level to the full
            // interior.
            cumulative += i + 1;
            var expectedShare = (float)(cumulative / total);
            page.Bids[i].Y.Should().BeApproximately(page.MidY + (i * (float)BandHeight), 0.001f);
            page.Bids[i].X.Should().BeApproximately(page.LeftX + (interior * expectedShare), 0.001f);
            page.Bids[i].CumulativeFraction.Should().BeApproximately(expectedShare, 0.0001f);
            page.Asks[i].CumulativeFraction.Should().BeApproximately(expectedShare, 0.0001f);
        }

        page.Bids[^1].X.Should().BeApproximately(page.LeftX + interior, 0.001f,
            "the deepest visible band carries the full side volume");
        page.Asks[^1].X.Should().BeApproximately(page.LeftX + interior, 0.001f);
    }

    [Fact]
    public void Build_TrimsTheLevelCountToVisibleBands()
    {
        var book = Book(1000);

        var page = DepthChartLayout.Build(book, Width, 400, HeaderHeight, BandHeight);

        var expectedBands = (int)(378 / (2 * BandHeight));
        page.Bids.Count.Should().Be(expectedBands);
        page.Asks.Count.Should().Be(expectedBands);
        page.Bids.Count.Should().BeLessThan(1000, "per-frame work is bounded by the control size, not the depth");
    }

    [Fact]
    public void Build_EmptySide_YieldsNoSlicesAndDegrades()
    {
        var book = new OrderBook
        {
            Symbol = Symbol.Parse("BTC/USDT"),
            Bids = [new PriceLevel(100m, 1)],
            Asks = [],
            Timestamp = DateTimeOffset.UtcNow,
        };

        var page = DepthChartLayout.Build(book, Width, HeightFor(10), HeaderHeight, BandHeight);

        page.Bids.Should().ContainSingle();
        page.Asks.Should().BeEmpty();
        page.MidLabel.Should().BeNull("a one-sided book has no mid price to label");
    }

    /// <summary>
    /// The served-but-empty book (a connected adapter delivering no levels)
    /// plans nothing: both sides empty, no mid label — the widget's no-data
    /// state rides this shape.
    /// </summary>
    [Fact]
    public void Build_EmptyBook_YieldsEmptySidesAndNoMidLabel()
    {
        var book = new OrderBook
        {
            Symbol = Symbol.Parse("BTC/USDT"),
            Bids = [],
            Asks = [],
            Timestamp = DateTimeOffset.UtcNow,
        };

        var page = DepthChartLayout.Build(book, Width, HeightFor(10), HeaderHeight, BandHeight);

        page.Bids.Should().BeEmpty();
        page.Asks.Should().BeEmpty();
        page.MidLabel.Should().BeNull();
        page.MidY.Should().Be(
            (float)(HeaderHeight + (10 * BandHeight)),
            "an empty book still plans its geometry; only the slices vanish");
    }

    [Fact]
    public void Build_HandlesDegenerateSize()
    {
        var page = DepthChartLayout.Build(Book(10), Width, 1, HeaderHeight, BandHeight);

        // Degenerate height: the bands collapse onto minimal plans, every
        // coordinate stays finite — no NaN, no infinity, no crash.
        foreach (var slice in page.Bids.Concat(page.Asks))
        {
            float.IsNaN(slice.Y).Should().BeFalse();
            float.IsInfinity(slice.Y).Should().BeFalse();
            float.IsNaN(slice.X).Should().BeFalse();
            float.IsInfinity(slice.X).Should().BeFalse();
        }

        page.Bids.Should().ContainSingle("at least one band keeps the sane degenerate state");
        page.Asks.Should().ContainSingle();
    }

    [Fact]
    public void Build_ZeroVolumes_YieldZeroWidthBands()
    {
        var book = new OrderBook
        {
            Symbol = Symbol.Parse("BTC/USDT"),
            Bids = [new PriceLevel(100m, 0), new PriceLevel(99m, 0)],
            Asks = [new PriceLevel(100.01m, 0)],
            Timestamp = DateTimeOffset.UtcNow,
        };

        var page = DepthChartLayout.Build(book, Width, HeightFor(10), HeaderHeight, BandHeight);

        // The unit denominator substitutes for a volumeless side (the
        // ladder's discipline): shares are zero, no band divides by zero.
        page.Bids[0].X.Should().Be(page.LeftX);
        page.Bids[1].X.Should().Be(page.LeftX);
        page.Asks[0].X.Should().Be(page.LeftX);
    }

    #endregion Geometry

    #region Mid Label

    [Fact]
    public void Build_LabelsTheMidPrice_Invariant()
    {
        var page = DepthChartLayout.Build(Book(10), Width, HeightFor(10), HeaderHeight, BandHeight);

        // (100.00 + 100.01) / 2 = 100.005, invariant culture, no thousands.
        page.MidLabel.Should().Be("100.005");
    }

    #endregion Mid Label

    #region Shared Depth Contract

    /// <summary>
    /// The width of the panel's writable interior the fills may span (the
    /// layout's own padding discipline).
    /// </summary>
    private static float WritableInterior(double width) => (float)width - (2 * 8f);

    /// <summary>
    /// The T-072 discipline across components: the ladder and the depth
    /// chart plan from the SAME served book instance and resolve the SAME
    /// visible depth (one band per planned row, mirroring one for one —
    /// same cut, same cumulative normalization). Neither component
    /// re-requests depth (each plan's only source is its book argument) and
    /// neither extends beyond the served cut: both components display
    /// min(served depth, their own pixel budget).
    /// </summary>
    [Fact]
    public void Build_LadderAndDepthShareTheResolvedDepth_OnTheSameBook()
    {
        const int Deep = 1000;

        // A book deeper than every panel budget: both components cut to
        // their own (equal) band budget — the served surplus is neither
        // re-requested nor fabricated — and the fractions they draw for each
        // visible level agree level for level.
        var book = Book(Deep);
        var ladder = OrderBookPageLayout.Build(book, 400, rowHeight: 14, headerHeight: 22);
        var depth = DepthChartLayout.Build(book, Width, 400, HeaderHeight, BandHeight);

        var visible = Math.Min(Deep, 13); // (400 - 22) / 28 bands per side for both
        ladder.Bids.Count.Should().Be(visible);
        depth.Bids.Count.Should().Be(visible, "the depth chart cuts the same rows the ladder cuts");
        for (var i = 0; i < visible; i++)
        {
            depth.Bids[i].CumulativeFraction.Should().Be(ladder.Bids[i].CumulativeFraction,
                "the depth band i and the ladder row i normalize the same served levels");
            depth.Asks[i].CumulativeFraction.Should().Be(ladder.Asks[i].CumulativeFraction);
        }

        // A book shallower than every budget: both display exactly the
        // served depth, the whole of it — nothing hidden, nothing invented.
        var shallow = Book(5);
        OrderBookPageLayout.Build(shallow, 400, rowHeight: 14, headerHeight: 22)
            .Bids.Count.Should().Be(5);
        DepthChartLayout.Build(shallow, Width, 400, HeaderHeight, BandHeight).Bids.Count.Should().Be(5);
    }

    /// <summary>
    /// The plan is a pure function of its book and geometry: two builds
    /// yield identical slices — no hidden state, no re-request riding the
    /// planning.
    /// </summary>
    [Fact]
    public void Build_IsAPureFunctionOfTheBookAndGeometry()
    {
        var book = Book(200);
        var first = DepthChartLayout.Build(book, Width, 400, HeaderHeight, BandHeight);
        var second = DepthChartLayout.Build(book, Width, 400, HeaderHeight, BandHeight);

        second.Bids.Count.Should().Be(first.Bids.Count);
        for (var i = 0; i < first.Bids.Count; i++)
        {
            second.Bids[i].Should().Be(first.Bids[i]);
            second.Asks[i].Should().Be(first.Asks[i]);
        }
    }

    #endregion Shared Depth Contract
}
