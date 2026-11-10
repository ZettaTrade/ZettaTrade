namespace ZettaTrade.App.Tests.Charting;

using System;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.Plugins.Abstractions;
using ZettaTrade.Plugins.Ema;

/// <summary>
/// The EMA plugin's golden vectors (T-092): the classic seeded exponential
/// average (the seed is the plain mean of the first period closes, the
/// alpha 2/(N+1) applies one step per bar), the warmup head, the boundary
/// periods and the documented constructor convention. Reference source:
/// hand-computed vectors over fixed closes; where a step lands on a
/// non-terminating decimal the expectation re-derives it with the same
/// decimal arithmetic (the repeated digits are exact, not rounded floats).
/// </summary>
public sealed class EmaIndicatorTests
{
    #region Helpers

    private static IReadOnlyList<IndicatorBar> MakeBars(params decimal[] closes)
    {
        return [.. closes.Select(close => new IndicatorBar(close, close, close, close))];
    }

    #endregion

    [Fact]
    public void Name_IsTheCanonicalEma()
    {
        new EmaIndicator().Name.Should().Be("EMA");
        new EmaIndicator(4).Name.Should().Be("EMA");
    }

    /// <summary>
    /// The hand-computed classic vector over the closes 1, 2, 3, 4, 5 at
    /// period 3: the seed is the mean of the first three closes (2), alpha
    /// = 2/4 = 0.5 makes every step land on a whole number here.
    /// </summary>
    [Fact]
    public void Apply_TheGoldenVector_SeedsWithTheMeanAndWalksTheAlphaStep()
    {
        var indicator = new EmaIndicator(3);
        var bars = MakeBars(1, 2, 3, 4, 5);

        // Seed: (1+2+3)/3 = 2; then +0.5*(4-2) = 3; then +0.5*(5-3) = 4.
        indicator.Apply(bars).Should().Equal([null, null, 2m, 3m, 4m]);
    }

    [Fact]
    public void Apply_TheConstantSeries_HoldsTheMean()
    {
        new EmaIndicator(3).Apply(MakeBars(100, 100, 100, 100, 100))
            .Should().Equal([null, null, 100m, 100m, 100m]);
    }

    [Fact]
    public void Apply_PeriodOne_MapsEveryCloseThrough()
    {
        new EmaIndicator(1).Apply(MakeBars(7, 9, 11))
            .Should().Equal([7m, 9m, 11m]);
    }

    [Fact]
    public void Apply_AShortSeries_IsPlainWarmup()
    {
        new EmaIndicator(5).Apply(MakeBars(1, 2))
            .Should().HaveCount(2).And.OnlyContain(value => !value.HasValue);
    }

    [Fact]
    public void Apply_AnEmptySeries_ProducesAnEmptySeries()
    {
        new EmaIndicator(3).Apply(MakeBars()).Should().BeEmpty();
    }

    /// <summary>
    /// The parameterless construction runs the classic default 20: over the
    /// closes 1..25 the seed is the mean of 1..20 (10.5) and the first
    /// alpha step (2/21 over the gap 21-10.5 = 10.5) advances the exact
    /// +1 hand reference to within decimal rounding - the step's repeated
    /// digits carry sub-unit-error noise only.
    /// </summary>
    [Fact]
    public void Apply_TheParameterlessConstruction_RunsTheClassicDefaultPeriod()
    {
        var indicator = new EmaIndicator();
        var bars = MakeBars([.. Enumerable.Range(1, 25).Select(i => (decimal)i)]);

        var values = indicator.Apply(bars);

        values.Should().HaveCount(25);
        values.Take(19).Should().OnlyContain(value => !value.HasValue, "the seed completes at the 20th bar");
        values[19].Should().Be(10.5m, "the mean of closes 1..20");
        values[20].Should().BeApproximately(11.5m, 0.000001m, "the alpha step is exactly +1 per rising bar");
    }

    [Fact]
    public void Ctor_TheBoundaryPeriods_AreAccepted()
    {
        var act = () => new EmaIndicator(EmaIndicator.MinPeriod);
        act.Should().NotThrow();

        act = () => new EmaIndicator(EmaIndicator.MaxPeriod);
        act.Should().NotThrow();
    }

    [Fact]
    public void Ctor_AnOutOfRangePeriod_ThrowsInsteadOfDefaulting()
    {
        var act = () => new EmaIndicator(0);
        act.Should().Throw<ArgumentOutOfRangeException>();

        act = () => new EmaIndicator(EmaIndicator.MaxPeriod + 1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
