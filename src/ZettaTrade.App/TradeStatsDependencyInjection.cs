namespace ZettaTrade.App;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// DI wiring for the trade-statistics flow. The read query shares the order
/// journal's registered <see cref="DbContextOptions{OrderHistoryDbContext}"/>
/// (Infrastructure's <c>AddOrderHistory</c> owns that singleton); the view
/// model is transient — every open starts a fresh scan of the journal as it
/// stands right now.
/// </summary>
public static class TradeStatsDependencyInjection
{
    /// <summary>Register the trade-statistics read query and the stats window view model.</summary>
    public static IServiceCollection AddTradeStatsServices(this IServiceCollection services) => services
        .AddSingleton<IOrderStatsQuery>(sp => new SqliteOrderStatsQuery(
            sp.GetRequiredService<DbContextOptions<OrderHistoryDbContext>>()))
        .AddTransient<TradeStatsViewModel>();
}
