namespace ZettaTrade.Infrastructure.Indicators;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.Charting;
using ZettaTrade.Infrastructure.Configuration;

/// <summary>
/// DI wiring for the indicator plugin seam: the catalog scans the resolved
/// plugins directory once at registration (the composition root resolves it
/// through the <see cref="IIndicatorCatalog"/> interface afterwards).
/// </summary>
public static class IndicatorsDependencyInjection
{
    /// <summary>Register the indicator catalog: the scan runs at the plugins directory the configuration resolves.</summary>
    public static IServiceCollection AddIndicators(this IServiceCollection services) =>
        services.AddSingleton<IIndicatorCatalog>(sp => new PluginIndicatorCatalog(
            ConfigPathResolver.PluginsDirectory,
            sp.GetRequiredService<ILogger<PluginIndicatorCatalog>>()));
}
