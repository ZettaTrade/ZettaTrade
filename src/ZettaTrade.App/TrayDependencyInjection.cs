namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using ZettaTrade.App.Services;

/// <summary>
/// Composition of the tray presence (T-214): the close policy the main
/// window's gesture consults and the tray icon service that carries the
/// menu. Called from the composition root — one line — after the update
/// services: the settings service and the notification center it reads
/// are already registered by then (the startup walk initializes every
/// <c>IAsyncInitializable</c> in registration order, so the policy's seed
/// read settles before the first close gesture can consult it).
/// </summary>
public static class TrayDependencyInjection
{
    /// <summary>Register the tray close policy and the tray icon service.</summary>
    /// <param name="services">The application's service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddTrayServices(this IServiceCollection services)
    {
        services.AddSingleton<TrayClosePolicy>();
        services.AddSingleton<TrayIconService>();

        return services;
    }
}
