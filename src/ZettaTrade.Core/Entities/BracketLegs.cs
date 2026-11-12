namespace ZettaTrade.Core.Entities;

/// <summary>
/// The protective exits of a bracket order (T-186): the take-profit leg and
/// the stop-loss leg ride ONE entry request instead of being placed
/// separately. Both exits form an either-or pair — they share one OCO group
/// id, so the first exit that fills or dies takes the other down — and they
/// arm only when the entry fills: the adapter holds them until then. The
/// exits close the entry's position (the opposite side, the same quantity),
/// and they validate as part of their entry request: an unparsable bracket
/// is refused whole, never a half protection.
/// </summary>
public sealed record BracketLegs
{
    /// <summary>The take-profit exit: a limit order resting beyond the market.</summary>
    public required OrderRequest TakeProfit { get; init; }

    /// <summary>The stop-loss exit: a stop-limit order arming beyond the market.</summary>
    public required OrderRequest StopLoss { get; init; }
}
