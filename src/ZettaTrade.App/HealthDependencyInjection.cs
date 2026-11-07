namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using ZettaTrade.App.Services;

/// <summary>
/// DI wiring for the status bar's adapter health strip (T-098). Registered
/// AFTER the active-exchange facade (its construction populates the adapter
/// catalog and binds the adapters' reconnect policies to the supervisor)
/// and BEFORE the status bar view model that subscribes the fold's stream.
/// </summary>
public static class HealthDependencyInjection
{
    /// <summary>
    /// Register the adapter health fold (a singleton: the observable
    /// composition is shared, stateless per subscriber).
    /// </summary>
    public static IServiceCollection AddHealthUiServices(this IServiceCollection services) => services
        .AddSingleton<AdapterHealthService>();
}
