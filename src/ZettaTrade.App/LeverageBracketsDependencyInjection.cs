namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using ZettaTrade.App.Services;

/// <summary>
/// DI wiring for the leverage brackets' front (T-243): the estimated
/// liquidation levels resolve the venue's public risk table through this
/// session singleton — the active-adapter capability probe, the bounded
/// stale-aware per-symbol cache. No view model of its own: the chart
/// ViewModel consumes the service directly.
/// </summary>
public static class LeverageBracketsDependencyInjection
{
    /// <summary>Register the leverage brackets' front.</summary>
    public static IServiceCollection AddLeverageBracketsServices(this IServiceCollection services) => services
        .AddSingleton<LeverageBracketsService>();
}
