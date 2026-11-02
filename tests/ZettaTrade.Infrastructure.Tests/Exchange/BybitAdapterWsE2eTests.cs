namespace ZettaTrade.Infrastructure.Tests.Exchange;

using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using Core.Entities;
using Core.Enums;
using Core.ValueObjects;
using FluentAssertions;
using Infrastructure.Exchange;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// E2e tests for <see cref="BybitAdapter"/> over real sockets: the whole
/// transport path runs against the in-process loopback server — Kestrel
/// handshake, the single multiplexed v5 subscribe frame, the
/// <c>{"topic","type","ts","data"}</c> dispatch, the snapshot/delta state
/// machine, the gap recovery (a gap discards the book and re-subscribes the
/// topic) and the reconnect re-subscribe of every tracked symbol.
/// </summary>
public sealed class BybitAdapterWsE2eTests
{
    #region Constants

    /// <summary>Bounded wait for any expected observable event or connection.</summary>
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(10);

    /// <summary>Symbol used in every scenario; its exchange form is "BTCUSDT".</summary>
    private static readonly Symbol BtcUsdt = new("btc", "usdt", AssetClass.Crypto);

    /// <summary>Spot tickers are snapshot-typed and carry NO bid/ask (live-verified).</summary>
    private const string TickerFrame = """
        {"topic":"tickers.BTCUSDT","ts":1699999999000,"type":"snapshot","data":{"symbol":"BTCUSDT","lastPrice":"50000.5","highPrice24h":"51000","lowPrice24h":"49000","prevPrice24h":"49000.24","volume24h":"123.456","turnover24h":"6100000","price24hPcnt":"0.0204"}}
        """;

    private const string TickerSecondFrame = """
        {"topic":"tickers.BTCUSDT","ts":1699999999500,"type":"snapshot","data":{"symbol":"BTCUSDT","lastPrice":"60001","highPrice24h":"61000","lowPrice24h":"59000","prevPrice24h":"58000","volume24h":"223.45","turnover24h":"12300000","price24hPcnt":"0.0345"}}
        """;

    private static readonly Ticker ExpectedTicker = new(
        BtcUsdt,
        50000.5m,
        0m,
        0m,
        123.456m,
        2.04m,
        DateTimeOffset.FromUnixTimeMilliseconds(1699999999000));

    private static readonly Ticker ExpectedSecondTicker = new(
        BtcUsdt,
        60001m,
        0m,
        0m,
        223.45m,
        3.45m,
        DateTimeOffset.FromUnixTimeMilliseconds(1699999999500));

    private const string BookSnapshotFrame = """
        {"topic":"orderbook.50.BTCUSDT","ts":1700000000000,"type":"snapshot","data":{"s":"BTCUSDT","b":[["49999","12.5"],["49998.5","3.21"]],"a":[["50000.9","7.5"],["50000.95","0.125"]],"u":100,"seq":100}}
        """;

    private const string BookDeltaFrame = """
        {"topic":"orderbook.50.BTCUSDT","ts":1700000000100,"type":"delta","data":{"s":"BTCUSDT","b":[["49999.5","2"]],"a":[],"u":101,"seq":101}}
        """;

    /// <summary>A sequence skip: the 102 was never sent — this frame must be rejected.</summary>
    private const string BookGapFrame = """
        {"topic":"orderbook.50.BTCUSDT","ts":1700000000200,"type":"delta","data":{"s":"BTCUSDT","b":[["49999","1"]],"a":[],"u":103,"seq":103}}
        """;

    private static readonly IReadOnlyList<PriceLevel> ExpectedSnapshotBids = new PriceLevel[]
    {
        new(49999m, 12.5m),
        new(49998.5m, 3.21m),
    };

    private static readonly IReadOnlyList<PriceLevel> ExpectedSnapshotAsks = new PriceLevel[]
    {
        new(50000.9m, 7.5m),
        new(50000.95m, 0.125m),
    };

    private const string TradeFrame = """
        {"topic":"publicTrade.BTCUSDT","ts":1699999999500,"type":"snapshot","data":[{"T":1699999999500,"s":"BTCUSDT","S":"Sell","v":"0.5","p":"49999.9","V":"24999.95","i":"42"},{"T":1699999999600,"s":"BTCUSDT","S":"Buy","v":"0.75","p":"50000.25","V":"37500.19","i":"43"}]}
        """;

    /// <summary>The exact topic names the adapter must request per symbol (deterministic order).</summary>
    private static readonly string[] ExpectedTopics =
    [
        "tickers.BTCUSDT",
        "orderbook.50.BTCUSDT",
        "publicTrade.BTCUSDT",
    ];

    /// <summary>A control ack frame (minus the topic) that must never publish data.</summary>
    private const string SubscribeAckFrame = """
        {"success":true,"ret_msg":"subscribe","conn_id":"loopback-1","op":"subscribe"}
        """;

    #endregion

    #region Tests

    [Fact]
    public async Task First_ticker_subscription_connects_subscribes_and_publishes_snapshot_tickers()
    {
        await using var stack = await E2eStack.StartAsync();
        var server = stack.Server;

        // The subscription itself drives the connection; no explicit Connect call.
        var tickersTask = stack.Adapter
            .SubscribeTicker(BtcUsdt)
            .Take(2)
            .ToList()
            .ToTask(TaskCreationOptions.RunContinuationsAsynchronously)
            .WaitAsync(EventWait);

        var connection = await server.WaitForConnectionAsync(1, EventWait);
        var subscribeFrame = await server.WaitForFrameAsync(
            f => f.Contains("\"op\":\"subscribe\""),
            EventWait);
        AssertSubscribeFrame(subscribeFrame);

        // The control ack carries no topic: it must never reach the feeds.
        await connection.SendTextAsync(SubscribeAckFrame);
        await connection.SendTextAsync(TickerFrame);
        await connection.SendTextAsync(TickerSecondFrame);

        (await tickersTask).Should().Equal(new[] { ExpectedTicker, ExpectedSecondTicker });
        stack.Adapter.IsConnected.Should().BeTrue();
    }

    [Fact]
    public async Task Snapshot_then_delta_publishes_the_full_rebuilt_book()
    {
        await using var stack = await E2eStack.StartAsync();
        var server = stack.Server;

        var booksTask = stack.Adapter
            .SubscribeOrderBook(BtcUsdt)
            .Take(2)
            .ToList()
            .ToTask(TaskCreationOptions.RunContinuationsAsynchronously)
            .WaitAsync(EventWait);

        var connection = await server.WaitForConnectionAsync(1, EventWait);
        await server.WaitForFrameAsync(f => f.Contains("\"op\":\"subscribe\""), EventWait);

        await connection.SendTextAsync(BookSnapshotFrame);
        await connection.SendTextAsync(BookDeltaFrame);

        var books = await booksTask;

        // The snapshot frame publishes the shipped levels exactly.
        books[0].Symbol.Should().Be(BtcUsdt);
        books[0].Bids.Should().Equal(ExpectedSnapshotBids);
        books[0].Asks.Should().Equal(ExpectedSnapshotAsks);
        books[0].Timestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000));

        // The delta frame publishes the FULL rebuilt state: the new bid level
        // merged in, the empty ask array a no-op.
        books[1].Bids.Should().Equal(new[]
        {
            new PriceLevel(49999.5m, 2m),
            new PriceLevel(49999m, 12.5m),
            new PriceLevel(49998.5m, 3.21m),
        });
        books[1].Asks.Should().Equal(ExpectedSnapshotAsks);
    }

    [Fact]
    public async Task Gap_in_the_book_sequence_discards_the_state_and_resubscribes_the_topic()
    {
        await using var stack = await E2eStack.StartAsync();
        var server = stack.Server;

        var booksTask = stack.Adapter
            .SubscribeOrderBook(BtcUsdt)
            .Take(3)
            .ToList()
            .ToTask(TaskCreationOptions.RunContinuationsAsynchronously)
            .WaitAsync(EventWait);

        var connection = await server.WaitForConnectionAsync(1, EventWait);
        await server.WaitForFrameAsync(f => f.Contains("\"op\":\"subscribe\""), EventWait);

        await connection.SendTextAsync(BookSnapshotFrame);
        await connection.SendTextAsync(BookDeltaFrame);

        // The gap frame (u=103 after 101) publishes NOTHING: the state is
        // discarded and the topic is re-subscribed for a fresh snapshot.
        await connection.SendTextAsync(BookGapFrame);

        await TestPoll.PollUntilAsync(
            () => server.SnapshotFrames()
                .Count(frame => frame.Contains("\"op\":\"subscribe\"")) >= 2,
            EventWait);

        // The recovery is a REST-of-the-world snapshot: it rebuilds from u=200.
        var recoveredSnapshot = """
            {"topic":"orderbook.50.BTCUSDT","ts":1700000000300,"type":"snapshot","data":{"s":"BTCUSDT","b":[["49998","1"]],"a":[["50001","1"]],"u":200,"seq":200}}
            """;
        await connection.SendTextAsync(recoveredSnapshot);

        var books = await booksTask;
        books[0].Bids.Should().Equal(ExpectedSnapshotBids);
        books[1].Bids.Should().Equal(new[]
        {
            new PriceLevel(49999.5m, 2m),
            new PriceLevel(49999m, 12.5m),
            new PriceLevel(49998.5m, 3.21m),
        });
        books[2].Bids.Should().Equal(new[] { new PriceLevel(49998m, 1m) }, "the recovery rebuilds from the fresh snapshot");
        books[2].Asks.Should().Equal(new[] { new PriceLevel(50001m, 1m) });
    }

    [Fact]
    public async Task Public_trade_frames_publish_the_aggressor_tape()
    {
        await using var stack = await E2eStack.StartAsync();
        var server = stack.Server;

        var tradesTask = stack.Adapter
            .SubscribeTrades(BtcUsdt)
            .Take(2)
            .ToList()
            .ToTask(TaskCreationOptions.RunContinuationsAsynchronously)
            .WaitAsync(EventWait);

        var connection = await server.WaitForConnectionAsync(1, EventWait);
        await server.WaitForFrameAsync(f => f.Contains("\"op\":\"subscribe\""), EventWait);

        await connection.SendTextAsync(TradeFrame);

        var trades = await tradesTask;
        trades[0].Id.Should().Be("42");
        trades[0].Side.Should().Be(OrderSide.Sell);
        trades[0].Price.Value.Should().Be(49999.9m);
        trades[0].Quantity.Value.Should().Be(0.5m);
        trades[0].Timestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1699999999500));

        trades[1].Id.Should().Be("43");
        trades[1].Side.Should().Be(OrderSide.Buy);
        trades[1].Price.Value.Should().Be(50000.25m);
        trades[1].Quantity.Value.Should().Be(0.75m);
        trades[1].Timestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1699999999600));
    }

    [Fact]
    public async Task Reconnection_resubscribes_every_tracked_symbol()
    {
        await using var stack = await E2eStack.StartAsync();
        var server = stack.Server;
        var adapter = stack.Adapter;

        var tickersTask = adapter
            .SubscribeTicker(BtcUsdt)
            .Take(2)
            .ToList()
            .ToTask(TaskCreationOptions.RunContinuationsAsynchronously)
            .WaitAsync(EventWait);

        var first = await server.WaitForConnectionAsync(1, EventWait);
        await server.WaitForFrameAsync(f => f.Contains("\"op\":\"subscribe\""), EventWait);

        await first.SendTextAsync(TickerFrame);

        // Server drops the session; the client reconnects on its own.
        await first.CloseGracefullyAsync();
        await TestPoll.PollUntilAsync(() => server.ConnectionCount >= 2, EventWait);

        // The reconnection must re-subscribe the tracked symbol on the wire.
        await TestPoll.PollUntilAsync(
            () => server.SnapshotFrames()
                .Count(frame => frame.Contains("\"op\":\"subscribe\"")) >= 2,
            EventWait);

        // Feed a ticker on the SECOND session: it must reach the still-live
        // (outer) subscription — the data path survived the reconnect.
        var second = await server.WaitForConnectionAsync(2, EventWait);
        await second.SendTextAsync(TickerSecondFrame);

        var tickers = await tickersTask;
        tickers[0].LastPrice.Should().Be(ExpectedTicker.LastPrice);
        tickers[1].PriceChange24hPercent.Should().Be(3.45m, "the reconnect re-subscribed the topic");
        adapter.IsConnected.Should().BeTrue();
    }

    #endregion

    #region Helpers

    /// <summary>Pins the ONE multiplexed v5 subscribe frame the adapter sends on the first stream start.</summary>
    private static void AssertSubscribeFrame(string frame)
    {
        using var json = JsonDocument.Parse(frame);
        var root = json.RootElement;

        root.GetProperty("op").GetString().Should().Be("subscribe");
        root.GetProperty("args")
            .EnumerateArray()
            .Select(item => item.GetString())
            .Should().Equal(ExpectedTopics);
    }

    /// <summary>
    /// The adapter, its service provider and the loopback server disposed in
    /// dependency order (adapter first, host last).
    /// </summary>
    private sealed class E2eStack : IAsyncDisposable
    {
        #region Fields

        private readonly ServiceProvider _provider;

        #endregion

        #region Constructor / Properties

        private E2eStack(LoopbackWebSocketServer server, ServiceProvider provider, BybitAdapter adapter)
        {
            Server = server;
            _provider = provider;
            Adapter = adapter;
        }

        public LoopbackWebSocketServer Server { get; }

        public BybitAdapter Adapter { get; }

        #endregion

        #region Methods: Factory

        public static async Task<E2eStack> StartAsync()
        {
            var server = await LoopbackWebSocketServer.StartAsync();

            var services = new ServiceCollection();
            services.AddHttpClient(BybitAdapter.HttpClientName);
            var provider = services.BuildServiceProvider();

            var adapter = new BybitAdapter(
                NullLogger<BybitAdapter>.Instance,
                provider.GetRequiredService<IHttpClientFactory>(),
                new BybitEndpoints(
                    RestBaseUrl: $"http://127.0.0.1:{server.Port}",
                    WsUrl: server.CombinedStreamUrl));

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
