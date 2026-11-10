namespace ZettaTrade.App.Tests.Charting;

using System;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.Plugins.Abstractions;
using ZettaTrade.Plugins.Rsi;

/// <summary>
/// The RSI plugin's golden vectors (T-092): Wilder's conventions - the
/// seeded first average over the first N close-to-close changes, the
/// Wilder smoothing (prev*(N-1)+move)/N, the gain/loss fraction and the
/// zero-loss shortcut. Reference source: hand-computed vectors over fixed
/// closes; the repeated-decimal readings re-derive with the same decimal
/// arithmetic the formula pins (the classic StockCharts/TA-Lib form:
/// RSI = 100 * avgGain / (avgGain + avgLoss)).
/// </summary>
public sealed class RsiIndicatorTests
{
    #region Helpers

    private static IReadOnlyList<IndicatorBar> MakeBars(params decimal[] closes)
    {
        return [.. closes.Select(close => new IndicatorBar(close, close, close, close))];
    }

    #endregion

    [Fact]
    public void Name_IsTheCanonicalRsi()
    {
        new RsiIndicator().Name.Should().Be("RSI");
        new RsiIndicator(2).Name.Should().Be("RSI");
    }

    /// <summary>
    /// The hand-computed classic vector over the closes 1, 3, 2, 4, 3, 5 at
    /// period 2. Changes: +2, -1, +2, -1, +2. Seed over the first pair of
    /// changes: avgGain = 2/2 = 1, avgLoss = 1/2 = 0.5 -> 66 2/3; then the
    /// Wilder steps: (1, 0.25) -> 85 5/7, (0.75, 0.625) -> 600/11,
    /// (1.375, 0.3125) -> 2200/27 - each fraction re-derives with the same
    /// decimal operations, so the comparison is exact.
    /// </summary>
    [Fact]
    public void Apply_TheGoldenVector_WalksWilderSmoothing()
    {
        var indicator = new RsiIndicator(2);
        var bars = MakeBars(1, 3, 2, 4, 3, 5);

        var values = indicator.Apply(bars);

        values[0].Should().BeNull("bar 0 carries no change, the seed is two bars away");
        values[1].Should().BeNull("the seed completes at bar 2");
        values[2].Should().Be(100m * 1m / 1.5m, "seed: gains 2/2 wins over losses 1/2");
        values[3].Should().Be(100m * 1.5m / 1.75m, "wilder step: (1,0.5)+(+2,0) halves");
        values[4].Should().Be(100m * 0.75m / 1.375m, "wilder step: (1.5,0.25)+(-1,1) halves");
        values[5].Should().Be(100m * 1.375m / 1.6875m, "wilder step: (0.75,0.625)+(+2,0) halves");
    }

    /// <summary>
    /// A strictly rising series reads 100 from the seed onward: the losses
    /// stay zero, the shortcut pins the reading instead of dividing by
    /// zero (the classic zero-loss reading).
    /// </summary>
    [Fact]
    public void Apply_TheRisingSeries_PinsAt100ThroughTheZeroLossShortcut()
    {
        var indicator = new RsiIndicator(3);
        var bars = MakeBars(1, 2, 3, 4, 5, 6, 7);

        indicator.Apply(bars).Should()
            .Equal([null, null, null, 100m, 100m, 100m, 100m]);
    }

    /// <summary>
    /// A strictly falling series reads 0 from the seed onward: the same
    /// fraction with a zero numerator.
    /// </summary>
    [Fact]
    public void Apply_TheFallingSeries_PinsAt0ThroughTheZeroGainFraction()
    {
        var indicator = new RsiIndicator(3);
        var bars = MakeBars(7, 6, 5, 4, 3, 2, 1);

        indicator.Apply(bars).Should()
            .Equal([null, null, null, 0m, 0m, 0m, 0m]);
    }

    /// <summary>
    /// A constant series carries no move at all: the zero-loss shortcut
    /// still applies (the closer 100 pins the no-loss reading - the flat
    /// market behavior the reference implementations ship).
    /// </summary>
    [Fact]
    public void Apply_TheConstantSeries_KeepsTheZeroLossShortcutExact()
    {
        var indicator = new RsiIndicator(2);
        var bars = MakeBars(5, 5, 5, 5, 5, 5);

        indicator.Apply(bars).Should()
            .Equal([null, null, 100m, 100m, 100m, 100m]);
    }

    /// <summary>
    /// A period-1 run reads the raw per-bar index: a lone rise pins 100, a
    /// lone fall pins 0, the mirror flips back on the next step.
    /// </summary>
    [Fact]
    public void Apply_PeriodOne_ReadsTheRawPerBarIndex()
    {
        var indicator = new RsiIndicator(1);
        var bars = MakeBars(10, 20, 10, 20);

        // Changes: +10, -10, +10; per-bar averages: (10, 0), (0, 10),
        // (10, 0) - the readings land on the zeros and hundreds.
        indicator.Apply(bars).Should().Equal([null, 100m, 0m, 100m]);
    }

    [Fact]
    public void Apply_AnEmptySeries_ProducesAnEmptySeries()
    {
        new RsiIndicator(3).Apply(MakeBars()).Should().BeEmpty();
    }

    /// <summary>
    /// The parameterless construction runs the classic Wilder 14: over the
    /// closes 1..30 (a unit rise per bar) the seed lands at bar 14 and
    /// every reading pins 100 through the zero-loss shortcut.
    /// </summary>
    [Fact]
    public void Apply_TheParameterlessConstruction_RunsTheClassicWilder14()
    {
        var indicator = new RsiIndicator();
        var bars = MakeBars([.. Enumerable.Range(1, 30).Select(i => (decimal)i)]);

        var values = indicator.Apply(bars);

        values.Should().HaveCount(30);
        values.Take(14).Should().OnlyContain(value => !value.HasValue, "the seed completes at bar 14");
        values.Skip(14).Should().OnlyContain(value => value == 100m, "every bar rises, the losses stay zero");
    }

    [Fact]
    public void Ctor_TheBoundaryPeriods_AreAccepted()
    {
        var act = () => new RsiIndicator(RsiIndicator.MinPeriod);
        act.Should().NotThrow();

        act = () => new RsiIndicator(RsiIndicator.MaxPeriod);
        act.Should().NotThrow();
    }

    [Fact]
    public void Ctor_AnOutOfRangePeriod_ThrowsInsteadOfDefaulting()
    {
        var act = () => new RsiIndicator(0);
        act.Should().Throw<ArgumentOutOfRangeException>();

        act = () => new RsiIndicator(RsiIndicator.MaxPeriod + 1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
