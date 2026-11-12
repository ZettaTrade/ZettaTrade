namespace ZettaTrade.App;

using Livery.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The watchlist panel's composition (T-120, the catalog added in T-196):
/// the catalog — an app-lifetime singleton owning the user-managed
/// watchlist document over the settings store — the feed over that catalog
/// and the panel ViewModel over both. Registered after the chart ViewModel
/// the workspace selection rides in and before MainViewModel, matching the
/// book/tape panel pair.
/// </summary>
public static class WatchlistDependencyInjection
{
    /// <summary>
    /// Register the watchlist catalog, feed and panel ViewModel. The feed
    /// keys its row set to the catalog's active list (plus the workspace
    /// selection's overlay row), so the panel always carries the symbol the
    /// user is working, plus the lists the user manages.
    /// </summary>
    /// <returns>The same collection, over the watchlist registrations appended.</returns>
    public static IServiceCollection AddWatchlistServices(this IServiceCollection services) => services
        .AddSingleton<WatchlistCatalogService>(sp => new WatchlistCatalogService(
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<ILogger<WatchlistCatalogService>>()))
        .AddSingleton<WatchlistFeedService>(sp =>
            new WatchlistFeedService(
                sp.GetRequiredService<IMarketDataAdapter>(),
                sp.GetRequiredService<ViewModels.ChartViewModel>().SymbolSelections,
                sp.GetRequiredService<WatchlistCatalogService>(),
                sp.GetRequiredService<ILogger<WatchlistFeedService>>()))
        .AddSingleton<WatchlistViewModel>(sp =>
            new WatchlistViewModel(
                sp.GetRequiredService<WatchlistFeedService>(),
                sp.GetRequiredService<WatchlistCatalogService>(),
                sp.GetRequiredService<ILocalizationManager>(),
                sp.GetRequiredService<ILogger<WatchlistViewModel>>()));
}
