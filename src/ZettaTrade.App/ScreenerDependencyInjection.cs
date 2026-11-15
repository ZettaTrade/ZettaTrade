namespace ZettaTrade.App;

using Livery.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// The screener panel's composition (T-195): the feed — an app-lifetime
/// singleton aggregating the active-exchange facade's tickers into one
/// table page per pulse — the trend computer over that feed (T-239), and
/// the panel ViewModel over the pair.
/// Registered right after the heatmap pair (the neighboring rail panel, the
/// same facade-riding composition), and before MainViewModel.
/// </summary>
public static class ScreenerDependencyInjection
{
    /// <summary>
    /// Register the screener feed, its trend computer and its panel
    /// ViewModel. The feed rides <c>IAsyncInitializable</c>, so the startup
    /// walk owns its warm-up universe fetch; the trend computer rides the
    /// walk too (its tuning load and page subscription arm there); the
    /// ViewModel wires the row-press route into the chart's workspace
    /// symbol.
    /// </summary>
    /// <returns>The same collection, over the screener registrations appended.</returns>
    public static IServiceCollection AddScreenerServices(this IServiceCollection services) => services
        .AddSingleton<ScreenerFeedService>(sp =>
            new ScreenerFeedService(
                sp.GetRequiredService<ActiveExchangeService>(),
                sp.GetRequiredService<SymbolListProvider>(),
                sp.GetRequiredService<ILogger<ScreenerFeedService>>()))
        .AddSingleton<MtfTrendService>(sp =>
            new MtfTrendService(
                sp.GetRequiredService<ScreenerFeedService>(),
                sp.GetRequiredService<ICandleCache>(),
                sp.GetRequiredService<ActiveExchangeService>(),
                sp.GetRequiredService<CandleBackfillService>(),
                sp.GetRequiredService<ISettingsService>(),
                sp.GetRequiredService<ILogger<MtfTrendService>>()))
        .AddSingleton<ScreenerViewModel>(sp =>
            new ScreenerViewModel(
                sp.GetRequiredService<ScreenerFeedService>(),
                sp.GetRequiredService<ViewModels.ChartViewModel>(),
                sp.GetRequiredService<MtfTrendService>(),
                sp.GetRequiredService<ILocalizationManager>(),
                sp.GetRequiredService<ILogger<ScreenerViewModel>>()));
}
