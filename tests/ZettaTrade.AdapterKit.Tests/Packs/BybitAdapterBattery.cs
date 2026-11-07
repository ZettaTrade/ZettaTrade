namespace ZettaTrade.AdapterKit.Tests.Packs;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ZettaTrade.AdapterKit.Tests.Battery;
using ZettaTrade.AdapterKit.Tests.Capture;
using ZettaTrade.AdapterKit.Tests.Harness;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// The Bybit adapter's conformance battery run: the pack rides the kit's own
/// loopback rig (the transcript sessions replay through it) and a canned
/// REST disguise (the catalog capture answers the symbols leg with zero
/// network). The rest base URL is deliberately unresolvable: if the canned
/// disguise were ever mis-wired, the kit fails loudly instead of quietly
/// hitting the internet.
/// </summary>
public sealed class BybitAdapterBattery : AdapterConformanceBattery
{
    #region Constants

    /// <summary>The canned REST request URL's host: routed by the disguise, never dialed.</summary>
    private const string CannedRestBaseUrl = "http://kit-canned-gateway";

    /// <summary>The blank endpoint configuration the constructor-guard probe carries.</summary>
    private const string BlankEndpoint = " ";

    /// <summary>The probe transport URL the constructor-guard probe carries (guarded before any transport is touched).</summary>
    private const string ProbeStreamUrl = "ws://127.0.0.1:9/stream";

    #endregion

    #region Constructor

    /// <summary>Create the Bybit pack's battery.</summary>
    public BybitAdapterBattery()
        : base(AdapterCapturePack.FromDirectory("Bybit"))
    {
    }

    #endregion

    #region Tests: constructor guard

    /// <inheritdoc/>
    protected override async Task RunCtorGuardProbeAsync()
    {
        var services = new ServiceCollection();
        services.AddHttpClient(BybitAdapter.HttpClientName);
        using var provider = services.BuildServiceProvider();

        BybitAdapter? leaked = null;
        try
        {
            FluentActions.Invoking(() => leaked = new BybitAdapter(
                    NullLogger<BybitAdapter>.Instance,
                    provider.GetRequiredService<IHttpClientFactory>(),
                    new BybitEndpoints(
                        RestBaseUrl: BlankEndpoint,
                        WsUrl: ProbeStreamUrl)))
                .Should().ThrowExactly<ArgumentException>().WithParameterName("endpoints");
        }
        finally
        {
            // A guard that wrongly passed must not leak adapter-owned loops.
            if (leaked is not null)
            {
                await leaked.DisposeAsync();
            }
        }
    }

    #endregion

    #region Battery: session

    /// <inheritdoc/>
    protected override async ValueTask<AdapterSession> OpenSessionAsync(CancellationToken ct)
    {
        var server = await KitLoopbackServer.StartAsync(ct);

        var services = new ServiceCollection();
        var canned = new KitCannedRestHandler(Pack.RestCannedRoutes);
        services.AddHttpClient(BybitAdapter.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => canned);
        var restProvider = services.BuildServiceProvider();

        var adapter = new BybitAdapter(
            NullLogger<BybitAdapter>.Instance,
            restProvider.GetRequiredService<IHttpClientFactory>(),
            new BybitEndpoints(
                RestBaseUrl: CannedRestBaseUrl,
                WsUrl: server.StreamUrl));

        return AdapterSession.Open(adapter, server, restProvider);
    }

    #endregion
}
