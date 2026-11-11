namespace ZettaTrade.Strategies.Tests.Harness;

using ZettaTrade.Strategies;

/// <summary>
/// The harness's hand-countable cross walk (T-168): the two-SMA transition
/// the seam facts walk over <see cref="TestBars.GoldenCandles"/> - a
/// golden cross of the fast average over the slow one stands long, a
/// death cross stands flat, ties hold the stance, an unfinished warmup is
/// an honest <see cref="StrategyStance.Neutral"/>. It stands in for the
/// shipped strategy after the demo moved to the standard pack (T-168):
/// the seam's host and feed facts need a deterministic walk over a seed
/// small enough to count by hand, not a plugin load - the shipped
/// strategy's own math facts live in the satellite's suite.
/// </summary>
public sealed class GoldenWalkStrategy : IStrategy
{
    #region Constants

    /// <summary>The canonical id the facts register the harness walk under.</summary>
    public const string StrategyId = "test-golden-walk";

    /// <summary>The fast average's period.</summary>
    public const int FastPeriod = 3;

    /// <summary>The slow average's period (kept longer than <see cref="FastPeriod"/>).</summary>
    public const int SlowPeriod = 6;

    #endregion

    /// <inheritdoc />
    public string Id => StrategyId;

    /// <inheritdoc />
    public StrategyStance Evaluate(StrategyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var bars = context.ClosedBars ?? throw new ArgumentException("The context carries no closed bars", nameof(context));

        var count = bars.Count;
        if (count < SlowPeriod)
        {
            return StrategyStance.Neutral;
        }

        var stance = StrategyStance.Neutral;
        var fastSum = 0m;
        var slowSum = 0m;

        // The first slow-full bar: both averages exist, the stance walk
        // starts here.
        for (var i = 0; i < SlowPeriod; i++)
        {
            slowSum += bars[i].Close;
        }

        for (var i = SlowPeriod - FastPeriod; i < SlowPeriod; i++)
        {
            fastSum += bars[i].Close;
        }

        for (var i = SlowPeriod - 1; i < count; i++)
        {
            var fast = fastSum / FastPeriod;
            var slow = slowSum / SlowPeriod;
            stance = Transition(stance, fast, slow);

            if (i + 1 < count)
            {
                fastSum += bars[i + 1].Close - bars[i + 1 - FastPeriod].Close;
                slowSum += bars[i + 1].Close - bars[i + 1 - SlowPeriod].Close;
            }
        }

        return stance;
    }

    private static StrategyStance Transition(StrategyStance current, decimal fast, decimal slow)
    {
        if (fast > slow)
        {
            return StrategyStance.Long;
        }

        if (fast < slow)
        {
            return StrategyStance.Flat;
        }

        // A tie is not a cross: the stance holds.
        return current;
    }
}
