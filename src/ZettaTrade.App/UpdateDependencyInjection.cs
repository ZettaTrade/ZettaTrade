namespace ZettaTrade.App;

using System;
using Microsoft.Extensions.DependencyInjection;
using ZettaTrade.App.Services;

/// <summary>
/// Composition of the update checker (T-213): the named HttpClient the
/// GitHub Releases check rides and the service itself. Called from the
/// composition root — one line — AFTER the notification services: the
/// checker publishes through the center, so the center must resolve
/// first (the startup walk initializes the settings service before
/// every later registration's consumer anyway).
/// </summary>
public static class UpdateDependencyInjection
{
    /// <summary>The request timeout the named client carries: an unreachable endpoint must never hang a check.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Register the update checker and its HTTP client.</summary>
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
        services.AddSingleton<UpdateCheckService>();

        return services;
    }
}
