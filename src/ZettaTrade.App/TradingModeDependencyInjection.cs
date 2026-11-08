namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI wiring for the trading-mode module (T-123): the lane service behind the
/// status-bar chip. The chip view model resolves the service directly, so the
/// registration only pins the singleton; consumers resolve
/// <see cref="Services.TradingModeService"/> after the endpoint registry the
/// resolver reads (the registry initializes first in the startup walk).
/// </summary>
public static class TradingModeDependencyInjection
{
    #region Public Methods

    /// <summary>
    /// Register the trading-mode service (T-123) as an application-lifetime
    /// singleton.
    /// </summary>
    public static IServiceCollection AddTradingModeServices(this IServiceCollection services) =>
        services.AddSingleton<Services.TradingModeService>();

    #endregion Public Methods
}
