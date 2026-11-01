namespace ZettaTrade.Infrastructure.Demo;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI wiring for the canned-data demo adapter. The adapter is registered by
/// concrete type: the active-exchange facade (App) collects every adapter
/// instance and exposes the single <c>IMarketDataAdapter</c> entry point,
/// so adapters must not claim the interface registration themselves.
/// </summary>
public static class DemoDependencyInjection
{
    /// <summary>Register the deterministic canned-data demo adapter.</summary>
    public static IServiceCollection AddDemoAdapter(this IServiceCollection services) =>
        services.AddSingleton<DemoMarketDataAdapter>();
}
