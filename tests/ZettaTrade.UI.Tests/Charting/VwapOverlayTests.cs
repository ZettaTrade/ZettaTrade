namespace ZettaTrade.UI.Tests.Charting;

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;

/// <summary>
/// Pins for the session-anchored VWAP overlay compute (T-093): the exact
/// golden numbers on crafted candle sets, the session reset at the calendar
/// -day boundary, the zero-share bar's carried value and the honest
/// no-volume state. Pure facts — the canvas itself is UIA-invisible.
/// </summary>
public sealed class VwapOverlayTests
{
    /// <summary>The day the goldens build their sessions on (UTC, offset zero).</summary>
    private static readonly DateTimeOffset DayStart = new(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);

    /// <summary>One full day later in UTC: the exact day boundary.</summary>
    private static readonly DateTimeOffset NextDayStart = DayStart.AddDays(1);

    /// <summary>A bar that prints exactly the given typical price (the high,
    /// low and close all carry it) with the given volume.</summary>
    private static Bar FlatBar(DateTimeOffset time, decimal typical, decimal volume) =>
        new(time, typical, typical, typical, typical, volume);

    #region Exact Goldens

    /// <summary>
    /// The exact golden: two same-session bars (equal weights 1 and typical
    /// prices 10 and 14) give an exact decimal VWAP of 12 and an exact ±1σ
    /// of 2 (σ² = (10²+14²)/2 − 12² = 148 − 144 = 4), because the choices
    /// keep every intermediate rational inside decimal's exact range.
    /// </summary>
    [Fact]
    public void Compute_CraftedKeepsEveryRationalExact_CenterAndBandsAreExact()
    {
        var bars = new[]
        {
            FlatBar(DayStart.AddHours(9), 10m, 1m),
            FlatBar(DayStart.AddHours(10), 14m, 1m),
        };

        var overlay = VwapOverlay.Compute(bars);

        overlay.State.Should().Be(VwapOverlayState.Computed);
        overlay.SessionStarts.Should().Equal([0]);
        overlay.Center[0].Should().Be(10m, "the first bar alone is its own session VWAP");
        overlay.Center[1].Should().Be(12m, "(10·1 + 14·1) / 2 — exact in decimal");
        overlay.Lower[0].Should().Be(10m, "one bar carries zero variance: the band is the VWAP itself");
        overlay.Upper[0].Should().Be(10m);
        overlay.Lower[1].Should().Be(10m, "12 − σ where σ = 2 exactly");
        overlay.Upper[1].Should().Be(14m, "12 + σ");
    }

    /// <summary>
    /// The boundary golden: the VWAP accumulates within one UTC calendar day
    /// and resets on the day-crossing bar — the third bar's session starts
    /// from zero and the old day's volume leaks no weight into it.
    /// </summary>
    [Fact]
    public void Compute_CrossingTheUtcDayBoundary_ResetsTheAccumulationExactly()
    {
        var bars = new[]
        {
            FlatBar(DayStart.AddHours(22), 10m, 1m),
            FlatBar(DayStart.AddHours(23), 14m, 1m),
            FlatBar(NextDayStart, 20m, 3m),
            FlatBar(NextDayStart.AddHours(1), 12m, 1m),
        };

        var overlay = VwapOverlay.Compute(bars);

        overlay.SessionStarts.Should().Equal([0, 2], "the day-2 open starts a fresh session");
        overlay.Center[0].Should().Be(10m);
        overlay.Center[1].Should().Be(12m, "the day-1 accumulation (10·1 + 14·1) / 2");
        overlay.Center[2].Should().Be(20m, "the reset: only day 2's first bar weighs the value");
        overlay.Center[3].Should().Be(18m, "(20·3 + 12·1) / 4 — exact in decimal");

        // Day 1's σ must not leak past the boundary either.
        overlay.Upper[2].Should().Be(20m, "the fresh session's first bar carries zero variance");
        overlay.Lower[2].Should().Be(20m);
    }

    /// <summary>
    /// A zero-volume bar's real weight is a zero share: the running session
    /// VWAP stands unchanged on its index (never zero-fabricated) and the
    /// next weighted bar continues the session.
    /// </summary>
    [Fact]
    public void Compute_AZeroVolumeBarInsideARunningSession_CarriesTheValueStanding()
    {
        var bars = new[]
        {
            FlatBar(DayStart.AddHours(9), 10m, 1m),
            FlatBar(DayStart.AddHours(10), 50m, 0m),
            FlatBar(DayStart.AddHours(11), 20m, 1m),
        };

        var overlay = VwapOverlay.Compute(bars);

        overlay.State.Should().Be(VwapOverlayState.Computed);
        overlay.Center[0].Should().Be(10m);
        overlay.Center[1].Should().Be(10m, "the zero-weight bar moves nothing");
        overlay.Center[2].Should().Be(15m, "(10·1 + 20·1) / 2 — the zero-volume bar added nothing");
    }

    /// <summary>
    /// The pre-volume warmup: a session that has accumulated no volume yet
    /// (the series opens on zero-volume bars) holds nulls until the first
    /// weighted bar defines the value.
    /// </summary>
    [Fact]
    public void Compute_ASessionWithoutVolumeYet_HoldsNullsUntilTheFirstWeightedBar()
    {
        var bars = new[]
        {
            FlatBar(DayStart.AddHours(9), 10m, 0m),
            FlatBar(DayStart.AddHours(10), 20m, 2m),
        };

        var overlay = VwapOverlay.Compute(bars);

        overlay.State.Should().Be(VwapOverlayState.Computed, "the second bar carries volume");
        overlay.Center[0].Should().BeNull("nothing accumulated before the first weighted bar");
        overlay.Center[1].Should().Be(20m);
        overlay.Upper[0].Should().BeNull();
        overlay.Lower[0].Should().BeNull();
    }

    #endregion

    #region Honest No-Volume State

    /// <summary>The whole series without volume: the no-volume state, every
    /// value null, the session record still the honest series shape.</summary>
    [Theory]
    [InlineData(3)]
    [InlineData(11)]
    public void Compute_ASeriesWithNoVolumeAtAll_ReportsTheNoVolumeState(int barCount)
    {
        var bars = Enumerable.Range(0, barCount)
            .Select(i => FlatBar(DayStart.AddHours(i), 10m + i, 0m))
            .ToArray();

        var overlay = VwapOverlay.Compute(bars);

        overlay.State.Should().Be(VwapOverlayState.NoVolumeData);
        overlay.Center.Should().HaveCount(barCount).And.OnlyContain(value => !value.HasValue);
        overlay.Upper.Should().HaveCount(barCount).And.OnlyContain(value => !value.HasValue);
        overlay.Lower.Should().HaveCount(barCount).And.OnlyContain(value => !value.HasValue);
        overlay.SessionStarts.Should().Equal([0], "the session record describes the series, not the values");
    }

    /// <summary>An empty series computes the no-volume state with no values at all.</summary>
    [Fact]
    public void Compute_AnEmptySeries_IsTheNoVolumeState()
    {
        var overlay = VwapOverlay.Compute([]);

        overlay.State.Should().Be(VwapOverlayState.NoVolumeData);
        overlay.Center.Should().BeEmpty();
        overlay.Upper.Should().BeEmpty();
        overlay.Lower.Should().BeEmpty();
        overlay.SessionStarts.Should().BeEmpty();
    }

    #endregion

    #region Slice

    /// <summary>
    /// The chart slices the overlay with its visible window: the values
    /// re-slice by index and the session starts rebase, so a session break
    /// inside the window keeps breaking the drawn polyline at the same bar.
    /// </summary>
    [Fact]
    public void Slice_TheVisibleWindow_RebasesTheSessionsWithTheValues()
    {
        var bars = new[]
        {
            FlatBar(DayStart.AddHours(22), 10m, 1m),
            FlatBar(DayStart.AddHours(23), 14m, 1m),
            FlatBar(NextDayStart, 20m, 3m),
            FlatBar(NextDayStart.AddHours(1), 12m, 1m),
        };

        var overlay = VwapOverlay.Compute(bars).Slice(1, 4);

        overlay.Center.Should().Equal([12m, 20m, 18m]);
        overlay.SessionStarts.Should().Equal([1], "the day-2 break sits at the window's second bar");
        overlay.State.Should().Be(VwapOverlayState.Computed);
    }

    /// <summary>A window out of the value range rejects instead of clamping.</summary>
    [Theory]
    [InlineData(-1, 2)]
    [InlineData(2, 1)]
    [InlineData(0, 17)]
    public void Slice_AWindowOutsideTheRange_Throws(int start, int end)
    {
        var overlay = VwapOverlay.Compute(
            [FlatBar(DayStart, 10m, 1m), FlatBar(DayStart.AddHours(1), 14m, 1m)]);

        var act = () => overlay.Slice(start, end);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    #endregion
}
