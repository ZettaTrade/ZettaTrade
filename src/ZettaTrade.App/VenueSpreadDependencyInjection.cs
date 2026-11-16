namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;

/// <summary>
/// The venue-spread panel's composition (T-253): the cross-venue
/// sampler over the facade's fleet and the candle cache (the ticker
/// race under its wait budget, the basis reads sharing the correlation
/// pass's bounded two-slot budget) and the panel ViewModel over that
/// sampler. Registered right after the correlation pair (the
/// neighboring analysis panel, the same facade-riding composition),
/// and before MainViewModel.
/// </summary>
public static class VenueSpreadDependencyInjection
{
    /// <summary>
    /// Register the venue sampler and its panel ViewModel. The pass is
    /// on-demand (no IAsyncInitializable warm-up — the adapters' lazy
    /// dial connects a venue on the pass's own first subscription), the
    /// ViewModel is a workspace singleton over it.
    /// </summary>
    /// <returns>The same collection, over the venue-spread registrations appended.</returns>
    public static IServiceCollection AddVenueSpreadServices(this IServiceCollection services) => services
        .AddSingleton<VenueSpreadService>(sp =>
            new VenueSpreadService(
                sp.GetRequiredService<ActiveExchangeService>(),
                sp.GetRequiredService<Infrastructure.Persistence.ICandleCache>(),
                sp.GetRequiredService<ILogger<VenueSpreadService>>()))
        .AddSingleton<VenueSpreadViewModel>(sp =>
            new VenueSpreadViewModel(
                sp.GetRequiredService<VenueSpreadService>(),
                sp.GetRequiredService<Livery.Localization.ILocalizationManager>(),
                sp.GetRequiredService<ILogger<VenueSpreadViewModel>>()));
}
