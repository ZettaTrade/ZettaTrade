namespace ZettaTrade.Strategies.Tests.Kit;

using ZettaTrade.Plugins.SampleStrategy;

/// <summary>
/// The out-of-tree sample plugin clears the standing battery too (T-111):
/// the kit applies to plugin classes, not only to the shipped demo - the
/// fixture's type is loaded here through its own test-referenced instance,
/// the same contract an external author builds against.
/// </summary>
public sealed class SampleCrossStrategyBattery : StrategyConformanceBattery
{
    public SampleCrossStrategyBattery()
        : base(() => new SampleCrossStrategy())
    {
    }
}
