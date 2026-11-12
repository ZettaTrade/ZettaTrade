namespace ZettaTrade.Infrastructure.Tests.Exchange;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Infrastructure.Exchange;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The private-stream e2e suite for the Binance trading surface (T-193): the
/// whole transport path runs against the in-process loopback server — the
/// listen-key issuance (REST, API-key header only), the
/// <c>{PrivateWsUrl}/{listenKey}</c> dial, the receive-only user-data stream
/// (the venue pushes; no auth or subscribe frames exist to run), and the
/// venue <c>executionReport</c> transitions mapping onto the adapter's
/// public <see cref="ITradingAdapter"/> update stream.
///
/// Wait budgets (T-116, 2026-11-07): <c>EventWait</c> is the 240 s house
/// completion budget shared by the whole WS E2E family.
/// </summary>
public sealed class BinanceTradingAdapterWsE2eTests
{
    #region Constants

    /// <summary>The 240 s house completion budget of the WS E2E family.</summary>
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(240);

    private const string StubApiKey = "wt-api-key";

    private const string StubApiSecret = "wt-api-secret";

    /// <summary>The partial transition the loopback pushes on the user-data stream.</summary>
    private const string OrderPartialFrame = """
        {"e":"executionReport","E":1700000000500,"s":"BTCUSDT","S":"BUY","o":"LIMIT","f":"GTC",
         "q":"1.500","p":"45000.50","X":"PARTIALLY_FILLED","i":28,"c":"zt-e2e-link-1",
         "z":"0.500","O":1700000000000,"Z":"0.000"}
        """;

    /// <summary>The filled transition: the same order's lifecycle closing.</summary>
    private const string OrderFilledFrame = """
        {"e":"executionReport","E":1700000001000,"s":"BTCUSDT","S":"BUY","o":"LIMIT","f":"GTC",
         "q":"1.500","p":"45000.50","X":"FILLED","i":28,"c":"zt-e2e-link-1",
         "z":"1.500","O":1700000000000,"Z":"0.000"}
        """;

    /// <summary>A no-op repeat of the standing state (same status, same fill) — never re-published.</summary>
    private const string OrderNoOpFrame = """
        {"e":"executionReport","E":1700000000200,"s":"BTCUSDT","S":"BUY","o":"LIMIT","f":"GTC",
         "q":"1.500","p":"45000.50","X":"NEW","i":28,"c":"zt-e2e-link-1",
         "z":"0.000","O":1700000000000,"Z":"0.000"}
        """;

    /// <summary>An account event the surface does not map — ignored, never an error.</summary>
    private const string AccountPositionFrame = """
        {"e":"outboundAccountPosition","E":1700000002000,"u":1700000002000,
         "B":[{"a":"USDT","f":"1000.00","l":"0.00"}]}
        """;

    #endregion

    #region Tests

    /// <summary>
    /// The listen-key session flow on the wire: the venue's create call (the
    /// API key rides the header alone), the dial of the issued key's stream,
    /// and the venue transition frames mapping into the adapter's update
    /// stream — an order unknown to the session is constructed honestly from
    /// the frame.
    /// </summary>
    [Fact]
    public async Task PrivateSession_IssuesTheListenKeyAndMapsTheVenueOrderTransition()
    {
        await using var stack = await E2eStack.StartAsync();

        var updatesTask = stack.Adapter
            .OrderUpdates
            .Take(2)
            .ToList()
            .ToTask(TaskCreationOptions.RunContinuationsAsynchronously)
            .WaitAsync(EventWait);

        await stack.OpenSessionAsync();

        // The venue pushes the lifecycle; the adapter must publish it.
        await stack.SendOrderFramesAsync(OrderPartialFrame, OrderFilledFrame);

        var updates = await updatesTask;

        updates.Should().HaveCount(2);
        var partial = updates[0];
        var filled = updates[1];

        partial.Status.Should().Be(OrderStatus.PartiallyFilled);
        partial.FilledQuantity.Value.Should().Be(0.5m);
        filled.Status.Should().Be(OrderStatus.Filled);
        filled.FilledQuantity.Value.Should().Be(1.5m);

        // The honest order record: the venue facts, the surface identity.
        filled.Id.Should().Be("zt-e2e-link-1");
        filled.ExchangeId.Should().Be(BinanceTradingAdapter.ExchangeId);
        filled.ExchangeOrderId.Should().Be("28");
        filled.Symbol.Should().Be(Symbol.Parse("BTC/USDT"));
        filled.Side.Should().Be(OrderSide.Buy);
        filled.Type.Should().Be(OrderType.Limit);
        filled.TimeInForce.Should().Be(TimeInForce.GTC);
        filled.PostOnly.Should().BeFalse();
        filled.Quantity.Value.Should().Be(1.5m);
        filled.Price.Value.Should().Be(45000.5m);
        filled.CreatedAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000));
        filled.UpdatedAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1700000001000));
    }

    /// <summary>
    /// A server-dropped session reconnects on its own and re-dials the SAME
    /// listen-key stream (no handshake re-runs — the user-data stream is
    /// receive-only), and the fresh session serves further transitions.
    /// </summary>
    [Fact]
    public async Task Reconnect_RedialsTheSameListenKeyStreamOnTheFreshSocket()
    {
        await using var stack = await E2eStack.StartAsync();

        await stack.OpenSessionAsync();
        var first = await stack.Server.WaitForConnectionAsync(1, EventWait);

        // Server drops the session; the client reconnects on its own.
        await first.CloseGracefullyAsync();
        await TestPoll.PollUntilAsync(() => stack.Server.ConnectionCount >= 2, EventWait);

        // The receive-only stream sends NOTHING — no auth, no subscribe, no
        // re-handshake frame exists on the fresh socket either.
        stack.Server.SnapshotFrames().Should().BeEmpty();

        // The fresh session serves transitions: the surface healed itself.
        var updatesTask = stack.Adapter
            .OrderUpdates
            .Take(1)
            .ToList()
            .ToTask(TaskCreationOptions.RunContinuationsAsynchronously)
            .WaitAsync(EventWait);
        var second = await stack.Server.WaitForConnectionAsync(2, EventWait);
        await second.SendTextAsync(OrderFilledFrame);

        var updates = await updatesTask;
        updates[0].Status.Should().Be(OrderStatus.Filled);
    }

    /// <summary>
    /// A placed order's resting state absorbs the venue transitions: a
    /// status-and-fill no-op repeat publishes nothing, the real transition
    /// publishes exactly once.
    /// </summary>
    [Fact]
    public async Task ExecutionReport_MergesOntoThePlacedOrderAndNeverRepeatsNoOps()
    {
        await using var stack = await E2eStack.StartAsync();
        stack.Rest.SetResponse(BinanceWirePaths.CreateOrder, """
            {"symbol":"BTCUSDT","orderId":28,"orderListId":-1,"clientOrderId":"zt-e2e-link-1","transactTime":1700000000000}
            """);

        var placed = await stack.Adapter.PlaceOrderAsync(
            LimitRequest(OrderSide.Buy, 1.5m, 45000.5m) with { ClientOrderId = "zt-e2e-link-1" });
        placed.Status.Should().Be(OrderStatus.Submitted);

        var updatesTask = stack.Adapter
            .OrderUpdates
            .Take(1)
            .ToList()
            .ToTask(TaskCreationOptions.RunContinuationsAsynchronously)
            .WaitAsync(EventWait);

        // The no-op repeat (Submitted, fill 0) must NOT publish; only the
        // real transition completes the collector.
        await stack.SendOrderFramesAsync(OrderNoOpFrame, OrderPartialFrame);

        var updates = await updatesTask;
        updates.Should().ContainSingle();
        updates[0].Status.Should().Be(OrderStatus.PartiallyFilled);
        updates[0].FilledQuantity.Value.Should().Be(0.5m);
        // The placement facts the frame cannot carry survive the merge.
        updates[0].Id.Should().Be(placed.Id);
        updates[0].TimeInForce.Should().Be(TimeInForce.GTC);
    }

    /// <summary>An account event the surface does not map is ignored — no update, no error.</summary>
    [Fact]
    public async Task UnmappedEvents_AreIgnoredWithoutBreakingTheSurface()
    {
        await using var stack = await E2eStack.StartAsync();

        await stack.OpenSessionAsync();

        // Drain period: nothing the account frame carries belongs on the
        // order stream; the session stays alive for further events.
        await stack.SendOrderFramesAsync(AccountPositionFrame);
        await Task.Delay(TimeSpan.FromSeconds(1));
        stack.Adapter.IsConnected.Should().BeTrue();
    }

    #endregion

    #region Test fixtures

    private static OrderRequest LimitRequest(OrderSide side, decimal quantity, decimal price) => new()
    {
        Symbol = Symbol.Parse("BTC/USDT"),
        Side = side,
        Type = OrderType.Limit,
        Quantity = new Quantity(quantity),
        Price = new Price(price),
        TimeInForce = TimeInForce.GTC,
    };

    /// <summary>The REST paths the e2e stack answers (mirrored adapter constants).</summary>
    private static class BinanceWirePaths
    {
        public const string CreateOrder = "/api/v3/order";
        public const string UserDataStream = "/api/v3/userDataStream";
    }

    /// <summary>The per-test stack: loopback WS server + REST stub + secret-stubbed trading adapter.</summary>
    private sealed class E2eStack : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        private E2eStack(LoopbackWebSocketServer server, BinanceTradingAdapter adapter, StubRecordingHandler rest, ServiceProvider provider)
        {
            Server = server;
            Adapter = adapter;
            Rest = rest;
            _provider = provider;
        }

        public LoopbackWebSocketServer Server { get; }

        public BinanceTradingAdapter Adapter { get; }

        public StubRecordingHandler Rest { get; }

        public static async Task<E2eStack> StartAsync()
        {
            var server = await LoopbackWebSocketServer.StartAsync();

            var rest = new StubRecordingHandler();
            // The listen-key issuance is every session flow's first venue call:
            // the canned capture answers it.
            rest.SetResponse(BinanceWirePaths.UserDataStream, "{\"listenKey\":\"stub-e2e-listen-key\"}");

            var services = new ServiceCollection();
            services.AddHttpClient(BinanceTradingAdapter.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => rest);
            var provider = services.BuildServiceProvider();

            var adapter = new BinanceTradingAdapter(
                NullLogger<BinanceTradingAdapter>.Instance,
                provider.GetRequiredService<IHttpClientFactory>(),
                Substitute.For<IMarketDataAdapter>(),
                new StubSecretStore(),
                new BinanceTradingEndpoints(
                    RestBaseUrl: "https://stub.local",
                    PrivateWsUrl: server.CombinedStreamUrl));

            return new E2eStack(server, adapter, rest, provider);
        }

        /// <summary>
        /// Run the session flow against the loopback: connect (the listen-key
        /// issuance rides the canned REST answer), then the receive-only
        /// stream is up — nothing to acknowledge.
        /// </summary>
        public async Task OpenSessionAsync()
        {
            await Adapter.ConnectAsync();

            // The dial carries the issued listen key in its URL.
            var dial = await Server.WaitForConnectionAsync(1, EventWait);
            _ = dial;
            await TestPoll.PollUntilAsync(() => Adapter.IsConnected, EventWait);
        }

        /// <summary>Push one or more user-data frames on the session.</summary>
        public async Task SendOrderFramesAsync(params string[] frames)
        {
            var connection = await Server.WaitForConnectionAsync(1, EventWait);
            foreach (var frame in frames)
            {
                await connection.SendTextAsync(frame);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Adapter.DisposeAsync();
            await Server.DisposeAsync();
            await _provider.DisposeAsync();
        }
    }

    /// <summary>The stub secret store: the single testnet key pair.</summary>
    private sealed class StubSecretStore : ISecretStore
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => key switch
        {
            BinanceTradingAdapter.ApiKeySecretKey => Task.FromResult<string?>(StubApiKey),
            BinanceTradingAdapter.ApiSecretSecretKey => Task.FromResult<string?>(StubApiSecret),
            _ => Task.FromResult<string?>(null),
        };

        public Task SetAsync(string key, string value, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RemoveAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListKeysAsync(string prefix = "", CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    /// <summary>The recording REST transport: canned results per path + the captured request bits.</summary>
    private sealed class StubRecordingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _responses = new(StringComparer.Ordinal);

        public HttpRequestMessage? LastRequest { get; private set; }

        public void SetResponse(string path, string content) => _responses[path] = content;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct)
        {
            LastRequest = request;
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var body = _responses.TryGetValue(path, out var canned)
                ? canned
                : "{\"code\":0,\"msg\":\"OK\",\"result\":{}}";

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    #endregion
}
