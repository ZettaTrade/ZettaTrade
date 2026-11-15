namespace ZettaTrade.Infrastructure.Exchange;

using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The open-interest-history capability of a market-data adapter (T-241).
/// An optional role interface in the Infrastructure Exchange module — the
/// consumers probe <c>Current is IOIHistoryProvider</c> the same way they
/// probe the other optional capabilities, so an adapter without the
/// endpoint (or a spot-only symbol on one with it) degrades to an honest
/// absence, never a guess. Only perpetual contracts serve history: a spot
/// symbol is a request-contract violation, refused with an exception.
/// </summary>
public interface IOIHistoryProvider
{
    /// <summary>
    /// Fetch the venue's open-interest snapshots of a perpetual symbol over
    /// the window, ascending by time, bucketed at the bar interval (the
    /// venue's own served buckets floor the request: anything finer than
    /// the smallest bucket rides that smallest bucket).
    /// </summary>
    /// <param name="symbol">The perpetual symbol to fetch (a spot symbol is refused).</param>
    /// <param name="barInterval">The chart's bar interval (the buckets floor onto the venue's served set).</param>
    /// <param name="from">The inclusive window start, UTC.</param>
    /// <param name="to">The exclusive window end, UTC; nothing means "now".</param>
    /// <param name="limit">The snapshot cap per call (the venue's own page cap bounds it).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The snapshots ascending by time.</returns>
    Task<IReadOnlyList<OpenInterestPoint>> GetOpenInterestHistoryAsync(
        Symbol symbol,
        TimeFrame barInterval,
        DateTimeOffset from,
        DateTimeOffset? to = null,
        int limit = 200,
        CancellationToken ct = default);
}
