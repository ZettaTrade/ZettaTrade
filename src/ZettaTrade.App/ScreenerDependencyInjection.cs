namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;

/// <summary>
/// The screener panel's composition (T-195): the feed — an app-lifetime
/// singleton aggregating the active-exchange facade's tickers into one
/// table page per pulse — and the panel ViewModel over that feed.
/// Registered right after the heatmap pair (the neighboring rail panel, the
/// same facade-riding composition), and before MainViewModel.
/// </summary>
public static class ScreenerDependencyInjection
{
    /// <summary>
    /// Register the screener feed and its panel ViewModel. The feed rides
    /// <c>IAsyncInitializable</c>, so the startup walk owns its warm-up
    /// universe fetch; the ViewModel wires the row-press route into the
    /// chart's workspace symbol.
    /// </summary>
    /// <returns>The same collection, over the screener registrations appended.</returns>
    public static IServiceCollection AddScreenerServices(this IServiceCollection services) => services
        .AddSingleton<ScreenerFeedService>(sp =>
            new ScreenerFeedService(
                sp.GetRequiredService<ActiveExchangeService>(),
                sp.GetRequiredService<ILogger<ScreenerFeedService>>()))
        .AddSingleton<ScreenerViewModel>(sp =>
            new ScreenerViewModel(
                sp.GetRequiredService<ScreenerFeedService>(),
                sp.GetRequiredService<ViewModels.ChartViewModel>(),
                sp.GetRequiredService<ILogger<ScreenerViewModel>>()));
}
