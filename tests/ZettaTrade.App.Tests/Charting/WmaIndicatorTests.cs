namespace ZettaTrade.App.Tests.Charting;

using System;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.Plugins.Abstractions;
using ZettaTrade.Plugins.Wma;

/// <summary>
/// The WMA plugin's golden vectors (T-092): the linearly weighted mean
/// (weights N..1 over the window, the sum N*(N+1)/2 as the denominator),
/// the warmup head and the documented constructor convention. Reference
/// source: hand-computed vectors over fixed closes; the repeated-decimal
/// expectations re-derive with the same decimal division, so the exact
/// 28-digit representation matches rather than a rounded stand-in.
/// </summary>
public sealed class WmaIndicatorTests
{
    #region Helpers

    private static IReadOnlyList<IndicatorBar> MakeBars(params decimal[] closes)
    {
        return [.. closes.Select(close => new IndicatorBar(close, close, close, close))];
    }

    #endregion

    [Fact]
    public void Name_IsTheCanonicalWma()
    {
        new WmaIndicator().Name.Should().Be("WMA");
        new WmaIndicator(4).Name.Should().Be("WMA");
    }

    /// <summary>
    /// The hand-computed classic vector over the closes 1, 2, 3, 4, 5 at
    /// period 3: the weights run newest-heaviest (3 for the freshest close
    /// of the window, 2, 1 for the oldest) and the weight sum is
    /// 3*(3+1)/2 = 6, so the windows read (3*3+2*2+1*1)/6 = 14/6, (3*4+2*3
    /// +1*2)/6 = 20/6, (3*5+2*4+1*3)/6 = 26/6 - the repeated decimals
    /// re-derive with the same division, the comparison stays exact.
    /// </summary>
    [Fact]
    public void Apply_TheGoldenVector_WeightsTheNewestCloseHeaviest()
    {
        var indicator = new WmaIndicator(3);
        var bars = MakeBars(1, 2, 3, 4, 5);

        var values = indicator.Apply(bars);

        values[0].Should().BeNull("the warmup head");
        values[1].Should().BeNull("the warmup head");
        values[2].Should().Be(14m / 6m, "weights 3/2/1 over the closes 1, 2, 3 (the newest weighs 3)");
        values[3].Should().Be(20m / 6m, "weights 3/2/1 over the closes 2, 3, 4");
        values[4].Should().Be(26m / 6m, "weights 3/2/1 over the closes 3, 4, 5");
    }

    /// <summary>
    /// A constant window lands exactly on the closes' value: every ladder
    /// term doubles the weight, so the weighted mean collapses to the plain
    /// reading - the denominator holds the whole weighted sum (3+2+1 over
    /// the value 5).
    /// </summary>
    [Fact]
    public void Apply_TheConstantWindow_MeansTheClosesExactly()
    {
        var indicator = new WmaIndicator(3);
        var bars = MakeBars(5, 5, 5);

        indicator.Apply(bars).Should().Equal([null, null, 5m]);
    }

    [Fact]
    public void Apply_PeriodOne_MapsEveryCloseThrough()
    {
        new WmaIndicator(1).Apply(MakeBars(7, 9, 11))
            .Should().Equal([7m, 9m, 11m]);
    }

    [Fact]
    public void Apply_AShortSeries_IsPlainWarmup()
    {
        new WmaIndicator(5).Apply(MakeBars(1, 2))
            .Should().HaveCount(2).And.OnlyContain(value => !value.HasValue);
    }

    [Fact]
    public void Apply_AnEmptySeries_ProducesAnEmptySeries()
    {
        new WmaIndicator(3).Apply(MakeBars()).Should().BeEmpty();
    }

    /// <summary>
    /// The parameterless construction runs the classic default 20: over the
    /// closes 1..25 the 20th bar seeds on the first twenty closes. The
    /// hand arithmetic: the weighted sum is SUM(k*k) = 2870 over closes
    /// 1..20 (each close k rides its own ladder weight k) and 3080 over
    /// closes 2..21 (SUM(k*(k-1)) over the shifted window) - the whole
    /// window gains exactly one weighted unit (210/210) per rising bar,
    /// the exact repeated thirds re-derive with the same division.
    /// </summary>
    [Fact]
    public void Apply_TheParameterlessConstruction_RunsTheClassicDefaultPeriod()
    {
        var indicator = new WmaIndicator();
        var bars = MakeBars([.. Enumerable.Range(1, 25).Select(i => (decimal)i)]);

        var values = indicator.Apply(bars);

        values.Should().HaveCount(25);
        values.Take(19).Should().OnlyContain(value => !value.HasValue, "the seed completes at the 20th bar");
        values[19].Should().Be(2870m / 210m, "the newest-heaviest weighted mean of the closes 1..20");
        values[20].Should().Be(3080m / 210m, "the shifted window (closes 2..21) adds exactly one weighted unit");
    }

    [Fact]
    public void Ctor_TheBoundaryPeriods_AreAccepted()
    {
        var act = () => new WmaIndicator(WmaIndicator.MinPeriod);
        act.Should().NotThrow();

        act = () => new WmaIndicator(WmaIndicator.MaxPeriod);
        act.Should().NotThrow();
    }

    [Fact]
    public void Ctor_AnOutOfRangePeriod_ThrowsInsteadOfDefaulting()
    {
        var act = () => new WmaIndicator(0);
        act.Should().Throw<ArgumentOutOfRangeException>();

        act = () => new WmaIndicator(WmaIndicator.MaxPeriod + 1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
