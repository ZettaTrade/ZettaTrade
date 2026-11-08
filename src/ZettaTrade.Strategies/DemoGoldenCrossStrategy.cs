namespace ZettaTrade.Strategies;

using ZettaTrade.Core.Entities;

/// <summary>
/// The demo strategy of the seam (T-111): a classic two-SMA cross. The
/// stance re-derives from the retained window on every evaluation - a
/// pure walk, no state between calls - so the same candle sequence always
/// yields the same stance sequence: a golden cross of the fast average
/// over the slow one stands long, a death cross stands flat, ties hold
/// the stance, and an unfinished warmup is an honest
/// <see cref="StrategyStance.Neutral"/>. It evaluates on candle close and
/// emits nothing else: the signals channel is inert output, the human
/// decides.
/// </summary>
public sealed class DemoGoldenCrossStrategy : IStrategy
{
    #region Constants

    /// <summary>The canonical id the catalog registers the demo under.</summary>
    public const string StrategyId = "demo-golden-cross";

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
