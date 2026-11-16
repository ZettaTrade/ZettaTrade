namespace ZettaTrade.Infrastructure.Exchange;

using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The leverage-bracket capability of a market-data adapter (T-243). An
/// optional role interface in the Infrastructure Exchange module — the
/// consumers probe <c>Current is ILeverageBracketsProvider</c> the same way
/// they probe the other optional capabilities, so an adapter without the
/// endpoint (or a spot-only symbol on one with it) degrades to an honest
/// absence, never a guess. Only perpetual contracts carry a risk table: a
/// spot symbol is a request-contract violation, refused with an exception.
/// </summary>
public interface ILeverageBracketsProvider
{
    /// <summary>
    /// Fetch the venue's public leverage-bracket table of a perpetual
    /// symbol, ascending by notional floor (the tiers a position's notional
    /// picks exactly one of).
    /// </summary>
    /// <param name="symbol">The perpetual symbol to fetch (a spot symbol is refused).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The brackets ascending by notional floor.</returns>
    Task<IReadOnlyList<LeverageBracket>> GetLeverageBracketsAsync(Symbol symbol, CancellationToken ct = default);
}
