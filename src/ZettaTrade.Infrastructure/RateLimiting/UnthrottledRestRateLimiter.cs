namespace ZettaTrade.Infrastructure.RateLimiting;

/// <summary>
/// The pass-through limiter for every exempt REST surface (T-106): the demo
/// and paper adapters are synthetic (no venue, no IP budget, nothing a 429
/// would ever police), and direct constructions of the real adapters without
/// a wired limiter (tests, plugin hosts) keep running unshaped instead of
/// failing — the throttling is a production posture, applied by the DI
/// seam, not a runtime requirement of the adapter itself.
/// </summary>
internal sealed class UnthrottledRestRateLimiter : IRestRateLimiter
{
    /// <summary>The shared instance the exempt seams use (stateless).</summary>
    public static readonly IRestRateLimiter Instance = new UnthrottledRestRateLimiter();

    private UnthrottledRestRateLimiter()
    {
    }

    /// <summary>Admit immediately: the surface holds no real venue budget.</summary>
    public Task AcquireAsync(
        RestLane lane,
        string endpointKey,
        int weight,
        CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Nothing to park: there is no venue bookkeeping to honor.</summary>
    public void ReportBlocked(string endpointKey, TimeSpan? retryAfter)
    {
    }
}
