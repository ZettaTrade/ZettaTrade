namespace ZettaTrade.Infrastructure.Exchange;

using System;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// DI wiring for the Binance venue trading surface (T-193): the named HTTP
/// client (adapter-owned timeout, transparent decompression, the same handler
/// shape the market-data adapters ship) and the trading adapter over it. The
/// endpoints come from the shipped <see cref="BinanceTradingEndpoints.Testnet"/>
/// pair — the testnet hosts are the ONLY pair this surface carries by design
/// (trading-safety: a real-money venue must never be reachable by default).
/// Concrete-type registration: the App-side trading-surface composition owns
/// the single <c>ITradingAdapter</c> entry point, so the adapter must not
/// claim the interface descriptor.
/// </summary>
public static class BinanceTestnetTradingDependencyInjection
{
    #region Public Methods

    /// <summary>
    /// Register the Binance testnet trading adapter. Its market-data
    /// pass-through resolves the concrete <see cref="BinanceAdapter"/>
    /// singleton (the same venue's own WebSocket/REST set, not the facade —
    /// one socket set apiece), its reconnect policy binds the shipped
    /// supervisor when that is registered, and its secrets resolve from
    /// <see cref="ISecretStore"/> per operation.
    /// </summary>
    public static IServiceCollection AddBinanceTestnetTrading(this IServiceCollection services)
    {
        services.AddHttpClient(BinanceTradingAdapter.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(BinanceTradingAdapter.RequestTimeoutSeconds);
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip
                | DecompressionMethods.Deflate
                | DecompressionMethods.Brotli,
        });

        services.AddSingleton<BinanceTradingAdapter>(sp => new BinanceTradingAdapter(
            sp.GetRequiredService<ILogger<BinanceTradingAdapter>>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<BinanceAdapter>(),
            sp.GetRequiredService<ISecretStore>(),
            reconnectSupervisor: sp.GetService<ReconnectSupervisor>()));

        return services;
    }

    #endregion
}
