namespace ZettaTrade.Infrastructure.Exchange;

using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// DI wiring for exchange adapters. Transport details (client name, timeouts) belong
/// to the adapter itself — the composition root only calls the aggregate method.
/// When adapters move to dynamically loaded plugins, these per-adapter methods
/// become the contract the plugin assemblies ship and discovery calls.
/// </summary>
public static class ExchangeAdaptersDependencyInjection
{
    #region Public Methods

    /// <summary>Register all currently shipped exchange adapters (data + paper trading).</summary>
    public static IServiceCollection AddExchangeAdapters(this IServiceCollection services) =>
        services
            .AddBinanceAdapter()
            .AddPaperTrading();

    /// <summary>
    /// Register the Binance market data adapter: its named HTTP client carries
    /// the adapter-owned timeout, so no other module needs to know it.
    /// </summary>
    public static IServiceCollection AddBinanceAdapter(this IServiceCollection services)
    {
        services.AddHttpClient(BinanceAdapter.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(BinanceAdapter.RequestTimeoutSeconds);
        });
        services.AddSingleton<IMarketDataAdapter, BinanceAdapter>();

        return services;
    }

    /// <summary>
    /// Register the paper trading adapter over whatever market data adapter is
    /// registered (mock surface; real keys are never involved).
    /// </summary>
    public static IServiceCollection AddPaperTrading(this IServiceCollection services) =>
        services.AddSingleton<ITradingAdapter>(sp => new PaperTradingAdapter(
            sp.GetRequiredService<IMarketDataAdapter>(),
            sp.GetRequiredService<ILogger<PaperTradingAdapter>>()));

    #endregion
}
