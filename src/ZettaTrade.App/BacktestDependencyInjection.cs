namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Backtest;
using ZettaTrade.Infrastructure.Fees;
using ZettaTrade.Infrastructure.Persistence;
using ZettaTrade.Strategies;
/// <summary>
/// DI wiring for the offline deterministic backtest flow (T-113). The
/// harness is a session singleton over the strategy seam, the candle cache
/// and the fee registry it already rents; the run view model is transient —
/// every open arms a fresh form (the attach pattern owns its subscriptions).
/// </summary>
public static class BacktestDependencyInjection
{
    /// <summary>Register the offline backtest harness and the run view model.</summary>
    public static IServiceCollection AddBacktestServices(this IServiceCollection services) => services
        .AddSingleton<IBacktestService>(sp => new BacktestService(
            sp.GetRequiredService<IStrategyCatalog>(),
            sp.GetRequiredService<ICandleCache>(),
            sp.GetRequiredService<IFeeScheduleProvider>(),
            sp.GetRequiredService<ILoggerFactory>()))
        .AddTransient<BacktestRunViewModel>();
}
