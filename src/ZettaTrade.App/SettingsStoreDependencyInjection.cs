namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Configuration;

/// <summary>
/// The settings store's composition (T-137): the concrete store is the ONE
/// singleton — registered by its concrete type AND forwarded as
/// <see cref="ISettingsService"/> from that same instance. Consumers taking
/// the interface (the app walk's language/theme legs, chat, workspace
/// layout, the settings ViewModel's own fields) and consumers addressing the
/// concrete store's batch/snapshot members (the T-100 settings port) then
/// share one store with one state, and the port's activation cannot fail
/// resolution again.
/// </summary>
public static class SettingsStoreDependencyInjection
{
    /// <summary>
    /// Register the settings store: the concrete singleton over the resolver's
    /// shipped settings path, and the <see cref="ISettingsService"/> service
    /// that forwards to that very instance.
    /// </summary>
    /// <returns>The same collection, over the store registrations appended.</returns>
    public static IServiceCollection AddSettingsStoreServices(this IServiceCollection services) => services
        .AddSingleton<SettingsService>(sp =>
            new SettingsService(
                ConfigPathResolver.MainSettingsFile,
                sp.GetRequiredService<ILogger<SettingsService>>()))
        .AddSingleton<ISettingsService>(sp => sp.GetRequiredService<SettingsService>());
}
