namespace ZettaTrade.App;

using System;
using Microsoft.Extensions.DependencyInjection;
using ZettaTrade.App.Services;

/// <summary>
/// Composition of the update services (T-213 checker, T-258 auto-update):
/// the named HttpClient the GitHub Releases check rides, the named
/// HttpClient the release assets' downloads ride, and the two services
/// themselves. Called from the composition root — one line — AFTER the
/// notification services: the checker publishes through the center, so
/// the center must resolve first (the startup walk initializes the
/// settings service before every later registration's consumer anyway).
/// The checker registers before the staging service: the staging
/// subscribes to the checker's verdict stream at its own initialization.
/// </summary>
public static class UpdateDependencyInjection
{
    /// <summary>The request timeout the named client carries: an unreachable endpoint must never hang a check.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The request timeout the asset client carries: a tens-of-megabytes
    /// archive must never be bound by the check's ten-second budget (the
    /// staging service wraps every fetch in its own size cap and total
    /// budget instead — this timeout only bounds one stalled request).
    /// </summary>
    private static readonly TimeSpan AssetRequestTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Register the update services and their HTTP clients.</summary>
    /// <param name="services">The application's service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddUpdateServices(this IServiceCollection services)
    {
        services.AddHttpClient(UpdateCheckService.HttpClientName, client =>
        {
            client.Timeout = RequestTimeout;
            // The GitHub API refuses requests without a User-Agent; the
            // versioned product string identifies the terminal honestly.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ZettaTrade-UpdateCheck");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        });

        // The asset lane (T-258): release archives and sums files ride
        // their own client — the same UA discipline, a transfer-sized
        // timeout, and the staging service's own caps on top.
        services.AddHttpClient(UpdateStageService.HttpClientName, client =>
        {
            client.Timeout = AssetRequestTimeout;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ZettaTrade-UpdateCheck");
        });

        services.AddSingleton<UpdateCheckService>();
        services.AddSingleton<UpdateStageService>();

        return services;
    }
}
