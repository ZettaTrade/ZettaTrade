namespace ZettaTrade.Infrastructure.Exchange;

using System;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Demo;

/// <summary>
/// DI wiring for exchange adapters. Transport details (client name, timeouts) belong
/// to the adapter itself — the composition root only calls the aggregate method.
/// When adapters move to dynamically loaded plugins, these per-adapter methods
/// become the contract the plugin assemblies ship and discovery calls.
/// </summary>
public static class ExchangeAdaptersDependencyInjection
{
    #region Public Methods

    /// <summary>
    /// Register all currently shipped exchange adapters by concrete type
    /// (market data + the canned-data demo entry). The single
    /// <see cref="IMarketDataAdapter"/>/<see cref="ITradingAdapter"/> entry
    /// points are composed by the App-side active-exchange wiring over the
    /// facade, so adapters must not claim the interface registrations.
    /// </summary>
    public static IServiceCollection AddExchangeAdapters(this IServiceCollection services)
    {
        // The endpoint registry must come FIRST: the composition-root init
        // loop initializes in registration order, so the settings file is
        // already read (and the configured endpoints cached) before the
        // first adapter resolves them synchronously.
        return services
            .AddExchangeEndpointRegistry()
            .AddBinanceAdapter()
            .AddDemoAdapter();
    }

    /// <summary>
    /// Register the adapter registry seam's configuration piece: a
    /// read-once settings snapshot of exchange transport endpoints, exposed
    /// synchronously after initialization (missing entries fall back to the
    /// shipped production endpoints).
    /// </summary>
    public static IServiceCollection AddExchangeEndpointRegistry(this IServiceCollection services) =>
        services.AddSingleton<ExchangeEndpointRegistry>();

    /// <summary>
    /// Register the Binance market data adapter: its named HTTP client carries
    /// the adapter-owned timeout and transparent gzip/deflate/brotli
    /// decompression (the factory client never enables automatic decompression
    /// by itself, and an uncompressed <c>exchangeInfo</c> response is
    /// multi-megabyte — the decompression is what keeps symbol-list loads in
    /// the sub-second range). Endpoints come from the
    /// <see cref="ExchangeEndpointRegistry"/> snapshot (settings override or
    /// shipped production defaults). Concrete-type registration: the
    /// App-side active-exchange wiring owns the single
    /// <c>IMarketDataAdapter</c> entry point, so the adapter itself must not
    /// claim the interface descriptor.
    /// </summary>
    public static IServiceCollection AddBinanceAdapter(this IServiceCollection services)
    {
        services.AddHttpClient(BinanceAdapter.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(BinanceAdapter.RequestTimeoutSeconds);
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip
                | DecompressionMethods.Deflate
                | DecompressionMethods.Brotli,
        });
        services.AddSingleton<BinanceAdapter>(sp =>
            new BinanceAdapter(
                sp.GetRequiredService<ILogger<BinanceAdapter>>(),
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<ExchangeEndpointRegistry>().Binance));

        return services;
    }

    /// <summary>
    /// Register the paper trading adapter over the active-exchange facade
    /// (mock surface; real keys are never involved). This registration
    /// belongs to the App-side active-exchange composition: infrastructure
    /// wiring alone carries no <c>IMarketDataAdapter</c> to wrap.
    /// </summary>
    public static IServiceCollection AddPaperTrading(this IServiceCollection services) =>
        services.AddSingleton<ITradingAdapter>(sp => new PaperTradingAdapter(
            sp.GetRequiredService<IMarketDataAdapter>(),
            sp.GetRequiredService<ILogger<PaperTradingAdapter>>()));

    #endregion
}
