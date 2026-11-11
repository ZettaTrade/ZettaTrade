namespace ZettaTrade.Strategies.Tests.Kit;

using ZettaTrade.Plugins.SampleStrategy;
using ZettaTrade.Strategies;
using ZettaTrade.Strategies.Tests.Harness;

/// <summary>
/// The loader fixture's battery (T-168): the sample strategy clears the
/// standing recipe before it serves the loader matrix - the demo battery's
/// seat after the demo moved to the standard pack (T-168).
/// </summary>
public sealed class SampleCrossBattery : StrategyConformanceBattery
{
    /// <summary>Run the battery against the sample strategy fixture.</summary>
    public SampleCrossBattery()
        : base(() => new SampleCrossStrategy())
    {
    }
}
