namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Demo;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// DI wiring for the active-exchange facade. Concrete exchange adapters are
/// registered by type by the infrastructure aggregate; this composition
/// collects them into one <see cref="ActiveExchangeService"/>, owns the
/// single <see cref="IMarketDataAdapter"/> entry point (the facade itself)
/// and registers the paper trading adapter over it, so trading follows the
/// same context as market data. Core contracts stay untouched.
/// </summary>
public static class ActiveExchangeDependencyInjection
{
    /// <summary>
    /// Compose the active-exchange stack: persisted selection + atomic
    /// stream switching through the facade, trading included. The demo lane
    /// is the FIRST registered adapter, so the shipped default (no persisted
    /// choice yet — a fresh install, i.e. exactly the first-run wizard's
    /// audience) is the canned-data demo, and nothing real is touched
    /// without an explicit pick (T-099); an existing install restores its
    /// own persisted id regardless of the order.
    /// </summary>
    public static IServiceCollection AddActiveExchange(this IServiceCollection services) => services
        .AddSingleton<ActiveExchangeService>(sp => new ActiveExchangeService(
            [
                sp.GetRequiredService<DemoMarketDataAdapter>(),
                sp.GetRequiredService<BinanceAdapter>(),
                sp.GetRequiredService<BybitAdapter>(),
            ],
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<ILogger<ActiveExchangeService>>()))
        .AddSingleton<IMarketDataAdapter>(sp => sp.GetRequiredService<ActiveExchangeService>())
        .AddPaperTrading();
}
