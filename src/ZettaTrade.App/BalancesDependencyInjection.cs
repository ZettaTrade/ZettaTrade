namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// DI wiring for the balances panel flows. The composition root calls the
/// single aggregate extension, so module-level registration details stay
/// local.
/// </summary>
public static class BalancesDependencyInjection
{
    /// <summary>
    /// Register the balances aggregation service (the registered adapters
    /// ride the active-exchange facade, the trading face is the single
    /// <see cref="ITradingAdapter"/>) and the transient panel view model:
    /// every panel open re-queries the adapters, so a stale snapshot never
    /// leaks into a new open.
    /// </summary>
    public static IServiceCollection AddBalancesServices(this IServiceCollection services) => services
        .AddSingleton<BalancesAggregationService>(sp => new BalancesAggregationService(
            sp.GetRequiredService<ActiveExchangeService>().Registered,
            sp.GetRequiredService<ITradingAdapter>(),
            sp.GetRequiredService<ILogger<BalancesAggregationService>>()))
        .AddTransient<BalancesPanelViewModel>();
}
