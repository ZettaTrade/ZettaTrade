namespace ZettaTrade.Infrastructure.Exchange;

using System;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// DI wiring for the Bybit venue trading surface (T-127): the named HTTP
/// client (adapter-owned timeout, transparent decompression, the same handler
/// shape the market-data adapters ship) and the trading adapter over it. The
/// endpoints come from the shipped <see cref="BybitTradingEndpoints.Testnet"/>
/// pair — the testnet hosts are the ONLY pair this surface carries by design
/// (trading-safety: a real-money venue must never be reachable by default).
/// Concrete-type registration: the App-side trading-surface composition owns
/// the single <c>ITradingAdapter</c> entry point, so the adapter must not
/// claim the interface descriptor.
/// </summary>
public static class BybitTestnetTradingDependencyInjection
{
    #region Public Methods

    /// <summary>
    /// Register the Bybit testnet trading adapter. Its market-data
    /// pass-through resolves the concrete <see cref="BybitAdapter"/>
    /// singleton (the same venue's own WebSocket/REST set, not the facade —
    /// one socket set apiece), its reconnect policy binds the shipped
    /// supervisor when that is registered, and its secrets resolve from
    /// <see cref="ISecretStore"/> per operation.
    /// </summary>
    public static IServiceCollection AddBybitTestnetTrading(this IServiceCollection services)
    {
        services.AddHttpClient(BybitTradingAdapter.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(BybitTradingAdapter.RequestTimeoutSeconds);
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip
                | DecompressionMethods.Deflate
                | DecompressionMethods.Brotli,
        });

        services.AddSingleton<BybitTradingAdapter>(sp => new BybitTradingAdapter(
            sp.GetRequiredService<ILogger<BybitTradingAdapter>>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<BybitAdapter>(),
            sp.GetRequiredService<ISecretStore>(),
            reconnectSupervisor: sp.GetService<ReconnectSupervisor>()));

        return services;
    }

    #endregion
}
