namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using ZettaTrade.App.Services;

/// <summary>
/// DI wiring for the open-interest history's front (T-241): the chart's OI
/// pane resolves the venue's settled snapshots through this session
/// singleton — the active-adapter probe, the bounded stale-aware
/// per-(symbol, interval) cache. No view model of its own: the chart
/// ViewModel consumes the service directly.
/// </summary>
public static class OIHistoryDependencyInjection
{
    /// <summary>Register the open-interest history's front.</summary>
    public static IServiceCollection AddOIHistoryServices(this IServiceCollection services) => services
        .AddSingleton<OIHistoryService>();
}
