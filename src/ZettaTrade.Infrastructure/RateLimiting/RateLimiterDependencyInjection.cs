namespace ZettaTrade.Infrastructure.RateLimiting;

using Microsoft.Extensions.DependencyInjection;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// DI wiring of the REST rate limiters (T-106): one limiter instance per
/// adapter, keyed by the adapter's named HttpClient identity (the name the
/// adapter itself declares), each built from that adapter's shipped
/// manifest and reading the same <see cref="ISettingsService"/> the rest of
/// the app already holds. Called from the exchange-adapter registration
/// seam so the App's composition root carries zero rate-limiter knowledge.
/// </summary>
/// <remarks>
/// The settings service is resolved <em>optionally</em>: Infrastructure
/// providers built in tests (no App composition) resolve the adapters with
/// the shipped manifests standing alone. A missing endpoint entry in a
/// manifest is a construction error, but a missing settings service is not
/// — the limiter stays honest without reactive reshaping.
/// </remarks>
public static class RateLimiterDependencyInjection
{
    /// <summary>
    /// Register the per-adapter REST limiters up front (the adapter factory
    /// lambdas resolve theirs by key during the same registration chain).
    /// Keys are the adapters' named HttpClient identities:
    /// <see cref="BinanceAdapter.HttpClientName"/> and
    /// <see cref="BybitAdapter.HttpClientName"/>.
    /// </summary>
    /// <param name="services">The registration collection.</param>
    public static IServiceCollection AddRestRateLimiters(this IServiceCollection services) =>
        services
            .AddKeyedSingleton<IRestRateLimiter>(
                BinanceAdapter.HttpClientName,
                (sp, _) => new RestRateLimiter(
                    BinanceAdapter.DefaultRestLimits,
                    sp.GetService<ISettingsService>()))
            .AddKeyedSingleton<IRestRateLimiter>(
                BybitAdapter.HttpClientName,
                (sp, _) => new RestRateLimiter(
                    BybitAdapter.DefaultRestLimits,
                    sp.GetService<ISettingsService>()));
}
