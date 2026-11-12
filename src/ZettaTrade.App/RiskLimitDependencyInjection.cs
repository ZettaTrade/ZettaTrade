namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using ZettaTrade.App.Services;

/// <summary>
/// DI wiring for the daily loss governor (T-189): the state service both
/// the risk manager (the order gate) and the order ticket (the banner and
/// the reset) resolve. Registered BEFORE the risk manager so the manager's
/// optional dependency resolves from this registration; the PnL engine's
/// own registration order stays the walk's business (singletons resolve at
/// first use, and the governor is not an initialization candidate).
/// </summary>
public static class RiskLimitDependencyInjection
{
    /// <summary>
    /// Register the daily loss limit state service: the typed registration
    /// stays the walk's inspectable shape, the interface alias serves the
    /// manager and the ticket.
    /// </summary>
    public static IServiceCollection AddRiskLimitServices(this IServiceCollection services) => services
        .AddSingleton<DailyLossLimitService>()
        .AddSingleton<IDailyLossLimit>(sp => sp.GetRequiredService<DailyLossLimitService>());
}
