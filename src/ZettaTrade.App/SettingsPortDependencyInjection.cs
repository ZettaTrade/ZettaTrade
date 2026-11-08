namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.App.Services;

/// <summary>
/// The settings port's composition (T-100): the app-lifetime singleton over
/// the app's settings store. Registered BEFORE the settings ViewModel (the
/// pane takes it in), after the settings service itself.
/// </summary>
public static class SettingsPortDependencyInjection
{
    /// <summary>
    /// Register the settings port service. The store dependency resolves the
    /// concrete <see cref="Infrastructure.Configuration.SettingsService"/> —
    /// the batch/snapshot primitives are its members.
    /// </summary>
    public static IServiceCollection AddSettingsPortServices(this IServiceCollection services) => services
        .AddSingleton<SettingsPortService>();
}
