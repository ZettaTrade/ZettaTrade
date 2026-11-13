namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Configuration;
using ZettaTrade.Infrastructure.Exchange;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// DI wiring for the portfolio-value snapshot service (T-209). The service
/// is a singleton (one cadence loop over the paper account) and
/// initializes through the IAsyncInitializable contract during the
/// composition root's startup walk — registered after the value
/// store, so its migrations are applied before the loop's first write.
/// The factory form pins the account: the paper adapter's own singleton
/// instance (paper-first — the concrete type names the account; other
/// trading accounts join later).
/// </summary>
public static class PortfolioValueDependencyInjection
{
    /// <summary>Register the snapshot service.</summary>
    public static IServiceCollection AddPortfolioValueServices(this IServiceCollection services) => services
        .AddSingleton<PortfolioValueSnapshotService>(sp => new PortfolioValueSnapshotService(
            sp.GetRequiredService<PaperTradingAdapter>(),
            sp.GetRequiredService<IMarketDataAdapter>(),
            sp.GetRequiredService<IPortfolioValueStore>(),
            sp.GetRequiredService<ILogger<PortfolioValueSnapshotService>>()));
}
