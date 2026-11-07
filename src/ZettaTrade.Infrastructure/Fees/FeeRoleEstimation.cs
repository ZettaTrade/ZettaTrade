namespace ZettaTrade.Infrastructure.Fees;

using ZettaTrade.Core.Enums;

/// <summary>
/// The pre-trade maker/taker estimation rule (T-080): which schedule branch a
/// ticket-shaped order is expected to ride, classified by order type — the
/// limit orders rest and provide liquidity (maker), every other type crosses
/// or triggers (taker). This is the ESTIMATE's classification: an estimate
/// states intent before the market has said anything; the fill itself is
/// classified by its executed behavior when it is stamped (a crossing limit
/// that consumed liquidity executes as a taker fill), which is exactly why
/// the estimate line ships as an estimate.
/// </summary>
public static class FeeRoleEstimation
{
    /// <summary>The schedule branch a pre-trade order of the given type is expected to ride.</summary>
    /// <param name="type">The ticket's order type.</param>
    /// <returns><see cref="FeeRole.Maker"/> for limit orders, <see cref="FeeRole.Taker"/> for every other type.</returns>
    public static FeeRole ForOrderType(OrderType type) => type switch
    {
        OrderType.Limit => FeeRole.Maker,
        OrderType.Market => FeeRole.Taker,
        OrderType.StopMarket => FeeRole.Taker,
        OrderType.StopLimit => FeeRole.Taker,
        OrderType.TrailingStop => FeeRole.Taker,
        _ => FeeRole.Taker,
    };
}
