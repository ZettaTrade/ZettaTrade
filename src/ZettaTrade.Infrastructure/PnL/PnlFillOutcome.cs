namespace ZettaTrade.Infrastructure.PnL;

/// <summary>
/// The accounting result of one ingested fill: what it did to the position
/// book and what it realized net of its commission (in the symbol's quote
/// currency). Cumulative numbers live on the state feed's snapshots, not
/// here — this describes the fill, not the account.
/// </summary>
/// <param name="Effect">How the fill touched the position book.</param>
/// <param name="ClosedQuantity">The quantity the fill closed (zero for pure opens).</param>
/// <param name="RealizedPnl">The fill's own realized contribution, gross
/// PnL of the closed part minus its commission (which equals exactly
/// −commission for a pure open).</param>
public sealed record PnlFillOutcome(
    PnlFillEffect Effect,
    decimal ClosedQuantity,
    decimal RealizedPnl);
