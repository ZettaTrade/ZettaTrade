namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;

/// <summary>
/// The heatmap panel's composition (T-121): the feed — an app-lifetime
/// singleton aggregating the active-exchange facade's tickers into one
/// ranked page per pulse — and the panel ViewModel over that feed.
/// Registered right after the watchlist pair (the neighboring right-rail
/// panel, the same facade-riding composition), and before MainViewModel.
/// </summary>
public static class HeatmapDependencyInjection
{
    /// <summary>
    /// Register the heatmap feed and its panel ViewModel. The feed rides
    /// <c>IAsyncInitializable</c>, so the startup walk owns its warm-up
    /// universe fetch; the ViewModel wires the tile-press route into the
    /// chart's workspace symbol.
    /// </summary>
    /// <returns>The same collection, over the heatmap registrations appended.</returns>
    public static IServiceCollection AddHeatmapServices(this IServiceCollection services) => services
        .AddSingleton<HeatmapFeedService>(sp =>
            new HeatmapFeedService(
                sp.GetRequiredService<ActiveExchangeService>(),
                sp.GetRequiredService<SymbolListProvider>(),
                sp.GetRequiredService<ILogger<HeatmapFeedService>>()))
        .AddSingleton<HeatmapViewModel>(sp =>
            new HeatmapViewModel(
                sp.GetRequiredService<HeatmapFeedService>(),
                sp.GetRequiredService<ViewModels.ChartViewModel>(),
                sp.GetRequiredService<ILogger<HeatmapViewModel>>()));
}
