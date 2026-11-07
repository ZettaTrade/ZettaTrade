namespace ZettaTrade.Infrastructure.RateLimiting;

/// <summary>
/// The REST rate limiter seam (T-106): a per-endpoint token bucket surface
/// the exchange adapters consult before every outbound REST call. The
/// limiter is per adapter (one instance per adapter, buckets keyed by the
/// adapter's path-template constants) and lives entirely adapter-side — the
/// UI never touches it, so throttling never turns into a busy-wait upstream.
/// It shapes REST traffic only; WebSocket traffic is out of scope.
/// </summary>
/// <remarks>
/// Waiters honor <c>ct</c> and cancel cleanly; every wait is bounded by the
/// limiter's max park span, never an infinite stall. Blocked venues (a 429
/// response) report back through <see cref="ReportBlocked"/> so every lane
/// parks for the venue's <c>Retry-After</c> and refills to full after it.
/// </remarks>
public interface IRestRateLimiter
{
    /// <summary>
    /// Acquire the weight budget for one outbound REST request. Returns when
    /// the request may go out: the bucket has the weight available and the
    /// endpoint is not parked. Throws
    /// <see cref="RestRateLimitWaitExpiredException"/> when the bounded wait
    /// would exceed the max park span and
    /// <see cref="InvalidOperationException"/> when the endpoint key has no
    /// configured budget (a call-site/manifest mismatch, never a silent
    /// default).
    /// </summary>
    /// <param name="lane">The traffic lane (user requests preempt background bursts by design).</param>
    /// <param name="endpointKey">The adapter's path-template constant (e.g. <c>/api/v3/klines</c>).</param>
    /// <param name="weight">The request's documented token cost (positive).</param>
    /// <param name="ct">Cancellation of the acquire wait itself; a cancelled acquire never spends tokens.</param>
    Task AcquireAsync(
        RestLane lane,
        string endpointKey,
        int weight,
        CancellationToken ct = default);

    /// <summary>
    /// Report a venue block (an HTTP 429 on the endpoint's response) so the
    /// limiter parks every lane for that endpoint for the venue's retry
    /// guidance and refills the bucket to full after the span. Passing
    /// <c>null</c> applies the limiter's default block span (the header was
    /// absent, not the signal).
    /// </summary>
    /// <param name="endpointKey">The adapter's path-template constant of the blocked request.</param>
    /// <param name="retryAfter">The venue's retry-after span, when the response carried one.</param>
    void ReportBlocked(string endpointKey, TimeSpan? retryAfter);
}
