namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Sound;

/// <summary>
/// DI wiring for the price-alerts module (T-096). The engine is a session
/// singleton over the reactive settings service, the active market-data
/// source, the beep seam and the system clock; the beep implementation is
/// platform-side Infrastructure. The manager view model is transient — every
/// open renders the CURRENT roster over the shared singleton engine (the
/// attach pattern owns its subscriptions).
/// </summary>
public static class AlertsDependencyInjection
{
    /// <summary>Register the alert engine (the roster's sole owner) and the manager view model.</summary>
    public static IServiceCollection AddAlertsServices(this IServiceCollection services) => services
        .AddSingleton<IAlertSoundPlayer, AlertBeepPlayer>()
        .AddSingleton(TimeProvider.System)
        .AddSingleton<IAlertEngine, AlertEngine>()
        .AddTransient<AlertsViewModel>();
}
