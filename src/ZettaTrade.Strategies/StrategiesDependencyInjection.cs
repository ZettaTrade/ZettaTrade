namespace ZettaTrade.Strategies;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// DI wiring for the strategy plugin seam: the catalog scans the given
/// strategies plugins directory once at resolution time (the composition
/// root hands the directory in - keeping the path resolution out of this
/// module keeps it a leaf, like Charting). Nothing here hosts or wires the
/// host: app-side composition is the hosting task's (T-112) business, and
/// no signal of this seam ever reaches an order path.
/// </summary>
public static class StrategiesDependencyInjection
{
    /// <summary>Register the strategy catalog: the scan runs at the plugins directory the composition resolves.</summary>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="strategiesDirectory">The directory scanned for strategy plugin assemblies.</param>
    public static IServiceCollection AddStrategies(this IServiceCollection services, string strategiesDirectory) =>
        services.AddSingleton<IStrategyCatalog>(sp => new PluginStrategyCatalog(
            strategiesDirectory,
            sp.GetRequiredService<ILogger<PluginStrategyCatalog>>()));
}
