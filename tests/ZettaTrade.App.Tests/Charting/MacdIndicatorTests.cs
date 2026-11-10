namespace ZettaTrade.App.Tests.Charting;

using System;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.Plugins.Abstractions;
using ZettaTrade.Plugins.Macd;

/// <summary>
/// The MACD plugin's golden vectors (T-092): the fast/slow EMA spread
/// through the classic seeded convention, the warmup head owned by the
/// slow EMA's seed, the catalog-facing ctor(fast) mapping (slow/signal pin
/// the canonical 26/9) and the fast-below-slow rejection. Reference source:
/// hand-computed vectors over fixed closes; the picked vector lengths keep
/// every alpha (2/(length+1)) a terminating decimal, so every expectation
/// is a whole or exact decimal number.
/// </summary>
public sealed class MacdIndicatorTests
{
    #region Helpers

    private static IReadOnlyList<IndicatorBar> MakeBars(params decimal[] closes)
    {
        return [.. closes.Select(close => new IndicatorBar(close, close, close, close))];
    }

    #endregion

    [Fact]
    public void Name_IsTheCanonicalMacd()
    {
        new MacdIndicator().Name.Should().Be("MACD");
        new MacdIndicator(3).Name.Should().Be("MACD");
    }

    /// <summary>
    /// The hand-computed vector over the closes 1, 2, 3, 4, 6, 8 at the
    /// trio (3, 4, 9): the fast EMA (alpha 0.5) walks 2, 3, 4.5, 6.25; the
    /// slow EMA (alpha 0.4) walks 2.5, 3.9, 5.54; the spread reads 0.5,
    /// 0.6, 0.71 from the slow seed onward - every step a terminating
    /// decimal.
    /// </summary>
    [Fact]
    public void Apply_TheGoldenVector_RendersTheFastSlowSpread()
    {
        var indicator = new MacdIndicator(3, 4, 9);
        var bars = MakeBars(1, 2, 3, 4, 6, 8);

        // fast: seed 2; +0.5*(4-2) = 3; +0.5*(6-3) = 4.5; +0.5*(8-4.5) = 6.25.
        // slow: seed 2.5; +0.4*(6-2.5) = 3.9; +0.4*(8-3.9) = 5.54.
        indicator.Apply(bars).Should()
            .Equal([null, null, null, 0.5m, 0.6m, 0.71m]);
    }

    /// <summary>
    /// The unit-rally series keeps a constant spread: the trio (3, 4, 9)
    /// over the closes 1..6 reads 0.5 on every bar from the slow seed
    /// onward (both averages rise exactly one per step, alpha steps on a
    /// linear series are exact).
    /// </summary>
    [Fact]
    public void Apply_TheUnitRallySeries_KeepsTheSpreadConstant()
    {
        var indicator = new MacdIndicator(3, 4, 9);
        var bars = MakeBars([.. Enumerable.Range(1, 6).Select(i => (decimal)i)]);

        indicator.Apply(bars).Should()
            .Equal([null, null, null, 0.5m, 0.5m, 0.5m]);
    }

    /// <summary>
    /// The catalog-facing ctor(fast) maps the row's period onto the FAST
    /// length while the slow stays the canonical 26: a six-bar series never
    /// reaches the slow seed, so every reading stays warmup - the knob
    /// clearly did not become the slow length.
    /// </summary>
    [Fact]
    public void Apply_TheCatalogFacingConstruction_PinsTheCanonicalSlowLength()
    {
        var indicator = new MacdIndicator(3);
        var bars = MakeBars([.. Enumerable.Range(1, 6).Select(i => (decimal)i)]);

        indicator.Apply(bars).Should()
            .HaveCount(6)
            .And.OnlyContain(value => !value.HasValue, "the canonical slow (26) owns the warmup here");
    }

    /// <summary>
    /// The parameterless construction runs the classic trio 12/26/9: the
    /// slow seed completes at bar 25 and the spread exists onward (a rally
    /// keeps the fast EMA above the slow one).
    /// </summary>
    [Fact]
    public void Apply_TheParameterlessConstruction_RunsTheClassicTrio()
    {
        var indicator = new MacdIndicator();
        var bars = MakeBars([.. Enumerable.Range(1, 27).Select(i => (decimal)i)]);

        var values = indicator.Apply(bars);

        values.Should().HaveCount(27);
        values.Take(25).Should().OnlyContain(value => !value.HasValue, "the slow seed completes at bar 25");
        values[25].Should().NotBeNull("the spread exists from the slow seed");
        values[26].Should().NotBeNull();
        values.Where(value => value.HasValue).Should()
            .OnlyContain(value => value > 0m, "a rally lifts the fast EMA above the slow one");
    }

    [Fact]
    public void Apply_AnEmptySeries_ProducesAnEmptySeries()
    {
        new MacdIndicator(3, 4, 9).Apply(MakeBars()).Should().BeEmpty();
    }

    [Fact]
    public void Ctor_TheCatalogFacingBoundaries_AreAccepted()
    {
        var act = () => new MacdIndicator(MacdIndicator.MaxFastPeriod);
        act.Should().NotThrow();

        act = () => new MacdIndicator(MacdIndicator.MinPeriod);
        act.Should().NotThrow();
    }

    [Fact]
    public void Ctor_TheCatalogFacingOutOfRangeFast_ThrowsInsteadOfDefaulting()
    {
        var act = () => new MacdIndicator(0);
        act.Should().Throw<ArgumentOutOfRangeException>();

        act = () => new MacdIndicator(MacdIndicator.CanonicalSlowPeriod);
        act.Should().Throw<ArgumentException>("the fast length must stay below the canonical slow");
    }

    [Fact]
    public void Ctor_AnInvertedTrio_ThrowsInsteadOfDefaulting()
    {
        var act = () => new MacdIndicator(4, 4, 9);
        act.Should().Throw<ArgumentException>();

        act = () => new MacdIndicator(5, 4, 9);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Ctor_AOutOfRangeTrioLength_ThrowsInsteadOfDefaulting()
    {
        var act = () => new MacdIndicator(3, 0, 9);
        act.Should().Throw<ArgumentOutOfRangeException>();

        act = () => new MacdIndicator(3, 4, MacdIndicator.MaxPeriod + 1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
