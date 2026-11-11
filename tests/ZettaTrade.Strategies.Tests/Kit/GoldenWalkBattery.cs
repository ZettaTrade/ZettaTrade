namespace ZettaTrade.Strategies.Tests.Kit;

using ZettaTrade.Strategies;
using ZettaTrade.Strategies.Tests.Harness;

/// <summary>
/// The harness walk's battery (T-168): the loader fixture's strategy
/// clears the standing recipe before it serves any seam fact suite.
/// </summary>
public sealed class GoldenWalkBattery : StrategyConformanceBattery
{
    /// <summary>Run the battery against the harness's cross walk.</summary>
    public GoldenWalkBattery()
        : base(() => new GoldenWalkStrategy())
    {
    }
}
