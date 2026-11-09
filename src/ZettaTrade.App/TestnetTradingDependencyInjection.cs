namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Exchange;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// DI wiring for the testnet venue trading surface (T-127): the Bybit
/// trading adapter's transport registration, the delegating traded surface
/// (the single <see cref="ITradingAdapter"/> entry point every consumer also
/// rides), and the venue transitions' persistent reflector. The paper lane
/// is untouched — the toggle (section <c>Exchange</c>, key
/// <c>TestnetTradingEnabled</c>) stays the only arming seam, default off.
/// </summary>
/// <remarks>
/// Registration order matters for the composition-root startup walk: this
/// composition MUST land after the active-exchange facade registration (the
/// facade and its paper adapter resolve behind the surface) and before the
/// trading-mode registration (the surface initializes its standing lane
/// ahead of the trading-mode resolver's construction).
/// </remarks>
public static class TestnetTradingDependencyInjection
{
    /// <summary>
    /// Compose the testnet trading stack: the venue transport, the delegating
    /// surface (registered BY concrete type over the interface descriptor so
    /// both recovery paths and handlers resolve the same singleton) and the
    /// venue order journal reflector.
    /// </summary>
    public static IServiceCollection AddTestnetTradingServices(this IServiceCollection services) => services
        .AddBybitTestnetTrading()
        .AddSingleton(sp => new VenueOrderLedger(
            sp.GetRequiredService<BybitTradingAdapter>().OrderUpdates,
            sp.GetRequiredService<IOrderHistoryStore>(),
            sp.GetRequiredService<ILogger<VenueOrderLedger>>()))
        .AddSingleton<TradingSurfaceService>()
        .AddSingleton<ITradingAdapter>(sp => sp.GetRequiredService<TradingSurfaceService>());
}
