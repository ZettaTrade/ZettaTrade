namespace ZettaTrade.Strategies.Tests.Kit;

using ZettaTrade.Strategies;

/// <summary>
/// The demo strategy clears the standing battery (T-111): the kit shapes
/// the seam's own shipped implementation.
/// </summary>
public sealed class DemoGoldenCrossBattery : StrategyConformanceBattery
{
    public DemoGoldenCrossBattery()
        : base(() => new DemoGoldenCrossStrategy())
    {
    }
}
