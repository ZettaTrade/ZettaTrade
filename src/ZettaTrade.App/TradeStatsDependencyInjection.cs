namespace ZettaTrade.App;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// DI wiring for the trade-statistics flow. The read query shares the order
/// journal's registered <see cref="DbContextOptions{OrderHistoryDbContext}"/>
/// (Infrastructure's <c>AddOrderHistory</c> owns that singleton); the view
/// model is transient — every open starts a fresh scan of the journal as it
/// stands right now. The export routes (T-210) resolve the traded surface
/// (the single <see cref="ZettaTrade.Core.Interfaces.ITradingAdapter"/>) and
/// the Infrastructure export writers.
/// </summary>
public static class TradeStatsDependencyInjection
{
    /// <summary>Register the trade-statistics read query, the excursion reader (T-251, riding the registered candle cache) and the stats window view model.</summary>
    public static IServiceCollection AddTradeStatsServices(this IServiceCollection services) => services
        .AddSingleton<IOrderStatsQuery>(sp => new SqliteOrderStatsQuery(
            sp.GetRequiredService<DbContextOptions<OrderHistoryDbContext>>()))
        .AddTransient<TradeExcursionReader>()
        .AddTransient<TradeStatsViewModel>();
}
