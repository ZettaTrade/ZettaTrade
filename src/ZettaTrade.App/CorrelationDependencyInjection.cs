namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;

/// <summary>
/// The correlation matrix panel's composition (T-252): the on-demand
/// computer over the candle cache (the reads share the bounded budget the
/// screener's trend computer walks) and the panel ViewModel over that
/// computer, the watchlist catalog and the chart's compare seam.
/// Registered right after the screener pair (the neighboring analysis
/// panel, the same facade-riding composition), and before MainViewModel.
/// </summary>
public static class CorrelationDependencyInjection
{
    /// <summary>
    /// Register the correlation computer and its panel ViewModel. The
    /// pass is on-demand (no IAsyncInitializable warm-up — the cache's
    /// standing coverage answers what it holds), the ViewModel wires the
    /// cell-press route into the chart's compare view.
    /// </summary>
    /// <returns>The same collection, over the correlation registrations appended.</returns>
    public static IServiceCollection AddCorrelationServices(this IServiceCollection services) => services
        .AddSingleton<CorrelationService>(sp =>
            new CorrelationService(
                sp.GetRequiredService<ActiveExchangeService>(),
                sp.GetRequiredService<Infrastructure.Persistence.ICandleCache>(),
                sp.GetRequiredService<ILogger<CorrelationService>>()))
        .AddSingleton<CorrelationViewModel>(sp =>
            new CorrelationViewModel(
                sp.GetRequiredService<CorrelationService>(),
                sp.GetRequiredService<WatchlistCatalogService>(),
                sp.GetRequiredService<ViewModels.ChartViewModel>(),
                sp.GetRequiredService<Livery.Localization.ILocalizationManager>(),
                sp.GetRequiredService<ILogger<CorrelationViewModel>>()));
}
