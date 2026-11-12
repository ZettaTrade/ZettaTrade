namespace ZettaTrade.AdapterKit.Tests.Packs;

using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ZettaTrade.AdapterKit.Tests.Battery;
using ZettaTrade.AdapterKit.Tests.Capture;
using ZettaTrade.AdapterKit.Tests.Harness;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// The Binance trading shell's conformance battery run (T-193): the pack's
/// market-data face is DELEGATED — the shell rides a real
/// <see cref="BinanceAdapter"/> (both sockets dial the kit's own loopback
/// rig: the delegate's combined stream on <c>/stream</c>, the shell's
/// private listen-key session on <c>/ws/…</c>) and a canned REST disguise
/// (the catalog and the listen-key capture answer with zero network). The
/// delegate is the session's additionally-owned disposable: the shell never
/// disposes its container-owned delegate, so the rig owns the pair.
/// </summary>
/// <remarks>
/// The rest base URL is deliberately unresolvable: if the canned disguise
/// were ever mis-wired, the kit fails loudly instead of quietly hitting the
/// internet. The battery facts exercise NO order operation, so the shell's
/// private session never stands during them — the connect leg alone proves
/// the listen-key flow end to end (issue the key, dial its stream).
/// </remarks>
public sealed class BinanceTradingAdapterBattery : AdapterConformanceBattery
{
    #region Constants

    /// <summary>The canned REST request URL's host: routed by the disguise, never dialed.</summary>
    private const string CannedRestBaseUrl = "http://kit-canned-gateway";

    /// <summary>The blank endpoint configuration the constructor-guard probe carries.</summary>
    private const string BlankEndpoint = " ";

    /// <summary>The probe transport URL the constructor-guard probe carries (guarded before any transport is touched).</summary>
    private const string ProbeStreamUrl = "ws://127.0.0.1:9/ws";

    #endregion

    #region Constructor

    /// <summary>Create the Binance trading pack's battery.</summary>
    public BinanceTradingAdapterBattery()
        : base(AdapterCapturePack.FromDirectory("BinanceTrading"))
    {
    }

    #endregion

    #region Tests: constructor guard

    /// <inheritdoc/>
    protected override Task RunCtorGuardProbeAsync()
    {
        var services = new ServiceCollection();
        services.AddHttpClient(BinanceAdapter.HttpClientName);
        services.AddHttpClient(BinanceTradingAdapter.HttpClientName);
        using var provider = services.BuildServiceProvider();

        BinanceTradingAdapter? leaked = null;
        try
        {
            FluentActions.Invoking(() => leaked = new BinanceTradingAdapter(
                    NullLogger<BinanceTradingAdapter>.Instance,
                    provider.GetRequiredService<IHttpClientFactory>(),
                    new BinanceAdapter(
                        NullLogger<BinanceAdapter>.Instance,
                        provider.GetRequiredService<IHttpClientFactory>(),
                        new BinanceEndpoints(CannedRestBaseUrl, ProbeStreamUrl)),
                    new KitSecretStub(),
                    new BinanceTradingEndpoints(
                        RestBaseUrl: BlankEndpoint,
                        PrivateWsUrl: ProbeStreamUrl)))
                .Should().ThrowExactly<ArgumentException>().WithParameterName("endpoints");
        }
        finally
        {
            // A guard that wrongly passed must not leak adapter-owned loops.
            if (leaked is not null)
            {
                leaked.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        return Task.CompletedTask;
    }

    #endregion

    #region Battery: session

    /// <inheritdoc/>
    protected override async ValueTask<AdapterSession> OpenSessionAsync(CancellationToken ct)
    {
        var server = await KitLoopbackServer.StartAsync(ct);

        var services = new ServiceCollection();
        var canned = new KitCannedRestHandler(Pack.RestCannedRoutes);
        services.AddHttpClient(BinanceAdapter.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => canned);
        services.AddHttpClient(BinanceTradingAdapter.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => canned);
        var restProvider = services.BuildServiceProvider();

        var marketData = new BinanceAdapter(
            NullLogger<BinanceAdapter>.Instance,
            restProvider.GetRequiredService<IHttpClientFactory>(),
            new BinanceEndpoints(
                RestBaseUrl: CannedRestBaseUrl,
                WsUrl: server.StreamUrl));

        // The shell's private-session base: the same rig, the /ws subpath the
        // listen-key stream dials ({base}/{listenKey}).
        var adapter = new BinanceTradingAdapter(
            NullLogger<BinanceTradingAdapter>.Instance,
            restProvider.GetRequiredService<IHttpClientFactory>(),
            marketData,
            new KitSecretStub(),
            new BinanceTradingEndpoints(
                RestBaseUrl: CannedRestBaseUrl,
                PrivateWsUrl: server.StreamUrl.Replace("/stream", "/ws", System.StringComparison.Ordinal)));

        return AdapterSession.Open(adapter, server, restProvider, ownedExtra: marketData);
    }

    #endregion

    /// <summary>The kit's secret stub: the testnet key pair the shell fetches per operation (never a real key).</summary>
    private sealed class KitSecretStub : ISecretStore
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => key switch
        {
            BinanceTradingAdapter.ApiKeySecretKey => Task.FromResult<string?>("kit-api-key"),
            BinanceTradingAdapter.ApiSecretSecretKey => Task.FromResult<string?>("kit-api-secret"),
            _ => Task.FromResult<string?>(null),
        };

        public Task SetAsync(string key, string value, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RemoveAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListKeysAsync(string prefix = "", CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
    }
}
