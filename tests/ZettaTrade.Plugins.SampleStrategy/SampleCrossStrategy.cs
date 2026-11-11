namespace ZettaTrade.Plugins.SampleStrategy;

using ZettaTrade.Strategies;

/// <summary>
/// T-111 loader fixture: one valid out-of-tree strategy. The stance is a
/// trivial deterministic read - a window ending on its own high stands
/// long, on its own low stands flat, anything undecided (a tie inside an
/// equal window or a mid-range close) stands neutral - so the catalog
/// tests can evaluate the registered instance by hand.
/// </summary>
public sealed class SampleCrossStrategy : IStrategy
{
    /// <summary>The canonical id the catalog registers the sample plugin under.</summary>
    public const string StrategyId = "sample-cross";

    /// <inheritdoc />
    public string Id => StrategyId;

    /// <inheritdoc />
    public StrategyStance Evaluate(StrategyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var bars = context.ClosedBars ?? throw new ArgumentException("The context carries no closed bars", nameof(context));

        var count = bars.Count;
        if (count == 0)
        {
            return StrategyStance.Neutral;
        }

        var last = count - 1;
        var close = bars[last].Close;
        var high = close;
        var low = close;
        for (var i = last; i >= 0; i--)
        {
            if (bars[i].Close > high)
            {
                high = bars[i].Close;
            }

            if (bars[i].Close < low)
            {
                low = bars[i].Close;
            }
        }

        if (high > low)
        {
            if (close == high)
            {
                return StrategyStance.Long;
            }

            if (close == low)
            {
                return StrategyStance.Flat;
            }
        }

        return StrategyStance.Neutral;
    }
}
