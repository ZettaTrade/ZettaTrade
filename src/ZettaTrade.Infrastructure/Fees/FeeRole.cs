namespace ZettaTrade.Infrastructure.Fees;

/// <summary>
/// Which side of the liquidity book a fill executed on: a maker fill provided
/// liquidity resting on the book (limit orders that sat there), a taker fill
/// consumed it (market orders, crossing/triggering orders). The distinction
/// selects the maker/taker branch of a fee schedule — the selection that has
/// to be visible everywhere the cost matters (T-080).
/// </summary>
public enum FeeRole
{
    /// <summary>The fill provides liquidity: it rests on the book (maker rate).</summary>
    Maker = 0,

    /// <summary>The fill consumes liquidity: it crosses or triggers against the book (taker rate).</summary>
    Taker = 1,
}
