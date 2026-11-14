namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;

/// <summary>
/// Golden tests for the pure MAE/MFE math (T-251): the long's adverse
/// excursion reads the dips below the entry and the short's the reaches
/// above it (the mirror rule), both excursions floor at zero, a window
/// without bars or a non-positive entry derives nothing (the honest
/// absence), the read is bar-order independent, and the binary window
/// slice keeps its [<c>from</c>, <c>to</c>) contract — the <c>from</c> bar
/// in, the <c>to</c> bar out, an empty or inverted window slices nothing.
/// </summary>
public sealed class TradeExcursionMathTests
{
    #region Test Constants

    private static readonly DateTimeOffset BaseTime = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    #endregion Test Constants

    #region Helpers

    private static Bar Bar(int hour, decimal high, decimal low) =>
        new(BaseTime.AddHours(hour), low, high, low, high, 1m);

    #endregion Helpers

    #region Derive

    [Fact]
    public void LongTrade_ReadsTheDipsBelowAndTheReachesAboveTheEntry()
    {
        // Entry 100: bar 1 dips to 90 and reaches 105, bar 2 dips to 95 and
        // reaches 120 — the adverse excursion is the deepest dip (10), the
        // favorable one the highest reach (20).
        var derived = TradeExcursionMath.Derive(
            100m,
            isLong: true,
            new[]
            {
                Bar(0, 105m, 90m),
                Bar(1, 120m, 95m),
            });

        derived.Should().NotBeNull();
        derived!.Value.Mae.Should().Be(10m);
        derived.Value.Mfe.Should().Be(20m);
    }

    [Fact]
    public void ShortTrade_MirrorsBothExcursionsAroundTheEntry()
    {
        // Entry 100, short: the price going ABOVE the entry hurts (the reach
        // 20 is adverse), the fall below it rewards (the dip 10 is favorable).
        var derived = TradeExcursionMath.Derive(
            100m,
            isLong: false,
            new[]
            {
                Bar(0, 105m, 90m),
                Bar(1, 120m, 95m),
            });

        derived.Should().NotBeNull();
        derived!.Value.Mae.Should().Be(20m);
        derived.Value.Mfe.Should().Be(10m);
    }

    [Fact]
    public void ExcursionsFloorAtZero_NeverNegative()
    {
        // A long over bars that only rise above the entry: the price never
        // moved against the trade — the excursion is zero, not negative.
        var derived = TradeExcursionMath.Derive(
            100m,
            isLong: true,
            new[] { Bar(0, 110m, 105m) });

        derived.Should().NotBeNull();
        derived!.Value.Mae.Should().Be(0m);
        derived.Value.Mfe.Should().Be(10m);
    }

    [Fact]
    public void EmptyWindow_DerivesNothing()
    {
        TradeExcursionMath.Derive(100m, true, Array.Empty<Bar>()).Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NonPositiveEntry_DerivesNothing(decimal entryPrice)
    {
        TradeExcursionMath.Derive(
            entryPrice,
            true,
            new[] { Bar(0, 110m, 90m) }).Should().BeNull(
            "a fabricated excursion over an unusable entry is a lie");
    }

    [Fact]
    public void TheReadIsBarOrderIndependent()
    {
        var bars = new[]
        {
            Bar(0, 105m, 90m),
            Bar(1, 120m, 95m),
        };
        var ascending = TradeExcursionMath.Derive(100m, true, bars);
        var shuffled = TradeExcursionMath.Derive(100m, true, bars.Reverse().ToArray());

        shuffled.Should().Be(ascending, "the excursion reads every bar, the order never matters");
    }

    #endregion Derive

    #region SliceWindow

    [Fact]
    public void SliceKeepsTheFromBarAndExcludesTheToBar()
    {
        var bars = Enumerable.Range(0, 5)
            .Select(hour => Bar(hour, 100m + hour, 99m + hour))
            .ToArray();

        // The window [01:00, 03:00): the 01:00 bar in, the 03:00 bar out.
        var slice = TradeExcursionMath.SliceWindow(
            bars,
            BaseTime.AddHours(1),
            BaseTime.AddHours(3));

        slice.Select(bar => bar.Time).Should().Equal(
            BaseTime.AddHours(1),
            BaseTime.AddHours(2));
    }

    [Fact]
    public void InvertedOrEmptyWindow_SlicesNothing()
    {
        var bars = new[] { Bar(0, 100m, 99m) };

        TradeExcursionMath.SliceWindow(bars, BaseTime.AddHours(2), BaseTime.AddHours(1))
            .Should().BeEmpty("to <= from is no window at all");
        TradeExcursionMath.SliceWindow(bars, BaseTime.AddHours(2), BaseTime.AddHours(2))
            .Should().BeEmpty();
    }

    [Fact]
    public void ASixFigureSeries_SlicesBySearch_NeverByScan()
    {
        // 100_000 hourly bars; the slice still lands exactly on its bounds —
        // the binary search contract the six-figure histories ride.
        var bars = Enumerable.Range(0, 100_000)
            .Select(hour => Bar(hour, 100m, 99m))
            .ToArray();

        var slice = TradeExcursionMath.SliceWindow(
            bars,
            BaseTime.AddHours(50_000),
            BaseTime.AddHours(50_010));

        slice.Should().HaveCount(10);
        slice.First().Time.Should().Be(BaseTime.AddHours(50_000));
        slice.Last().Time.Should().Be(BaseTime.AddHours(50_009));
    }

    [Fact]
    public void AWindowOutsideTheSeries_SlicesNothing()
    {
        var bars = new[] { Bar(0, 100m, 99m) };

        TradeExcursionMath.SliceWindow(bars, BaseTime.AddHours(10), BaseTime.AddHours(12))
            .Should().BeEmpty();
    }

    #endregion SliceWindow
}
