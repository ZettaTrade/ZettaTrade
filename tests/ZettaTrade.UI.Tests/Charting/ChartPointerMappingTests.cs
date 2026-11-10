namespace ZettaTrade.UI.Tests.Charting;

using System;
using System.Collections.Generic;
using Avalonia;
using FluentAssertions;
using ZettaTrade.Charting;
using ZettaTrade.Core.Enums;
using ZettaTrade.UI.Charting;
using Xunit;

/// <summary>
/// The drawings layer's pointer-mapping goldens (T-091): the inverse of
/// the draw pass's mapping round-trips through the SAME pure helpers,
/// every scale mode unwinds to raw prices, and the grid snap catches
/// only what rides within the threshold — a sloppy drag stays free.
/// </summary>
public sealed class ChartPointerMappingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 10, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Four hourly opens, plotted with a 10 px pitch off the plot's right edge (x 280..310).</summary>
    private static IReadOnlyList<DateTimeOffset> Opens() =>
    [
        T0,
        T0.AddHours(1),
        T0.AddHours(2),
        T0.AddHours(3),
    ];

    private const double Pitch = 10d;
    private const double Threshold = 6d;
    private static readonly Rect Plot = new(0, 0, 320, 200);
    private const decimal MinAxis = 90m;
    private const decimal AxisRange = 100m;

    [Fact]
    public void EmptyWindow_ReturnsNull()
    {
        ChartPointerMapping.MapPointToAnchor(
            Plot, Pitch, [], MinAxis, AxisRange, PriceScaleMode.Linear, 0m, 1m, 300d, 100d)
            .Should().BeNull("no painted geometry, no honest reading.");
    }

    [Fact]
    public void RoundTrip_TimeAndPrice_LinearMode()
    {
        var anchor = new ChartDrawingAnchor(T0.AddHours(1), 95m);
        var x = ChartBarLayout.BarXForTime(Plot.Right, Pitch, Opens(), anchor.Time);
        var y = (double)ChartPriceScale.PriceToY(
            Plot,
            ChartPriceTransform.ToAxisValue(anchor.Price, PriceScaleMode.Linear, 0m, 1m),
            MinAxis,
            AxisRange);

        var mapped = ChartPointerMapping.MapPointToAnchor(
            Plot, Pitch, Opens(), MinAxis, AxisRange, PriceScaleMode.Linear, 0m, 1m, x, y)
            ?? throw new InvalidOperationException("a painted frame refusing to read back.");

        mapped.Time.Should().Be(anchor.Time, "the same frame's constants invert exactly.");
        mapped.Price.Should().BeApproximately(anchor.Price, 0.01m, "the float y-rounding stays sub-cent.");
    }

    [Fact]
    public void RoundTrip_LogMode_UnwindsToRawPrices()
    {
        var anchor = new ChartDrawingAnchor(T0, 100m);

        var mapped = ChartPointerMapping.MapPointToAnchor(
            Plot,
            Pitch,
            Opens(),
            1m,
            2m,
            PriceScaleMode.Logarithmic,
            0m,
            1m,
            Plot.Right - (4 * Pitch),
            (double)ChartPriceScale.PriceToY(
                Plot,
                ChartPriceTransform.ToAxisValue(anchor.Price, PriceScaleMode.Logarithmic, 0m, 1m),
                1m,
                2m))
            ?? throw new InvalidOperationException("a painted frame refusing to read back.");

        mapped.Price.Should().BeApproximately(100m, 0.01m, "10^(log10 100) is the raw price again.");
    }

    [Fact]
    public void RoundTrip_PercentMode_UnwindsOverTheAnchor()
    {
        var mapped = ChartPointerMapping.MapPointToAnchor(
            Plot,
            Pitch,
            Opens(),
            MinAxis,
            AxisRange,
            PriceScaleMode.Percent,
            100m,
            1m,
            Plot.Right - (2 * Pitch),
            (double)ChartPriceScale.PriceToY(
                Plot,
                10m,
                MinAxis,
                AxisRange))
            ?? throw new InvalidOperationException("a painted frame refusing to read back.");

        mapped.Price.Should().BeApproximately(110m, 0.01m, "the +10% axis read is 110 over the 100 anchor.");
    }

    [Fact]
    public void BeforeFirstOpen_ClampsToTheFirstOpen()
    {
        var mapped = ChartPointerMapping.MapPointToAnchor(
            Plot, Pitch, Opens(), MinAxis, AxisRange, PriceScaleMode.Linear, 0m, 1m, 270d, 100d)
            ?? throw new InvalidOperationException("a painted frame refusing to read back.");

        mapped.Time.Should().Be(T0, "the left of the slot space reads the first open, never an earlier time.");
    }

    [Fact]
    public void MidSlot_AdvancesThroughTheBarSpan()
    {
        var mapped = ChartPointerMapping.MapPointToAnchor(
            Plot, Pitch, Opens(), MinAxis, AxisRange, PriceScaleMode.Linear, 0m, 1m, 286d, 100d)
            ?? throw new InvalidOperationException("a painted frame refusing to read back.");

        (mapped.Time - T0).TotalHours.Should().BeApproximately(0.6, 1e-6, "the fraction rides the slot's own span.");
    }

    [Fact]
    public void SnapToGrid_OpenRidesTheThreshold()
    {
        // The threshold is the caller's affordance: at 6 px with a 10 px
        // pitch every in-slot point snaps (0.5 slot = 5 px), so the free
        // case asserts with a 3 px threshold where 0.36 slots = 3.6 px.
        var snapping = new ChartDrawingAnchor(T0.AddHours(0.3), 100m);   // 3 px off the first open
        var snapped = ChartPointerMapping.SnapToGrid(
            Plot, Pitch, Opens(), [], MinAxis, AxisRange, PriceScaleMode.Linear, 0m, 1m, snapping, 6d);

        snapped.Time.Should().Be(T0, "3 px to the open is within the 6 px threshold.");

        var towardNext = new ChartDrawingAnchor(T0.AddHours(0.6), 100m); // 4 px off the second open
        var next = ChartPointerMapping.SnapToGrid(
            Plot, Pitch, Opens(), [], MinAxis, AxisRange, PriceScaleMode.Linear, 0m, 1m, towardNext, 6d);

        next.Time.Should().Be(T0.AddHours(1), "the nearest open wins, not the nearest left one.");

        var loose = new ChartDrawingAnchor(T0.AddHours(0.36), 100m);    // 3.6 px off everywhere
        var free = ChartPointerMapping.SnapToGrid(
            Plot, Pitch, Opens(), [], MinAxis, AxisRange, PriceScaleMode.Linear, 0m, 1m, loose, 3d);

        free.Time.Should().Be(loose.Time, "a sloppy drag stays free.");
    }

    [Fact]
    public void SnapToGrid_PriceRungSnapsAndUnwinds_AcrossModes()
    {
        var linear = ChartPointerMapping.SnapToGrid(
            Plot,
            Pitch,
            Opens(),
            [150m],
            MinAxis,
            AxisRange,
            PriceScaleMode.Linear,
            0m,
            1m,
            new ChartDrawingAnchor(T0, 152m),
            Threshold);

        linear.Price.Should().Be(150m, "the rung absorbs a 2-of-100-price distance.");

        var percent = ChartPointerMapping.SnapToGrid(
            Plot,
            Pitch,
            Opens(),
            [10m],
            MinAxis,
            AxisRange,
            PriceScaleMode.Percent,
            100m,
            1m,
            new ChartDrawingAnchor(T0, 110.3m),
            Threshold);

        percent.Price.Should().BeApproximately(110m, 0.01m, "the percent rung unwinds over the anchor to the raw price.");
    }
}
