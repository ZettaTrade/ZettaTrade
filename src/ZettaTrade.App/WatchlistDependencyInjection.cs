namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The watchlist panel's composition (T-120): the feed — an app-lifetime
/// singleton pricing the panel's rows over the active-exchange facade — and
/// the panel ViewModel over that feed. Registered after the chart ViewModel
/// the workspace selection rides in and before MainViewModel, matching the
/// book/tape panel pair.
/// </summary>
public static class WatchlistDependencyInjection
{
    /// <summary>
    /// Register the watchlist feed and its panel ViewModel. The feed keys
    /// its row set to the workspace selection (the chart's symbol combo), so
    /// the panel always carries the symbol the user is working, plus the
    /// majors starter rows.
    /// </summary>
    /// <returns>The same collection, over the watchlist registrations appended.</returns>
    public static IServiceCollection AddWatchlistServices(this IServiceCollection services) => services
        .AddSingleton<WatchlistFeedService>(sp =>
            new WatchlistFeedService(
                sp.GetRequiredService<IMarketDataAdapter>(),
                sp.GetRequiredService<ViewModels.ChartViewModel>().SymbolSelections,
                sp.GetRequiredService<ILogger<WatchlistFeedService>>()))
        .AddSingleton<WatchlistViewModel>(sp =>
            new WatchlistViewModel(
                sp.GetRequiredService<WatchlistFeedService>(),
                sp.GetRequiredService<ILogger<WatchlistViewModel>>()));
}
