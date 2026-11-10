using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ZettaTrade.Infrastructure.Plugins;

/// <summary>
/// The DI entry the app uses to summon the SDK loader (T-131): the one
/// blessed path an out-of-tree plugin assembly travels — the plugins
/// directory scan at the startup walk, the registry the shell reads
/// after it.
/// </summary>
public static class PluginSdkDependencyInjection
{
    /// <summary>
    /// Registers the SDK loader and the extension registry. The loader
    /// joins the startup walk automatically through
    /// <see cref="Livery.Contracts.IAsyncInitializable"/>.
    /// </summary>
    /// <param name="services">The host service collection.</param>
    /// <param name="scanDirectory">The plugins directory the loader walks.</param>
    public static IServiceCollection AddPluginSdk(
        this IServiceCollection services,
        string scanDirectory)
    {
        services.AddSingleton(new PluginExtensionRegistry());
        services.AddSingleton(serviceProvider => new PluginSdkHost(
            scanDirectory,
            serviceProvider.GetRequiredService<PluginExtensionRegistry>(),
            serviceProvider,
            serviceProvider.GetRequiredService<ILogger<PluginSdkHost>>()));
        services.AddSingleton<IPluginExtensionRegistry>(
            serviceProvider => serviceProvider.GetRequiredService<PluginExtensionRegistry>());
        return services;
    }
}
