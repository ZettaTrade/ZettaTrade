namespace ZettaTrade.UI.Tests.Charting;

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.Charting;

/// <summary>
/// The snapping goldens (T-091): nearest-rung picks for the price ladder,
/// nearest-open-time picks for the bar grid (epoch-mapped), and the empty
/// grid's no-snap answer — the chart supplies the thresholds, the helper
/// supplies the candidate.
/// </summary>
public sealed class ChartDrawingSnappingTests
{
    [Fact]
    public void Nearest_PriceRungs_PicksTheClosest()
    {
        IReadOnlyList<decimal> rungs = [100m, 105m, 110m, 115m];

        var snapped = ChartDrawingSnapping.Nearest(107.0, rungs, p => (double)p);

        snapped.Should().Be(105m);
    }

    [Fact]
    public void Nearest_ExactMidpoint_PicksTheLowerRung()
    {
        IReadOnlyList<decimal> rungs = [100m, 110m];

        var snapped = ChartDrawingSnapping.Nearest(105.0, rungs, p => (double)p);

        snapped.Should().Be(100m, "a tie keeps the first (lower) rung — deterministic, then the chart's threshold rules.");
    }

    [Fact]
    public void Nearest_BarOpenTimes_PicksTheClosestEpoch()
    {
        var t0 = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
        var t1 = new DateTimeOffset(2026, 11, 1, 10, 15, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 11, 1, 10, 30, 0, TimeSpan.Zero);
        IReadOnlyList<DateTimeOffset> opens = [t0, t1, t2];

        var snapped = ChartDrawingSnapping.Nearest(
            value: t1.AddMinutes(4).Ticks / (double)TimeSpan.TicksPerMillisecond,
            candidates: opens,
            toDouble: t => t.Ticks / (double)TimeSpan.TicksPerMillisecond);

        snapped.Should().Be(t1);
    }

    [Fact]
    public void Nearest_EmptyGrid_NoSnap()
    {
        IReadOnlyList<decimal> rungs = [];

        var snapped = ChartDrawingSnapping.Nearest(107.0, rungs, p => (double)p);

        snapped.Should().NotHaveValue("an empty grid never snaps.");
    }
}
