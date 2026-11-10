namespace ZettaTrade.App.Tests.Charting;

using System;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.Plugins.Abstractions;
using ZettaTrade.Plugins.Atr;

/// <summary>
/// The ATR plugin's golden vectors (T-092): Wilder's true range (the wide
/// of the bar's own spread and the two gaps against the prior close, the
/// opening bar's range standing alone), the seeded first average and the
/// Wilder smoothing. Reference source: hand-computed vectors over fixed
/// OHLC bars, chosen so every step lands on a terminating decimal.
/// </summary>
public sealed class AtrIndicatorTests
{
    #region Helpers

    /// <summary>A bar from (open, high, low, close).</summary>
    private static IndicatorBar BarOf(
        decimal open,
        decimal high,
        decimal low,
        decimal close) => new(open, high, low, close);

    private static IReadOnlyList<IndicatorBar> MakeBars(params (decimal open, decimal high, decimal low, decimal close)[] pieces)
    {
        return [.. pieces.Select(piece => BarOf(piece.open, piece.high, piece.low, piece.close))];
    }

    /// <summary>Flat-candle bars (open=high=low=close) for the warmup-shape tests.</summary>
    private static IReadOnlyList<IndicatorBar> FlatCandleBars(params decimal[] closes)
    {
        return [.. closes.Select(close => BarOf(close, close, close, close))];
    }

    #endregion

    [Fact]
    public void Name_IsTheCanonicalAtr()
    {
        new AtrIndicator().Name.Should().Be("ATR");
        new AtrIndicator(2).Name.Should().Be("ATR");
    }

    /// <summary>
    /// The hand-computed vector at period 2 over three bars: the true
    /// ranges read 6 (the opening bar's plain range), 6 (its own spread
    /// wins over the gaps) and 4; the seed is their mean and the next step
    /// smooths (6*1+4)/2 = 5.
    /// </summary>
    [Fact]
    public void Apply_TheGoldenVector_SeedsWithTheMeanAndSmooths()
    {
        var indicator = new AtrIndicator(2);
        var bars = MakeBars(
            (open: 3, high: 7, low: 1, close: 5),
            (open: 5, high: 9, low: 3, close: 6),
            (open: 6, high: 4, low: 2, close: 3));

        // TR: 6 (no prior close), 6 (spread 6 beats gaps 4/2), 4 (gaps beat the spread 2).
        indicator.Apply(bars).Should().Equal([null, 6m, 5m]);
    }

    /// <summary>
    /// A period-3 vector with a gap-up bar: the true ranges read 4 (the
    /// opening spread), 8 (the 8-point gap above the prior close beats the
    /// 7-point spread), 3 and 5; the seed is their mean and the next Wilder
    /// step smooths (5*2+5)/3 = 5 - the hand arithmetic stays whole.
    /// </summary>
    [Fact]
    public void Apply_TheGapUpVector_KeepsTheWilderSmoothingWhole()
    {
        var indicator = new AtrIndicator(3);
        var bars = MakeBars(
            (open: 10, high: 12, low: 8, close: 10),
            (open: 10, high: 18, low: 11, close: 17),
            (open: 17, high: 18, low: 15, close: 16),
            (open: 16, high: 17, low: 12, close: 13));

        // TR: 4 (the opening spread), 8 (the 8-point gap above the prior
        // close beats the 7-point spread), 3, 5 -> seed 15/3 = 5, step
        // (5*2+5)/3 = 5.
        indicator.Apply(bars).Should().Equal([null, null, 5m, 5m]);
    }

    /// <summary>
    /// Period 1 reads the raw true ranges: a gap-DOWN bar (the open gapping
    /// below the prior close) owns the reading - the 3-point gap down beats
    /// the 1-point spread.
    /// </summary>
    [Fact]
    public void Apply_PeriodOne_ReadsTheRawTrueRanges()
    {
        var indicator = new AtrIndicator(1);
        var bars = MakeBars(
            (open: 4, high: 5, low: 3, close: 4),
            (open: 1, high: 2, low: 1, close: 2));

        // TR0 = 2 (no prior close), TR1 = max(1, 2, 3) = 3 (the gap down wins).
        indicator.Apply(bars).Should().Equal([2m, 3m]);
    }

    [Fact]
    public void Apply_AShortSeries_IsPlainWarmup()
    {
        var bars = MakeBars(
            (open: 3, high: 7, low: 1, close: 5),
            (open: 5, high: 9, low: 3, close: 6),
            (open: 6, high: 4, low: 2, close: 3));

        new AtrIndicator(5).Apply(bars)
            .Should().HaveCount(3).And.OnlyContain(value => !value.HasValue);
    }

    [Fact]
    public void Apply_AnEmptySeries_ProducesAnEmptySeries()
    {
        new AtrIndicator(3).Apply(MakeBars()).Should().BeEmpty();
    }

    /// <summary>
    /// The parameterless construction runs the classic Wilder 14: over
    /// flat-candle bars rising one per bar the true ranges are the unit
    /// steps (the opening bar carries the zero-range convention), so the
    /// seed is 13/14 and the reading stays pinned by the same fraction.
    /// </summary>
    [Fact]
    public void Apply_TheParameterlessConstruction_RunsTheClassicWilder14()
    {
        var indicator = new AtrIndicator();

        // Flat candles rising +1: TR0 = 0, TR(i>=1) = 1.
        var bars = FlatCandleBars([.. Enumerable.Range(1, 20).Select(i => (decimal)i)]);

        var values = indicator.Apply(bars);

        values.Should().HaveCount(20);
        values.Take(13).Should().OnlyContain(value => !value.HasValue, "the seed completes at bar 13");
        values[13].Should().Be(13m / 14m, "the mean of TR 0 and thirteen unit ranges");
        values[14].Should().NotBeNull("the Wilder smoothing runs onward");
    }

    [Fact]
    public void Ctor_TheBoundaryPeriods_AreAccepted()
    {
        var act = () => new AtrIndicator(AtrIndicator.MinPeriod);
        act.Should().NotThrow();

        act = () => new AtrIndicator(AtrIndicator.MaxPeriod);
        act.Should().NotThrow();
    }

    [Fact]
    public void Ctor_AnOutOfRangePeriod_ThrowsInsteadOfDefaulting()
    {
        var act = () => new AtrIndicator(0);
        act.Should().Throw<ArgumentOutOfRangeException>();

        act = () => new AtrIndicator(AtrIndicator.MaxPeriod + 1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
