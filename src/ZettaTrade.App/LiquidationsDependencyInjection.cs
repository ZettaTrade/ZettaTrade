namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;

/// <summary>
/// The liquidation tape panel's composition (T-242): the feed — the
/// app-lifetime front over the active-exchange facade's forced-liquidation
/// capability (the raw event streams + the pulse-paced aggregation lanes) —
/// and the panel ViewModel over that feed. Registered right after the
/// heatmap pair (the neighboring panels, the same facade-riding
/// composition), and before MainViewModel.
/// </summary>
public static class LiquidationsDependencyInjection
{
    /// <summary>
    /// Register the liquidation feed and its panel ViewModel. The feed rides
    /// the facade's capability probe (no warm-up fetch — the wires are
    /// demand-subscribed per symbol), the ViewModel wires the workspace
    /// symbol chain and the badge state.
    /// </summary>
    /// <returns>The same collection, over the liquidation registrations appended.</returns>
    public static IServiceCollection AddLiquidationsServices(this IServiceCollection services) => services
        .AddSingleton<LiquidationFeedService>(sp =>
            new LiquidationFeedService(
                sp.GetRequiredService<ActiveExchangeService>(),
                TimeProvider.System,
                sp.GetRequiredService<ILogger<LiquidationFeedService>>()))
        .AddSingleton<LiquidationsViewModel>(sp =>
            new LiquidationsViewModel(
                sp.GetRequiredService<LiquidationFeedService>(),
                sp.GetRequiredService<ViewModels.ChartViewModel>().SymbolSelections,
                sp.GetRequiredService<ILogger<LiquidationsViewModel>>()));
}
