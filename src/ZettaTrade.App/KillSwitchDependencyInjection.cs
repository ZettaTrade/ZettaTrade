namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// DI wiring for the kill switch (T-188): the venue set (the app's two
/// trading adapters, in sweep order — the paper simulator leads, the Bybit
/// testnet venue follows) and the sweep service that confirms once and
/// commands every working order and open position across them (best effort
/// per item). Both adapters resolve behind the traded-surface composition,
/// so the sweep commands them directly — the panic action never routes
/// through the current lane.
/// </summary>
public static class KillSwitchDependencyInjection
{
    /// <summary>
    /// Register the kill-switch venue set and the sweep service (stateless:
    /// every sweep re-reads the venues' orders and positions).
    /// </summary>
    public static IServiceCollection AddKillSwitchServices(this IServiceCollection services) => services
        .AddSingleton<KillSwitchVenues>(sp => new KillSwitchVenues(
            new ITradingAdapter[]
            {
                sp.GetRequiredService<PaperTradingAdapter>(),
                sp.GetRequiredService<BybitTradingAdapter>(),
            }))
        .AddSingleton<KillSwitchService>();
}
