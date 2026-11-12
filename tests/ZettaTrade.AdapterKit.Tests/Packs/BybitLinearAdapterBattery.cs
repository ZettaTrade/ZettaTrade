namespace ZettaTrade.AdapterKit.Tests.Packs;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ZettaTrade.AdapterKit.Tests.Battery;
using ZettaTrade.AdapterKit.Tests.Capture;
using ZettaTrade.AdapterKit.Tests.Harness;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// The Bybit adapter's conformance battery run on the LINEAR face (T-197):
/// the same adapter, judged through a pack whose universe carries only
/// linear-perp symbols — the canned spot catalog answers an empty list, so
/// the served universe is the perps and the conformance probe is a
/// perpetual. The battery therefore rides TWO rigs: the LINEAR rig is the
/// session's server (every subscribed symbol dials it), while the spot rig
/// stays an owned extra no leg ever dials — the category discipline's
/// structural proof. The canned REST is routed by the category query
/// fragments (non-overlapping, order-independent), and the transcript
/// replays the linear ticker family with its funding members.
/// </summary>
public sealed class BybitLinearAdapterBattery : AdapterConformanceBattery
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

    /// <summary>Create the Bybit linear pack's battery.</summary>
    public BybitLinearAdapterBattery()
        : base(AdapterCapturePack.FromDirectory("BybitLinear"))
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
        // TWO rigs (T-197): the linear rig is the session's server — the
        // conformance probe is a perpetual and its subscriptions connect
        // THAT socket — while the spot rig stays an owned extra the session
        // closes right after the adapter. The endpoints are honest loopback
        // URLs: the unresolvable REST host fails loudly on any unplanned
        // dial, and no wire ever reaches the internet.
        var spotServer = await KitLoopbackServer.StartAsync(ct);
        var linearServer = await KitLoopbackServer.StartAsync(ct);

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
                WsUrl: spotServer.StreamUrl,
                LinearWsUrl: linearServer.StreamUrl));

        return AdapterSession.Open(adapter, linearServer, restProvider, ownedExtra: spotServer);
    }

    #endregion
}
