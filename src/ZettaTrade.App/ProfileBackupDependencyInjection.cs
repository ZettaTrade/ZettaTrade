namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using ZettaTrade.Infrastructure.Backup;

/// <summary>
/// The profile backup's composition (T-215): the app-lifetime singleton over
/// the production profile paths (the tests build their own over a sandbox).
/// Registered beside the settings port — the settings window's Integrations
/// tab takes both in.
/// </summary>
public static class ProfileBackupDependencyInjection
{
    /// <summary>
    /// Register the profile backup service. The paths parameter defaults to
    /// the production resolver inside the service (the composition passes
    /// nothing; the tests construct the service directly).
    /// </summary>
    public static IServiceCollection AddProfileBackupServices(this IServiceCollection services) => services
        .AddSingleton<ProfileBackupService>();
}
