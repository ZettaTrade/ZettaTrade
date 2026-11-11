namespace ZettaTrade.Plugins.SampleStrategy;

using ZettaTrade.Strategies;

/// <summary>
/// T-111 loader fixture: a type that builds cleanly but exposes an empty
/// id. It lives in the SAME assembly as the valid sample strategy so the
/// loader test can pin the per-type skip: the broken type alone is
/// dropped with a loud warning, the healthy neighbor of the same file
/// still registers.
/// </summary>
public sealed class NoIdStrategy : IStrategy
{
    /// <summary>An id no catalog may register: the standing no-blank rule.</summary>
    public string Id => "   ";

    /// <inheritdoc />
    public StrategyStance Evaluate(StrategyContext context) => StrategyStance.Neutral;
}
