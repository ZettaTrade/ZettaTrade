namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;

/// <summary>
/// The pure trend classifier's goldens (T-239): a steady rise classifies
/// bull, a steady fall bear, an oscillation range — and the veto discipline
/// holds: three legs must AGREE, so a strong strength leg and a rising
/// slope are not enough when the swing structure disagrees (the spike
/// fixture prints higher extremes in its older half, the gentle rise in
/// its newer half, and the verdict stays the honest range). A series too
/// short for any leg throws — the warming state's territory, never a
/// fabricated verdict.
/// </summary>
public sealed class TrendClassifierTests
{
    /// <summary>The shipped tuning's minimum: 28 ADX bars + the 40-bar swing window + the 5-bar slope window.</summary>
    private const int DefaultMinimum = 73;

    /// <summary>A steady one-directional series: every bar's range rides the close ±1.</summary>
    private static List<Bar> Monotone(int count, decimal start, decimal step)
    {
        var bars = new List<Bar>(count);
        var close = start;
        for (var index = 0; index < count; index++)
        {
            bars.Add(new Bar(
                DateTimeOffset.UnixEpoch.AddDays(index),
                close,
                close + 1m,
                close - 1m,
                close,
                1000m));
            close += step;
        }

        return bars;
    }

    /// <summary>An oscillating series: the closes alternate base−1 / base+1, every leg flat.</summary>
    private static List<Bar> Oscillating(int count, decimal basePrice = 100m)
    {
        var bars = new List<Bar>(count);
        for (var index = 0; index < count; index++)
        {
            var close = basePrice + (index % 2 == 0 ? -1m : 1m);
            bars.Add(new Bar(
                DateTimeOffset.UnixEpoch.AddDays(index),
                close,
                close + 1m,
                close - 1m,
                close,
                1000m));
        }

        return bars;
    }

    /// <summary>
    /// The spike fixture: a sharp rise to 200 and a deep fall to 60 inside
    /// the swing window's OLDER half, then a gentle rise out of the bottom
    /// in the NEWER half. The slope leg reads the gentle rise up, the ADX
    /// leg reads the one-directional tail strong — but the newer half's
    /// extremes print both lower highs and lower lows against the older
    /// half's spike, so the structure leg vetoes and the verdict stays
    /// range.
    /// </summary>
    private static List<Bar> SpikeThenGentleRise()
    {
        var bars = new List<Bar>(73);
        for (var index = 0; index <= 35; index++)
        {
            var close = 100m + index * 2.85m;
            bars.Add(new Bar(DateTimeOffset.UnixEpoch.AddDays(index), close, close + 1m, close - 1m, close, 1000m));
        }

        for (var index = 36; index <= 55; index++)
        {
            var close = 199.75m - (index - 35) * 7m;
            bars.Add(new Bar(DateTimeOffset.UnixEpoch.AddDays(index), close, close + 1m, close - 1m, close, 1000m));
        }

        for (var index = 56; index <= 72; index++)
        {
            var close = 59.75m + (index - 55) * 2.5m;
            bars.Add(new Bar(DateTimeOffset.UnixEpoch.AddDays(index), close, close + 1m, close - 1m, close, 1000m));
        }

        return bars;
    }

    /// <summary>A steady rise classifies bull: every leg agrees upward.</summary>
    [Fact]
    public void ASteadyRise_ClassifiesBull()
    {
        var verdict = TrendClassifier.Classify(Monotone(DefaultMinimum, 100m, 1m), 20, 14, 25m);

        verdict.Should().Be(MtfTrendState.Bull);
    }

    /// <summary>A steady fall classifies bear: every leg agrees downward.</summary>
    [Fact]
    public void ASteadyFall_ClassifiesBear()
    {
        var verdict = TrendClassifier.Classify(Monotone(DefaultMinimum, 100m, -1m), 20, 14, 25m);

        verdict.Should().Be(MtfTrendState.Bear);
    }

    /// <summary>An oscillation classifies range: the strength leg reads no trend.</summary>
    [Fact]
    public void AnOscillation_ClassifiesRange()
    {
        var verdict = TrendClassifier.Classify(Oscillating(DefaultMinimum), 20, 14, 25m);

        verdict.Should().Be(MtfTrendState.Range);
    }

    /// <summary>
    /// The structure vetoes the direction: a rising slope over a strong
    /// one-directional tail still stays range when the swing structure
    /// prints lower extremes against the older half's spike.
    /// </summary>
    [Fact]
    public void TheStructureVeto_KeepsTheDirectionOut_WhenTheExtremesDisagree()
    {
        var verdict = TrendClassifier.Classify(SpikeThenGentleRise(), 20, 14, 25m);

        verdict.Should().Be(MtfTrendState.Range);
    }

    /// <summary>A series below the minimum throws: the warming state's territory, never a fabricated verdict.</summary>
    [Fact]
    public void AShortSeries_Throws()
    {
        var act = () => TrendClassifier.Classify(Monotone(DefaultMinimum - 1, 100m, 1m), 20, 14, 25m);

        act.Should().Throw<ArgumentException>();
    }

    /// <summary>A null series throws, a period below its floor throws — the honest errors, never silent substitutes.</summary>
    [Fact]
    public void BadInputs_Throw()
    {
        var act = () => TrendClassifier.Classify(null!, 20, 14, 25m);
        act.Should().Throw<ArgumentNullException>();

        var shortPeriod = () => TrendClassifier.Classify(Monotone(DefaultMinimum, 100m, 1m), 1, 14, 25m);
        shortPeriod.Should().Throw<ArgumentOutOfRangeException>();

        var shortAdx = () => TrendClassifier.Classify(Monotone(DefaultMinimum, 100m, 1m), 20, 1, 25m);
        shortAdx.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>The minimum covers its own legs: two ADX periods, the longer of the EMA and swing windows, the slope window.</summary>
    [Fact]
    public void TheMinimumBars_CoversItsLegs()
    {
        TrendClassifier.MinimumBars(20, 14).Should().Be(73);
        TrendClassifier.MinimumBars(2, 2).Should().Be(47);
        TrendClassifier.MinimumBars(200, 100).Should().Be(450);
        TrendClassifier.MinimumBars(20, 14).Should().BeGreaterThanOrEqualTo(
            TrendClassifier.MinimumBars(2, 2),
            "a longer tuning never asks for fewer bars");
    }

    /// <summary>The classifier is deterministic: the same series answers the same verdict twice.</summary>
    [Fact]
    public void TheClassifier_IsDeterministic()
    {
        var series = SpikeThenGentleRise();

        TrendClassifier.Classify(series, 20, 14, 25m).Should().Be(
            TrendClassifier.Classify(series, 20, 14, 25m));
    }
}
