namespace ZettaTrade.Infrastructure.RateLimiting;

using System.Net.Http.Headers;

/// <summary>
/// The HTTP-plane seam between the adapters and the limiter (T-106): the
/// venue's <c>Retry-After</c> header parsed to a relative span so the
/// limiter itself stays HTTP-free. A <c>Date</c> form is converted against
/// the response's own date (or local now) and clamped to non-negative; an
/// absent header yields null (the limiter then applies its default block
/// span — the block is real, only the guidance is missing).
/// </summary>
internal static class RestRetryAfterParser
{
    /// <summary>
    /// Parse one 429 response's <c>Retry-After</c> to a relative span.
    /// </summary>
    /// <param name="headers">The blocked response's headers.</param>
    /// <returns>The relative retry span, or null when the header is absent or unparsable.</returns>
    public static TimeSpan? Parse(HttpResponseHeaders? headers)
    {
        var retryAfter = headers?.RetryAfter;
        if (retryAfter is null)
        {
            return null;
        }

        if (retryAfter.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (retryAfter.Date is { } date)
        {
            var span = date - DateTimeOffset.UtcNow;
            return span < TimeSpan.Zero ? TimeSpan.Zero : span;
        }

        return null;
    }
}
