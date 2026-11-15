namespace ZettaTrade.Infrastructure.Tests.Exchange;

using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// E2e tests for the Binance adapter's liquidation lane (T-242) over real
/// sockets: the whole transport path runs against the in-process loopback
/// server — the dedicated futures socket's on-demand dial, the all-market
/// <c>!forceOrder@arr</c> SUBSCRIBE frame, the combined-envelope dispatch,
/// the executed-facts preference (<c>ap</c>/<c>z</c> over <c>p</c>/<c>q</c>),
/// the untracked contract's drop, the ref-counted release (an UNSUBSCRIBE
/// and the socket drop when the last subscriber leaves) and the reconnect
/// re-subscribe of the all-market stream.
///
/// Wait budgets: <c>EventWait</c> is the 240 s house completion budget
/// shared by the whole WS E2E family (T-116) — load patience, never a
/// product timing claim.
/// </summary>
/// <remarks>
/// The reconnect delay stays with the production 5 s inside the adapter
/// (<c>WebSocketClient</c>'s own default); pacing scenarios live in
/// <see cref="WebSocketClientTests"/>, where the client is built directly.
/// </remarks>
public sealed class BinanceAdapterLiquidationE2eTests
{
    #region Constants

    /// <summary>
    /// The 240 s house load budget (T-116, see the class header): bounded wait
    /// for any expected observable event or connection — completion patience,
    /// never a latency ceiling.
    /// </summary>
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(240);

    /// <summary>Spot symbol used throughout; its exchange form is "BTCUSDT".</summary>
    private static readonly Symbol BtcUsdt = new("btc", "usdt", AssetClass.Crypto);

    /// <summary>The settle-marked perpetual; its exchange form is ALSO "BTCUSDT" — one contract, one wire text.</summary>
    private static readonly Symbol BtcUsdtPerp = new("btc", "usdt", AssetClass.Crypto, "usdt");

    /// <summary>The all-market forced-liquidation stream name.</summary>
    private const string LiquidationStreamName = "!forceOrder@arr";

    /// <summary>
    /// The measured live wire (2026-11-16): the combined envelope, the
    /// forced-order object under <c>o</c> — the closing ORDER's side
    /// (<c>S:SELL</c> = a LONG position force-closed), the original order
    /// facts (<c>q</c>/<c>p</c>) and the executed facts (<c>z</c>/<c>ap</c>).
    /// </summary>
    private const string ForceOrderFrame = """
        {"stream":"!forceOrder@arr","data":{"e":"forceOrder","E":1699999999000,"o":{"s":"BTCUSDT","S":"SELL","o":"LIMIT","f":"IOC","q":"0.014","p":"9910","X":"FILLED","ap":"9910.5","l":"0.011","z":"0.011","T":1699999999000}}}
        """;

    /// <summary>A BUY close: the SHORT position was force-closed.</summary>
    private const string ForceOrderBuyFrame = """
        {"stream":"!forceOrder@arr","data":{"e":"forceOrder","E":1699999999500,"o":{"s":"BTCUSDT","S":"BUY","o":"LIMIT","f":"IOC","q":"0.5","p":"51000","X":"FILLED","ap":"51001","l":"0.5","z":"0.5","T":1699999999500}}}
        """;

    /// <summary>An unfilled force: the average price and accumulation are ZEROS — the original order facts must carry the event.</summary>
    private const string ForceOrderUnfilledFrame = """
        {"stream":"!forceOrder@arr","data":{"e":"forceOrder","E":1699999999600,"o":{"s":"BTCUSDT","S":"SELL","o":"LIMIT","f":"IOC","q":"0.5","p":"49000","X":"NEW","ap":"0","l":"0","z":"0","T":1699999999600}}}
        """;

    /// <summary>A forced order of a contract the lane never subscribed.</summary>
    private const string ForceOrderUntrackedFrame = """
        {"stream":"!forceOrder@arr","data":{"e":"forceOrder","E":1699999999700,"o":{"s":"ETHUSDT","S":"SELL","q":"1","p":"1800","ap":"1800","z":"1","T":1699999999700}}}
        """;

    /// <summary>A forced order missing the side member: malformed, dropped — never a defaulted side.</summary>
    private const string ForceOrderMalformedFrame = """
        {"stream":"!forceOrder@arr","data":{"e":"forceOrder","E":1699999999800,"o":{"s":"BTCUSDT","q":"0.5","p":"49000","T":1699999999800}}}
        """;

    /// <summary>The WS API's subscribe echo (a control frame — no stream member).</summary>
    private const string SubscribeAckFrame = """
        {"result":null,"id":1}
        """;

    #endregion

    #region Tests

    [Fact]
    public async Task First_liquidation_subscription_dials_the_futures_socket_and_publishes_events()
    {
        await using var stack = await E2eStack.StartAsync();
        var server = stack.Server;

        var eventsTask = stack.Adapter
            .SubscribeLiquidations(BtcUsdt)
            .Take(1)
            .ToList()
            .ToTask(TaskCreationOptions.RunContinuationsAsynchronously)
            .WaitAsync(EventWait);

        // The subscription itself drives the dedicated socket's dial; no
        // explicit Connect call, and no market-data subscription anywhere.
        var connection = await server.WaitForConnectionAsync(1, EventWait);
        var subscribeFrame = await server.WaitForFrameAsync(
            f => f.Contains("\"method\":\"SUBSCRIBE\"") && f.Contains(LiquidationStreamName),
            EventWait);
        subscribeFrame.Should().Contain("\"params\":[\"" + LiquidationStreamName + "\"]", "the all-market stream is ONE subscription, not per-contract");

        // The control echo carries no stream member: it must never reach the feeds.
        await connection.SendTextAsync(SubscribeAckFrame);
        await connection.SendTextAsync(ForceOrderFrame);

        var events = await eventsTask;
        events[0].Symbol.Should().Be(BtcUsdt);
        events[0].LiquidatedSide.Should().Be(LiquidationSide.Long, "S:SELL force-closed a LONG position");
        events[0].Price.Value.Should().Be(9910.5m, "the average forced fill (ap) is the served price, over the bankruptcy price");
        events[0].Quantity.Value.Should().Be(0.011m, "the accumulated fill (z) is the served size, over the original quantity");
        events[0].Timestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1699999999000));
        stack.Adapter.IsConnected.Should().BeTrue("the dedicated socket's state aggregates into the adapter's");
    }

    [Fact]
    public async Task A_Buy_close_names_the_force_closed_SHORT()
    {
        await using var stack = await E2eStack.StartAsync();
        var server = stack.Server;

        var eventsTask = stack.Adapter
            .SubscribeLiquidations(BtcUsdt)
            .Take(1)
            .ToList()
            .ToTask(TaskCreationOptions.RunContinuationsAsynchronously)
            .WaitAsync(EventWait);

        var connection = await server.WaitForConnectionAsync(1, EventWait);
        await server.WaitForFrameAsync(f => f.Contains(LiquidationStreamName), EventWait);

        await connection.SendTextAsync(ForceOrderBuyFrame);

        var events = await eventsTask;
        events[0].LiquidatedSide.Should().Be(LiquidationSide.Short, "S:BUY force-closed a SHORT position");
        events[0].Price.Value.Should().Be(51001m);
        events[0].Quantity.Value.Should().Be(0.5m);
    }

    [Fact]
    public async Task An_unfilled_force_carries_the_original_order_facts()
    {
        await using var stack = await E2eStack.StartAsync();
        var server = stack.Server;

        var eventsTask = stack.Adapter
            .SubscribeLiquidations(BtcUsdt)
            .Take(1)
            .ToList()
            .ToTask(TaskCreationOptions.RunContinuationsAsynchronously)
            .WaitAsync(EventWait);

        var connection = await server.WaitForConnectionAsync(1, EventWait);
        await server.WaitForFrameAsync(f => f.Contains(LiquidationStreamName), EventWait);

        await connection.SendTextAsync(ForceOrderUnfilledFrame);

        var events = await eventsTask;
        events[0].Price.Value.Should().Be(49000m, "a zero average price falls back to the bankruptcy price");
        events[0].Quantity.Value.Should().Be(0.5m, "a zero accumulation falls back to the original quantity");
    }

    [Fact]
    public async Task Spot_and_perp_forms_share_one_subscription_and_each_sees_its_own_stamp()
    {
        await using var stack = await E2eStack.StartAsync();
        var server = stack.Server;

        var spotTask = stack.Adapter
            .SubscribeLiquidations(BtcUsdt)
            .Take(1)
            .ToList()
            .ToTask(TaskCreationOptions.RunContinuationsAsynchronously)
            .WaitAsync(EventWait);
        var perpTask = stack.Adapter
            .SubscribeLiquidations(BtcUsdtPerp)
            .Take(1)
            .ToList()
            .ToTask(TaskCreationOptions.RunContinuationsAsynchronously)
            .WaitAsync(EventWait);

        var connection = await server.WaitForConnectionAsync(1, EventWait);
        await server.WaitForFrameAsync(f => f.Contains(LiquidationStreamName), EventWait);

        await connection.SendTextAsync(ForceOrderFrame);

        var spot = (await spotTask)[0];
        var perp = (await perpTask)[0];

        spot.Symbol.Should().Be(BtcUsdt, "each subscriber sees the event stamped with ITS app symbol");
        perp.Symbol.Should().Be(BtcUsdtPerp, "the settle-marked form names the same contract");
        spot.Price.Value.Should().Be(perp.Price.Value, "both subscribers read the same wire event's facts");

        // Both tasks completing means both starts completed: any erroneous
        // second SUBSCRIBE would already be in the server's frames.
        server.SnapshotFrames()
            .Count(frame => frame.Contains("\"method\":\"SUBSCRIBE\"") && frame.Contains(LiquidationStreamName))
            .Should().Be(1, "one wire text serves both app forms — one all-market subscription, ref-counted");
    }

    [Fact]
    public async Task Untracked_and_malformed_contracts_drop_never_publish()
    {
        await using var stack = await E2eStack.StartAsync();
        var server = stack.Server;

        var eventsTask = stack.Adapter
            .SubscribeLiquidations(BtcUsdt)
            .Take(1)
            .ToList()
            .ToTask(TaskCreationOptions.RunContinuationsAsynchronously)
            .WaitAsync(EventWait);

        var connection = await server.WaitForConnectionAsync(1, EventWait);
        await server.WaitForFrameAsync(f => f.Contains(LiquidationStreamName), EventWait);

        // The untracked contract's event and the malformed one (no side)
        // must BOTH drop: the Take(1) task can only complete on the valid
        // event — either leak would have completed it with the wrong fact.
        await connection.SendTextAsync(ForceOrderUntrackedFrame);
        await connection.SendTextAsync(ForceOrderMalformedFrame);
        await connection.SendTextAsync(ForceOrderFrame);

        var events = await eventsTask;
        events[0].Symbol.Should().Be(BtcUsdt);
        events[0].Price.Value.Should().Be(9910.5m, "the FIRST published event is the valid one, never the dropped ones");
    }

    [Fact]
    public async Task Releasing_the_last_subscription_unsubscribes_and_drops_the_socket()
    {
        await using var stack = await E2eStack.StartAsync();
        var server = stack.Server;

        var subscription = stack.Adapter
            .SubscribeLiquidations(BtcUsdt)
            .Subscribe(_ => { });

        var connection = await server.WaitForConnectionAsync(1, EventWait);
        await server.WaitForFrameAsync(f => f.Contains("\"method\":\"SUBSCRIBE\""), EventWait);

        subscription.Dispose();
        await TestPoll.PollUntilAsync(
            () => server.SnapshotFrames().Any(frame => frame.Contains("\"method\":\"UNSUBSCRIBE\"")),
            EventWait);

        await TestPoll.PollUntilAsync(() => connection.IsClosed, EventWait);
        await TestPoll.PollUntilAsync(() => !stack.Adapter.IsConnected, EventWait);
    }

    [Fact]
    public async Task Reconnection_resubscribes_the_all_market_stream()
    {
        await using var stack = await E2eStack.StartAsync();
        var server = stack.Server;
        var adapter = stack.Adapter;

        var eventsTask = adapter
            .SubscribeLiquidations(BtcUsdt)
            .Take(2)
            .ToList()
            .ToTask(TaskCreationOptions.RunContinuationsAsynchronously)
            .WaitAsync(EventWait);

        var first = await server.WaitForConnectionAsync(1, EventWait);
        await server.WaitForFrameAsync(f => f.Contains(LiquidationStreamName), EventWait);

        await first.SendTextAsync(ForceOrderFrame);

        // Server drops the session; the client reconnects on its own.
        await first.CloseGracefullyAsync();
        await TestPoll.PollUntilAsync(() => server.ConnectionCount >= 2, EventWait);

        // The reconnection must re-subscribe the all-market stream on the wire.
        await TestPoll.PollUntilAsync(
            () => server.SnapshotFrames()
                .Count(frame => frame.Contains("\"method\":\"SUBSCRIBE\"") && frame.Contains(LiquidationStreamName)) >= 2,
            EventWait);

        // Feed a forced order on the SECOND session: it must reach the
        // still-live (outer) subscription — the data path survived.
        var second = await server.WaitForConnectionAsync(2, EventWait);
        await second.SendTextAsync(ForceOrderBuyFrame);

        var events = await eventsTask;
        events[0].LiquidatedSide.Should().Be(LiquidationSide.Long);
        events[1].LiquidatedSide.Should().Be(LiquidationSide.Short, "the reconnect re-subscribed the stream");
        adapter.IsConnected.Should().BeTrue();
    }

    #endregion

    #region Helpers

    /// <summary>
    /// The adapter, its service provider and the loopback server disposed in
    /// dependency order (adapter first, host last). The futures socket shares
    /// the loopback host BY CONFIGURATION: a blank FuturesWsUrl would derive
    /// the shipped PRODUCTION futures host and dial out.
    /// </summary>
    private sealed class E2eStack : IAsyncDisposable
    {
        #region Fields

        private readonly ServiceProvider _provider;

        #endregion

        #region Constructor / Properties

        private E2eStack(LoopbackWebSocketServer server, ServiceProvider provider, BinanceAdapter adapter)
        {
            Server = server;
            _provider = provider;
            Adapter = adapter;
        }

        public LoopbackWebSocketServer Server { get; }

        public BinanceAdapter Adapter { get; }

        #endregion

        #region Methods: Factory

        public static async Task<E2eStack> StartAsync()
        {
            var server = await LoopbackWebSocketServer.StartAsync();

            var services = new ServiceCollection();
            services.AddHttpClient(BinanceAdapter.HttpClientName);
            var provider = services.BuildServiceProvider();

            var adapter = new BinanceAdapter(
                NullLogger<BinanceAdapter>.Instance,
                provider.GetRequiredService<IHttpClientFactory>(),
                new BinanceEndpoints(
                    RestBaseUrl: $"http://127.0.0.1:{server.Port}",
                    WsUrl: server.CombinedStreamUrl,
                    FuturesWsUrl: server.CombinedStreamUrl));

            return new E2eStack(server, provider, adapter);
        }

        #endregion

        #region IAsyncDisposable

        public async ValueTask DisposeAsync()
        {
            await Adapter.DisposeAsync();
            await Server.DisposeAsync();
            _provider.Dispose();
        }

        #endregion
    }

    #endregion
}
