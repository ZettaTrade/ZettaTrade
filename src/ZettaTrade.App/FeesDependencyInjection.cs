namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using ZettaTrade.Infrastructure.Fees;

/// <summary>
/// DI wiring for the fees module (T-080): the per-adapter schedule registry
/// the paper adapter stamps fill commissions with and the order ticket's
/// estimate line and the history panels' fee columns read.
/// </summary>
public static class FeesDependencyInjection
{
    /// <summary>
    /// Register the fee schedule registry (IAsyncInitializable: the startup
    /// walk loads the configured schedules before any consumer resolves —
    /// the paper adapter's first fill and the first ticket open both follow
    /// the walk).
    /// </summary>
    public static IServiceCollection AddFeeServices(this IServiceCollection services) =>
        services
            .AddSingleton<FeeScheduleService>()
            .AddSingleton<IFeeScheduleProvider>(sp => sp.GetRequiredService<FeeScheduleService>());
}
