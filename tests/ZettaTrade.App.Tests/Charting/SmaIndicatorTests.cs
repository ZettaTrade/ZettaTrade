namespace ZettaTrade.App.Tests.Charting;

using System;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.Plugins.Abstractions;
using ZettaTrade.Plugins.Sma;

/// <summary>
/// The SMA plugin's golden vectors: the rolling-mean semantics over the
/// closes, the warmup head (nulls before the period fills), the boundary
/// periods and the documented constructor convention (the ctor(int) the
/// loader prefers, the parameterless default fallback).
/// </summary>
public sealed class SmaIndicatorTests
{
    #region Helpers

    private static IReadOnlyList<IndicatorBar> MakeBars(params decimal[] closes)
    {
        return [.. closes.Select(close => new IndicatorBar(close, close, close, close))];
    }

    #endregion

    [Fact]
    public void Name_IsTheCanonicalSma()
    {
        new SmaIndicator().Name.Should().Be("SMA");
        new SmaIndicator(4).Name.Should().Be("SMA");
    }

    [Fact]
    public void Apply_TheConstantSeries_RollsTheMeanExactly()
    {
        var indicator = new SmaIndicator(3);
        var bars = MakeBars(100, 100, 100, 100, 100);

        indicator.Apply(bars).Should().Equal([null, null, 100m, 100m, 100m]);
    }

    [Fact]
    public void Apply_TheGoldenVector_WalksTheWindow()
    {
        var indicator = new SmaIndicator(3);
        var bars = MakeBars(1, 2, 3, 4, 5);

        // 1+2+3=6/3=2, 2+3+4=9/3=3, 3+4+5=12/3=4.
        indicator.Apply(bars).Should().Equal([null, null, 2m, 3m, 4m]);
    }

    [Fact]
    public void Apply_TheOutputLength_AlwaysMatchesTheInput()
    {
        var indicator = new SmaIndicator(5);
        var bars = MakeBars(1, 2);

        var values = indicator.Apply(bars);

        values.Should().HaveCount(2).And.OnlyContain(value => !value.HasValue);
    }

    [Fact]
    public void Apply_AnEmptySeries_ProducesAnEmptySeries()
    {
        new SmaIndicator(3).Apply(MakeBars()).Should().BeEmpty();
    }

    [Fact]
    public void Apply_PeriodOne_MapsEveryCloseThrough()
    {
        var indicator = new SmaIndicator(1);
        var bars = MakeBars(7, 9, 11);

        indicator.Apply(bars).Should().Equal([7m, 9m, 11m]);
    }

    [Fact]
    public void Apply_TheParameterlessConstruction_RunsTheClassicDefaultPeriod()
    {
        var indicator = new SmaIndicator();
        var bars = MakeBars([.. Enumerable.Range(1, 25).Select(i => (decimal)i)]);

        var values = indicator.Apply(bars);

        values.Should().HaveCount(25);
        values[19].Should().Be(10.5m, "the mean of closes 1..20");
        values[20].Should().Be(11.5m, "the window advanced one bar");
        values.Take(19).Should().OnlyContain(value => !value.HasValue);
    }

    [Fact]
    public void Ctor_TheBoundaryPeriods_AreAccepted()
    {
        var act = () => new SmaIndicator(SmaIndicator.MinPeriod);
        act.Should().NotThrow();

        act = () => new SmaIndicator(SmaIndicator.MaxPeriod);
        act.Should().NotThrow();
    }

    [Fact]
    public void Ctor_AnOutOfRangePeriod_ThrowsInsteadOfDefaulting()
    {
        var act = () => new SmaIndicator(0);
        act.Should().Throw<ArgumentOutOfRangeException>();

        act = () => new SmaIndicator(SmaIndicator.MaxPeriod + 1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
