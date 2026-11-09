namespace ZettaTrade.Infrastructure.Tests.Exchange;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Core.Entities;
using Core.Enums;
using Core.Interfaces;
using Core.ValueObjects;
using FluentAssertions;
using Infrastructure.Exchange;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

/// <summary>
/// The private-stream e2e suite for the Bybit trading surface (T-127): the
/// whole transport path runs against the in-process loopback server — the
/// connect-time v5 auth handshake (with its signature mirror asserted on the
/// wire), the <c>order.spot</c> subscribe, the venue order-transition frames
/// mapping onto the adapter's public <see cref="ITradingAdapter"/> update
/// stream and the reconnect re-auth (a fresh socket re-runs the auth+subscribe
/// session flow before any order data flows again).
///
/// Wait budgets (T-116, 2026-11-07): <c>EventWait</c> is the 240 s house
/// completion budget shared by the whole WS E2E family.
/// </summary>
public sealed class BybitTradingAdapterWsE2eTests
{
    #region Constants

    /// <summary>The 240 s house completion budget of the WS E2E family.</summary>
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(240);

    /// <summary>The loopback session's auth-ack window (the adapter's own bound is 10 s).</summary>
    private static readonly TimeSpan SessionAckWait = TimeSpan.FromSeconds(30);

    private const string StubApiKey = "wt-api-key";

    private const string StubApiSecret = "wt-api-secret";

    /// <summary>The filled transition the loopback pushes on the order topic.</summary>
    private const string OrderPartialFrame = """
        {"topic":"order.spot","ts":1700000000500,"type":"delta","data":[
          {"orderId":"v-e2e-1","orderLinkId":"zt-e2e-link-1","symbol":"BTCUSDT","side":"Buy",
           "orderStatus":"PartiallyFilled","orderType":"Limit","timeInForce":"GTC","qty":"1.5",
           "price":"45000.5","cumExecQty":"0.5","createdTime":"1700000000000","updatedTime":"1700000000500"}
        ]}
        """;

    private const string OrderFilledFrame = """
        {"topic":"order.spot","ts":1700000001000,"type":"snapshot","data":[
          {"orderId":"v-e2e-1","orderLinkId":"zt-e2e-link-1","symbol":"BTCUSDT","side":"Buy",
           "orderStatus":"Filled","orderType":"Limit","timeInForce":"GTC","qty":"1.5",
           "price":"45000.5","cumExecQty":"1.5","createdTime":"1700000000000","updatedTime":"1700000001000"}
        ]}
        """;

    #endregion

    #region Tests

    /// <summary>
    /// The connect-time session flow on the wire: the auth frame (the key, the
    /// expiry and the HMAC signature over <c>GET/realtime{expires}</c>), the
    /// subscribe frame listing the order topic, and the venue transition
    /// frames mapping into the adapter's update stream.
    /// </summary>
    [Fact]
    public async Task PrivateSession_AuthsSubscribesAndMapsTheVenueOrderTransition()
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
        filled.ExchangeId.Should().Be(BybitTradingAdapter.ExchangeId);
        filled.ExchangeOrderId.Should().Be("v-e2e-1");
        filled.Symbol.Should().Be(Symbol.Parse("BTC/USDT"));
        filled.Side.Should().Be(OrderSide.Buy);
        filled.Type.Should().Be(OrderType.Limit);
        filled.TimeInForce.Should().Be(TimeInForce.GTC);
        filled.Quantity.Value.Should().Be(1.5m);
        filled.Price.Value.Should().Be(45000.5m);
        filled.CreatedAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000));
        filled.UpdatedAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1700000001000));
    }

    /// <summary>
    /// A server-dropped session reconnects and the surface re-runs the whole
    /// auth+subscribe flow before any further order data flows — a silent
    /// reconnect would keep the socket live while no order updates arrive.
    /// </summary>
    [Fact]
    public async Task Reconnect_RerunsTheAuthAndSubscribeFlowOnTheFreshSocket()
    {
        await using var stack = await E2eStack.StartAsync();

        await stack.OpenSessionAsync();
        var first = await stack.Server.WaitForConnectionAsync(1, EventWait);

        // Server drops the session; the client reconnects on its own.
        await first.CloseGracefullyAsync();
        await TestPoll.PollUntilAsync(() => stack.Server.ConnectionCount >= 2, EventWait);

        // The fresh socket must re-run the AUTH handshake on the wire.
        await TestPoll.PollUntilAsync(
            () => stack.Server.SnapshotFrames().Count(frame => frame.Contains("\"op\":\"auth\"")) >= 2,
            EventWait);
        await stack.AcknowledgeSessionAsync();

        // The re-auth must be followed by the order topic re-subscribe.
        await TestPoll.PollUntilAsync(
            () => stack.Server.SnapshotFrames().Count(frame => frame.Contains("\"op\":\"subscribe\"")) >= 2,
            EventWait);
    }

    #endregion

    #region Test fixtures

    /// <summary>The per-test stack: loopback WS server + secret-stubbed trading adapter.</summary>
    private sealed class E2eStack : IAsyncDisposable
    {
        private static readonly TimeSpan SignedHandshakeCheck = TimeSpan.FromSeconds(1);

        private readonly ServiceProvider _provider;

        private E2eStack(LoopbackWebSocketServer server, BybitTradingAdapter adapter, ServiceProvider provider)
        {
            Server = server;
            Adapter = adapter;
            _provider = provider;
        }

        public LoopbackWebSocketServer Server { get; }

        public BybitTradingAdapter Adapter { get; }

        public static async Task<E2eStack> StartAsync()
        {
            var server = await LoopbackWebSocketServer.StartAsync();

            var services = new ServiceCollection();
            services.AddHttpClient(BybitTradingAdapter.HttpClientName);
            var provider = services.BuildServiceProvider();

            var adapter = new BybitTradingAdapter(
                NullLogger<BybitTradingAdapter>.Instance,
                provider.GetRequiredService<IHttpClientFactory>(),
                Substitute.For<IMarketDataAdapter>(),
                new StubSecretStore(),
                new BybitTradingEndpoints(
                    RestBaseUrl: "https://stub.local",
                    PrivateWsUrl: server.CombinedStreamUrl));

            return new E2eStack(server, adapter, provider);
        }

        /// <summary>
        /// Run the first session flow against the loopback: connect, observe
        /// the auth frame on the wire (the signature mirror asserted), ack it,
        /// observe the subscribe frame and ack it.
        /// </summary>
        public async Task OpenSessionAsync()
        {
            await Adapter.ConnectAsync();

            var authFrame = await Server.WaitForFrameAsync(
                frame => frame.Contains("\"op\":\"auth\""),
                SessionAckWait);
            AssertAuthHandshake(authFrame);

            var connection = await Server.WaitForConnectionAsync(1, SessionAckWait);
            await connection.SendTextAsync("{\"success\":true,\"op\":\"auth\"}");

            var subscribeFrame = await Server.WaitForFrameAsync(
                frame => frame.Contains("\"op\":\"subscribe\""),
                SessionAckWait);
            subscribeFrame.Should().Contain("order.spot");

            await connection.SendTextAsync("{\"success\":true,\"op\":\"subscribe\",\"args\":[\"order.spot\"]}");
            await TestPoll.PollUntilAsync(() => Adapter.IsConnected, SignedHandshakeCheck);
        }

        /// <summary>Push one or more order-topic frames on the session.</summary>
        public async Task SendOrderFramesAsync(params string[] frames)
        {
            var connection = await Server.WaitForConnectionAsync(1, SessionAckWait);
            foreach (var frame in frames)
            {
                await connection.SendTextAsync(frame);
            }
        }

        /// <summary>
        /// Acknowledge the currently-open session flow's auth + subscribe
        /// (the reconnect-path variant: the frames are already on the wire).
        /// </summary>
        public async Task AcknowledgeSessionAsync()
        {
            var connection = await Server.WaitForConnectionAsync(2, SessionAckWait);
            await connection.SendTextAsync("{\"success\":true,\"op\":\"auth\"}");
            await connection.SendTextAsync("{\"success\":true,\"op\":\"subscribe\",\"args\":[\"order.spot\"]}");
        }

        private static void AssertAuthHandshake(string authFrame)
        {
            using var json = JsonDocument.Parse(authFrame);
            var root = json.RootElement;
            root.GetProperty("op").GetString().Should().Be("auth");

            var args = root.GetProperty("args");
            args.GetArrayLength().Should().Be(3);
            args[0].GetString().Should().Be(StubApiKey);

            var expires = args[1].GetInt64();
            var signature = args[2].GetString();

            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(StubApiSecret));
            var expected = Convert.ToHexString(
                    hmac.ComputeHash(Encoding.UTF8.GetBytes($"GET/realtime{expires}")))
                .ToLowerInvariant();
            signature.Should().Be(expected);
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
            BybitTradingAdapter.ApiKeySecretKey => Task.FromResult<string?>(StubApiKey),
            BybitTradingAdapter.ApiSecretSecretKey => Task.FromResult<string?>(StubApiSecret),
            _ => Task.FromResult<string?>(null),
        };

        public Task SetAsync(string key, string value, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RemoveAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListKeysAsync(string prefix = "", CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    #endregion
}
