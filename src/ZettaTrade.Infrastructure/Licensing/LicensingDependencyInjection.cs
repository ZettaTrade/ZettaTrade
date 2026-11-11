namespace ZettaTrade.Infrastructure.Licensing;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Configuration;

/// <summary>
/// DI wiring for the licensing module (T-132): the validator, the
/// settings-backed store, the activation service, the loader gate and the
/// EULA surface. The plugin SDK consumes the gate when present (its
/// absence — an app shape without licensing registered — leaves every
/// plugin unprotected, which matches the default empty protected list).
/// </summary>
public static class LicensingDependencyInjection
{
    /// <summary>Registers the licensing module's services (all singletons — the state rides settings, none rides the services).</summary>
    /// <param name="services">The host service collection.</param>
    public static IServiceCollection AddLicensingServices(this IServiceCollection services) => services
        .AddSingleton<ILicenseValidator, LicenseValidator>()
        .AddSingleton<ILicenseStore, SettingsLicenseStore>()
        .AddSingleton<ILicenseEulaService>(serviceProvider =>
        {
            var settings = serviceProvider.GetRequiredService<ISettingsService>();
            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            return new LicenseEulaService(
                settings,
                ConfigPathResolver.PluginsDirectory,
                loggerFactory.CreateLogger<LicenseEulaService>());
        })
        .AddSingleton<ILicenseActivationService, LicenseActivationService>()
        .AddSingleton<ILicenseGate, LicenseGate>();
}
