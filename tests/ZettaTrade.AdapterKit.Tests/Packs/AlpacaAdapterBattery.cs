namespace ZettaTrade.AdapterKit.Tests.Packs;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ZettaTrade.AdapterKit.Tests.Battery;
using ZettaTrade.AdapterKit.Tests.Capture;
using ZettaTrade.AdapterKit.Tests.Harness;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// The Alpaca adapter's conformance battery run (T-198): the pack rides the
/// kit's own loopback rig with an auto-responder answering the venue's auth
/// action (the stream must authenticate within its window before any
/// subscribe rides), and a canned REST disguise answers the assets catalog.
/// The rest base URL is deliberately unresolvable: if the canned disguise
/// were ever mis-wired, the kit fails loudly instead of quietly hitting the
/// internet. The user-keyed pair rides the kit's pairs-based secret stub —
/// never a real credential.
/// </summary>
public sealed class AlpacaAdapterBattery : AdapterConformanceBattery
{
    #region Constants

    /// <summary>The canned REST request URL's host: routed by the disguise, never dialed (both REST faces share it).</summary>
    private const string CannedRestBaseUrl = "http://kit-canned-gateway";

    /// <summary>The blank endpoint configuration the constructor-guard probe carries.</summary>
    private const string BlankEndpoint = " ";

    /// <summary>The probe transport URL the constructor-guard probe carries (guarded before any transport is touched).</summary>
    private const string ProbeStreamUrl = "ws://127.0.0.1:9/stream";

    /// <summary>The auth action's wire fragment the rig's responder answers.</summary>
    private const string AuthActionFragment = "\"action\":\"auth\"";

    /// <summary>The venue's auth ack, exactly as the v2 stream answers it (an array of one success event).</summary>
    private const string AuthAckFrame = "[{\"T\":\"success\",\"msg\":\"authenticated\"}]";

    /// <summary>The kit stub's key pair (never a real credential).</summary>
    private const string StubApiKey = "kit-api-key";
    private const string StubApiSecret = "kit-api-secret";

    #endregion

    #region Constructor

    /// <summary>Create the Alpaca pack's battery.</summary>
    public AlpacaAdapterBattery()
        : base(AdapterCapturePack.FromDirectory("Alpaca"))
    {
    }

    #endregion

    #region Tests: constructor guard

    /// <inheritdoc/>
    protected override async Task RunCtorGuardProbeAsync()
    {
        var services = new ServiceCollection();
        services.AddHttpClient(AlpacaAdapter.HttpClientName);
        using var provider = services.BuildServiceProvider();

        AlpacaAdapter? leaked = null;
        try
        {
            FluentActions.Invoking(() => leaked = new AlpacaAdapter(
                    NullLogger<AlpacaAdapter>.Instance,
                    provider.GetRequiredService<IHttpClientFactory>(),
                    new KitSecretStoreStub(new Dictionary<string, string>
                    {
                        [AlpacaAdapter.ApiKeySecretKey] = StubApiKey,
                        [AlpacaAdapter.ApiSecretSecretKey] = StubApiSecret,
                    }),
                    new AlpacaEndpoints(
                        RestBaseUrl: BlankEndpoint,
                        CatalogRestBaseUrl: BlankEndpoint,
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
        var server = await KitLoopbackServer.StartAsync(ct, AnswerAuthActionAsync);
        var services = new ServiceCollection();
        var canned = new KitCannedRestHandler(Pack.RestCannedRoutes);
        services.AddHttpClient(AlpacaAdapter.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => canned);
        var restProvider = services.BuildServiceProvider();

        var adapter = new AlpacaAdapter(
            NullLogger<AlpacaAdapter>.Instance,
            restProvider.GetRequiredService<IHttpClientFactory>(),
            new KitSecretStoreStub(new Dictionary<string, string>
            {
                [AlpacaAdapter.ApiKeySecretKey] = StubApiKey,
                [AlpacaAdapter.ApiSecretSecretKey] = StubApiSecret,
            }),
            new AlpacaEndpoints(
                RestBaseUrl: CannedRestBaseUrl,
                CatalogRestBaseUrl: CannedRestBaseUrl,
                WsUrl: server.StreamUrl));

        return AdapterSession.Open(adapter, server, restProvider);
    }

    /// <summary>
    /// The rig's auth responder (T-198): the venue's control protocol,
    /// scripted. Every auth action the adapter sends on any socket session —
    /// the lazy start's and every reconnect's — is answered with the
    /// venue's success ack, so the adapter's own 10 s auth window never
    /// expires on a healthy rig.
    /// </summary>
    private static IReadOnlyList<string>? AnswerAuthActionAsync(string frame) =>
        frame.Contains(AuthActionFragment, StringComparison.Ordinal)
            ? [AuthAckFrame]
            : null;

    #endregion
}
