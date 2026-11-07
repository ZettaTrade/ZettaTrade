namespace ZettaTrade.Infrastructure.RateLimiting;

/// <summary>
/// The immutable budget manifest of one REST endpoint family: the token
/// budget the family's bucket refills over its refill interval (weights are
/// per-request costs supplied by the call sites, so one bucket serves a
/// graded-cost family such as the venue's tiered kline schedule). The
/// per-adapter manifests live as static defaults on the adapters and are
/// scaled at runtime by the reactive <c>Limiter.*</c> settings keys — the
/// manifest is the source of truth, never invented per call.
/// </summary>
/// <param name="EndpointKey">The adapter's path-template constant (e.g. <c>/api/v3/klines</c>). Unique within an adapter's manifest.</param>
/// <param name="BudgetTokens">The bucket's full weight budget (tokens available right after a full refill or park reset).</param>
/// <param name="RefillInterval">The span over which the budget refills at its documented flow rate (tokens accrue continuously, not in one burst).</param>
public sealed record RestEndpointBudget(
    string EndpointKey,
    int BudgetTokens,
    TimeSpan RefillInterval);
