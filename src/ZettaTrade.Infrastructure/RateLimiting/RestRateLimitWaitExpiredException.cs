namespace ZettaTrade.Infrastructure.RateLimiting;

/// <summary>
/// The bounded-wait contract of the REST limiter (T-106): an acquire that
/// would have to wait past the max park span is an error, never an infinite
/// stall. The limiter throws it when a venue park (or a token-deficit wait)
/// exceeds the configured bound — the caller surfaces the failure like any
/// other REST refusal instead of hanging.
/// </summary>
public sealed class RestRateLimitWaitExpiredException : Exception
{
    /// <summary>
    /// Create the expiry error for one endpoint: the reported wait would
    /// have exceeded the limiter's max park span.
    /// </summary>
    /// <param name="endpointKey">The endpoint the acquire waited on.</param>
    /// <param name="wait">The wait the limiter refused to park for.</param>
    public RestRateLimitWaitExpiredException(
        string endpointKey,
        TimeSpan wait)
        : base($"The REST rate limiter's bounded wait expired for {EndpointKeyEscape(endpointKey)}: " +
               $"refused a {wait.TotalSeconds:F1} s wait past the max park span")
    {
        EndpointKey = endpointKey;
        RefusedWait = wait;
    }

    /// <summary>The endpoint the acquire waited on.</summary>
    public string EndpointKey { get; }

    /// <summary>The wait the limiter refused to park for.</summary>
    public TimeSpan RefusedWait { get; }

    /// <summary>Quote the endpoint key in the message readably (may carry slashes).</summary>
    private static string EndpointKeyEscape(string endpointKey) => endpointKey.Trim();
}
