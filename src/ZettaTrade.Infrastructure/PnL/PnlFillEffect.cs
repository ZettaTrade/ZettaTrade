namespace ZettaTrade.Infrastructure.PnL;

/// <summary>
/// What one accounted fill did to its position book: opened a new
/// position, reduced (partially or fully closed) an existing one, or
/// flipped the side (a closing trade larger than the open position closes
/// it and re-opens the remainder in the opposite direction at the same
/// fill price).
/// </summary>
public enum PnlFillEffect
{
    /// <summary>The fill grew a position (a fresh one, or an extension in the same direction).</summary>
    Open,

    /// <summary>The fill closed part or all of a position without exceeding it.</summary>
    Reduce,

    /// <summary>The fill closed the position and exceeded it, re-opening the remainder on the other side.</summary>
    Flip,
}
