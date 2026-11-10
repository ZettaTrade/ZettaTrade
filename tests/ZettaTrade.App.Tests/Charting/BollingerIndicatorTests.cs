namespace ZettaTrade.App.Tests.Charting;

using System;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.Plugins.Abstractions;
using ZettaTrade.Plugins.Bollinger;

/// <summary>
/// The Bollinger plugin's golden vectors (T-092): the upper band the seam
/// renders (middle SMA + k sigma of the same window, the population
/// deviation), the warmup head and the parameter conventions. Reference
/// source: hand-computed vectors over fixed closes. Where the window
/// deviation is irrational the expectation recomputes through the same
/// decimal -> double -> decimal square-root pipeline the formula pins, so
/// the comparison stays byte-exact; windows with perfect-square
/// deviations assert whole decimal numbers directly.
/// </summary>
public sealed class BollingerIndicatorTests
{
    #region Helpers

    private static IReadOnlyList<IndicatorBar> MakeBars(params decimal[] closes)
    {
        return [.. closes.Select(close => new IndicatorBar(close, close, close, close))];
    }

    #endregion

    [Fact]
    public void Name_IsTheCanonicalBoll()
    {
        new BollingerIndicator().Name.Should().Be("BOLL");
        new BollingerIndicator(3).Name.Should().Be("BOLL");
    }

    /// <summary>
    /// The whole-number vector at window 2 (the classic count k = 2) over
    /// the closes 1, 5: the middle is 3, the population deviation of the
    /// pair is exactly 2, the upper band reads 3 + 2*2 = 7.
    /// </summary>
    [Fact]
    public void Apply_ThePerfectSquareWindow_ReadsTheWholeUpperBand()
    {
        var indicator = new BollingerIndicator(2);
        var bars = MakeBars(1, 5);

        // Middle (1+5)/2 = 3; deviation sqrt(((1-3)^2 + (5-3)^2)/2) = 2.
        indicator.Apply(bars).Should().Equal([null, 7m]);
    }

    /// <summary>
    /// The deviation count rides the full ctor: window 2 over the closes
    /// 1, 5 with k = 1 reads 3 + 1*2 = 5 and with k = 0.5 reads 3 + 0.5*2
    /// = 4 - the bands scale with the count, nothing else moves.
    /// </summary>
    [Fact]
    public void Apply_TheDeviationCount_ScalesTheBandAroundTheMiddle()
    {
        new BollingerIndicator(2, 1m).Apply(MakeBars(1, 5)).Should().Equal([null, 5m]);
        new BollingerIndicator(2, 0.5m).Apply(MakeBars(1, 5)).Should().Equal([null, 4m]);
    }

    /// <summary>
    /// The constant series reads the middle band itself: the deviation
    /// collapses to zero, the upper band hugs the SMA - a window whose
    /// spread is a whole number in every position.
    /// </summary>
    [Fact]
    public void Apply_TheConstantSeries_HugsTheMiddleBand()
    {
        var indicator = new BollingerIndicator(3);
        var bars = MakeBars(5, 5, 5, 5, 5);

        indicator.Apply(bars).Should().Equal([null, null, 5m, 5m, 5m]);
    }

    /// <summary>
    /// The classic 20/2 pair over the closes 1, 2, 3, 4, 5 at window 3:
    /// the middles read 2, 3, 4 and the deviation of every window is
    /// sqrt(2/3) - irrational, so each reading rides the shared
    /// decimal-to-double-to-decimal root pipeline (byte-exact, hand
    /// arithmetic: 2 + 2*sqrt(2/3), 3 + 2*sqrt(2/3), 4 + 2*sqrt(2/3)).
    /// </summary>
    [Fact]
    public void Apply_TheIrrationalWindow_RidesTheSharedRootPipeline()
    {
        var indicator = new BollingerIndicator(3);
        var bars = MakeBars(1, 2, 3, 4, 5);

        var values = indicator.Apply(bars);

        values[2].Should().Be(2m + 2m * (decimal)Math.Sqrt((double)(2m / 3m)));
        values[3].Should().Be(3m + 2m * (decimal)Math.Sqrt((double)(2m / 3m)));
        values[4].Should().Be(4m + 2m * (decimal)Math.Sqrt((double)(2m / 3m)));
    }

    /// <summary>
    /// The parameterless construction runs the classic 20/2: over the
    /// closes 1..22 the seed lands at bar 19 on the middle 10.5 with the
    /// window sum of squares 665 (the population interpreter), the upper
    /// reading rides the shared root pipeline.
    /// </summary>
    [Fact]
    public void Apply_TheParameterlessConstruction_RunsTheClassicPair()
    {
        var indicator = new BollingerIndicator();
        var bars = MakeBars([.. Enumerable.Range(1, 22).Select(i => (decimal)i)]);

        var values = indicator.Apply(bars);

        values.Should().HaveCount(22);
        values.Take(19).Should().OnlyContain(value => !value.HasValue, "the seed completes at bar 19");

        // Window 1..20: middle 10.5; sum of squared carries 665 over 20
        // positions = 33.25 exactly; the root is irrational - the same
        // root pipeline produces the value, the test mirrors it.
        values[19].Should().Be(10.5m + 2m * (decimal)Math.Sqrt((double)33.25m));
        values[20].Should().NotBeNull("the window advances on the next bar");
    }

    [Fact]
    public void Ctor_TheBoundaryWindows_AreAccepted()
    {
        var act = () => new BollingerIndicator(BollingerIndicator.MinPeriod);
        act.Should().NotThrow();

        act = () => new BollingerIndicator(BollingerIndicator.MaxPeriod);
        act.Should().NotThrow();
    }

    [Fact]
    public void Ctor_AnOutOfRangeWindow_ThrowsInsteadOfDefaulting()
    {
        var act = () => new BollingerIndicator(0);
        act.Should().Throw<ArgumentOutOfRangeException>();

        act = () => new BollingerIndicator(BollingerIndicator.MaxPeriod + 1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Ctor_ANonPositiveDeviationCount_ThrowsInsteadOfDefaulting()
    {
        var act = () => new BollingerIndicator(20, 0m);
        act.Should().Throw<ArgumentOutOfRangeException>();

        act = () => new BollingerIndicator(20, -1m);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
