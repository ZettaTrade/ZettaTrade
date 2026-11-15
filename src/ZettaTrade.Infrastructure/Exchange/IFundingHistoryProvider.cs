namespace ZettaTrade.Infrastructure.Exchange;

using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The funding-rate-history capability of a market-data adapter (T-240).
/// An optional role interface in the Infrastructure Exchange module — the
/// consumers probe <c>Current is IFundingHistoryProvider</c> the same way
/// they probe the other optional capabilities, so an adapter without the
/// endpoint (or a spot-only symbol on one with it) degrades to an honest
/// absence, never a guess. Only perpetual contracts serve history: a spot
/// symbol is a request-contract violation, refused with an exception.
/// </summary>
public interface IFundingHistoryProvider
{
    /// <summary>
    /// Fetch the settled funding-rate events of a perpetual symbol over the
    /// window, ascending by time. Rates are wire fractions (0.0001 = 0.01%).
    /// </summary>
    /// <param name="symbol">The perpetual symbol to fetch (a spot symbol is refused).</param>
    /// <param name="from">The inclusive window start, UTC.</param>
    /// <param name="to">The exclusive window end, UTC; nothing means "now".</param>
    /// <param name="limit">The event cap per call (the venue's own page cap bounds it).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The events ascending by time.</returns>
    Task<IReadOnlyList<FundingRatePoint>> GetFundingRateHistoryAsync(
        Symbol symbol,
        DateTimeOffset from,
        DateTimeOffset? to = null,
        int limit = 200,
        CancellationToken ct = default);
}
