namespace ZettaTrade.Strategies.Tests.Demo;

using FluentAssertions;
using Xunit;
using ZettaTrade.Core.Enums;
using ZettaTrade.Strategies;
using ZettaTrade.Strategies.Tests.Harness;

/// <summary>
/// The demo golden on seeded candles (T-111): the two-SMA cross walked
/// over the hand-countable seed (ramp 1..12, fall 12..2, recovery 10/12/14).
/// Per closed candle the window grows and the stance over the window-so-far
/// is asserted - exactly the per-candle evaluation the host performs - so
/// the golden pins both the stance walk and the warmup honesty to the same
/// numbers: Neutral(5) > Long(9) > Flat(5) > Long(2).
/// </summary>
public sealed class DemoGoldenCrossTests
{
    [Fact]
    public void TheGolden_WalksNeutralLongFlatOnTheSeededCandles()
    {
        var strategy = new DemoGoldenCrossStrategy();
        var candles = TestBars.GoldenCandles();
        var expected = TestBars.ExpectedGoldenStances;

        candles.Should().HaveCount(expected.Length, "the seed and the golden are one series, candle for candle");

        for (var k = 0; k < candles.Length; k++)
        {
            var window = candles.Take(k + 1).ToArray();
            var context = new StrategyContext(TestBars.Symbol, TimeFrame.M1, window);

            var stance = strategy.Evaluate(context);

            stance.Should().Be(
                expected[k],
                "candle {0} (close {1}) holds a hand-counted stance over the window-so-far",
                k,
                window[^1].Close);
        }
    }

    /// <summary>The same series evaluated in one call (the whole window at once) ends flat after the fall.</summary>
    [Fact]
    public void TheWholeWindow_EvaluatesEveryCandleAndEndsLongAfterTheRecovery()
    {
        var strategy = new DemoGoldenCrossStrategy();
        var context = new StrategyContext(TestBars.Symbol, TimeFrame.M1, TestBars.GoldenCandles());

        strategy.Evaluate(context).Should().Be(StrategyStance.Long, "the recovery's golden cross stands long at the last candle");
    }

    [Fact]
    public void TheWarmup_IsNeutralWhileTheSlowAverageIsUnfilled()
    {
        var strategy = new DemoGoldenCrossStrategy();
        var context = new StrategyContext(
            TestBars.Symbol,
            TimeFrame.M1,
            TestBars.Candles(1, 2, 3, 4, 5));

        strategy.Evaluate(context).Should().Be(StrategyStance.Neutral, "the warmup is an honest neutral, never a fabricated stance");
    }

    /// <summary>
    /// A tie at the evaluated candle is not a cross: the stance holds.
    /// The seed [1,2,3,4,5,6,3,3] walks long through the ramp and the final
    /// bar's averages tie exactly (fast 4, slow 4) - the long survives.
    /// </summary>
    [Fact]
    public void TheTie_HoldsTheStandingStance()
    {
        var strategy = new DemoGoldenCrossStrategy();
        var context = new StrategyContext(TestBars.Symbol, TimeFrame.M1, TestBars.Candles(1, 2, 3, 4, 5, 6, 3, 3));

        strategy.Evaluate(context).Should().Be(StrategyStance.Long, "the tie holds the long the ramp established; only a cross releases it");
    }

    /// <summary>Ties with no opinion established stay neutral - the tie never fabricates one.</summary>
    [Fact]
    public void TheTie_StaysNeutralWhenNoStanceExists()
    {
        var strategy = new DemoGoldenCrossStrategy();
        var context = new StrategyContext(TestBars.Symbol, TimeFrame.M1, TestBars.Candles(6, 6, 6, 6, 6, 6));

        strategy.Evaluate(context).Should().Be(StrategyStance.Neutral, "an undecided window keeps its no-opinion");
    }

    /// <summary>The no-substitution rule on the context's bars: a null window is rejected, not defaulted.</summary>
    [Fact]
    public void Evaluate_ANullWindow_IsRejectedInsteadOfDefaulting()
    {
        var strategy = new DemoGoldenCrossStrategy();
        var context = new StrategyContext(TestBars.Symbol, TimeFrame.M1, null!);

        FluentActions.Invoking(() => strategy.Evaluate(context))
            .Should().ThrowExactly<ArgumentException>();
    }
}
