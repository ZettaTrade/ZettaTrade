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
using Microsoft.Extensions.Logging;
using Xunit;

/// <summary>
/// E2e tests for <see cref="OkxAdapter"/> over real sockets: the whole
/// transport path runs against the in-process loopback server — Kestrel
/// handshake, the ONE multiplexed v5 subscribe frame (three channel args),
/// the <c>{"arg","data"}</c> dispatch, the sequenced snapshot/update book
/// state machine, the gap recovery (a gap discards the book and
/// re-subscribes the channels), the reconnect re-subscribe of every tracked
/// symbol, the control-frame surface (<c>event</c> acks and errors never
/// publish data) and the literal ping/pong keepalive texts.
///
/// Wait budgets (T-116, 2026-11-07): <c>EventWait</c> is the 240 s house
/// completion budget shared by the whole WS E2E family — load patience,
/// never a product timing claim (see the Bybit suite's header).
/// </summary>
public sealed class OkxAdapterWsE2eTests
{
    #region Constants

    /// <summary>
    /// The 240 s house load budget (T-116, see the class header): bounded wait
    /// for any expected observable event or connection — completion patience,
    /// never a latency ceiling.
    /// </summary>
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(240);

    /// <summary>The one depth the adapter serves (the venue's books capacity).</summary>
    private const int ServedBookDepth = 400;

    /// <summary>Symbol used in every scenario; its OKX wire form is "BTC-USDT".</summary>
    private static readonly Symbol BtcUsdt = new("btc", "usdt", AssetClass.Crypto);

    // The 24 h change is COMPUTED from the venue's own last/open24h: this
    // frame's pair is chosen to yield an exact 5% (2381/47620 = 0.05).
    /// <summary>A public tickers frame: last, top-of-book, volume and open as STRING numbers.</summary>
    private const string TickerFrame = """
        {"arg":{"channel":"tickers","instId":"BTC-USDT"},"data":[{"instId":"BTC-USDT","last":"50001.0","bidPx":"50000","askPx":"50001","open24h":"47620","vol24h":"123.456","ts":"1699999999000"}]}
        """;

    // (59525 - 47620) / 47620 = 0.25 → an exact 25%.
    private const string TickerSecondFrame = """
        {"arg":{"channel":"tickers","instId":"BTC-USDT"},"data":[{"instId":"BTC-USDT","last":"59525","bidPx":"59500","askPx":"59526","open24h":"47620","vol24h":"223.45","ts":"1699999999500"}]}
        """;

    private static readonly Ticker ExpectedTicker = new(
        BtcUsdt,
        50001.0m,
        50000m,
        50001m,
        123.456m,
        5m,
        DateTimeOffset.FromUnixTimeMilliseconds(1699999999000));

    private static readonly Ticker ExpectedSecondTicker = new(
        BtcUsdt,
        59525m,
        59500m,
        59526m,
        223.45m,
        25m,
        DateTimeOffset.FromUnixTimeMilliseconds(1699999999500));

    private const string BookSnapshotFrame = """
        {"arg":{"channel":"books","instId":"BTC-USDT"},"data":[{"action":"snapshot","asks":[["50000.9","7.5"],["50000.95","0.125"]],"bids":[["49999","12.5"],["49998.5","3.21"]],"checksum":0,"prevSeq":99,"seq":100,"ts":"1700000000000"}]}
        """;

    private const string BookDeltaFrame = """
        {"arg":{"channel":"books","instId":"BTC-USDT"},"data":[{"action":"update","asks":[],"bids":[["49999.5","2"]],"checksum":0,"prevSeq":100,"seq":101,"ts":"1700000000100"}]}
        """;

    /// <summary>A sequence skip: the 102 was never sent — this frame must be rejected.</summary>
    private const string BookGapFrame = """
        {"arg":{"channel":"books","instId":"BTC-USDT"},"data":[{"action":"update","asks":[],"bids":[["49999","1"]],"checksum":0,"prevSeq":102,"seq":103,"ts":"1700000000200"}]}
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
        {"arg":{"channel":"trades","instId":"BTC-USDT"},"data":[{"instId":"BTC-USDT","tradeId":"42","px":"49999.9","sz":"0.5","side":"sell","ts":"1699999999500"},{"instId":"BTC-USDT","tradeId":"43","px":"50000.25","sz":"0.75","side":"buy","ts":"1699999999600"}]}
        """;

    /// <summary>
    /// A public trade frame whose array mixes malformed entries (a string, a
    /// bare number) between two valid ones: the malformed entries are counted
    /// and dropped, the valid ones publish (BUG-027 — they never abort the
    /// frame or throw out of the handler).
    /// </summary>
    private const string TradeFrameWithMalformedEntries = """
        {"arg":{"channel":"trades","instId":"BTC-USDT"},"data":[{"instId":"BTC-USDT","tradeId":"42","px":"49999.9","sz":"0.5","side":"sell","ts":"1699999999500"},"not-an-object",42,{"instId":"BTC-USDT","tradeId":"43","px":"50000.25","sz":"0.75","side":"buy","ts":"1699999999600"}]}
        """;

    /// <summary>A control ack frame (the successful subscribe): it must never publish data.</summary>
    private const string SubscribeAckFrame = """
        {"event":"subscribe","arg":{"channel":"tickers","instId":"BTC-USDT"}}
        """;

    /// <summary>A control error frame: logged loudly, never publishes data.</summary>
    private const string ErrorFrame = """
        {"event":"error","code":"60013","msg":"Invalid arguments","arg":{"channel":"books","instId":"BTC-USDT"}}
        """;

    #endregion

    #region Tests

    [Fact]
    public async Task First_ticker_subscription_connects_subscribes_and_publishes_tickers()
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

        // The control ack carries no data member: it must never reach the feeds.
        await connection.SendTextAsync(SubscribeAckFrame);
        await connection.SendTextAsync(TickerFrame);
        await connection.SendTextAsync(TickerSecondFrame);

        (await tickersTask).Should().Equal(new[] { ExpectedTicker, ExpectedSecondTicker });
        stack.Adapter.IsConnected.Should().BeTrue();
    }

    [Fact]
    public async Task Snapshot_then_update_publishes_the_full_rebuilt_book()
    {
        await using var stack = await E2eStack.StartAsync();
        var server = stack.Server;

        var booksTask = stack.Adapter
            .SubscribeOrderBook(BtcUsdt, ServedBookDepth)
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

        // The book stamp is the frame's local receive moment (T-142's clock
        // discipline), never the exchange's server ts: the frame's ts says
        // 2023-11, the stamp reads "now". The wide tolerance is load
        // patience (the 240 s budget's own reasoning), never a latency claim.
        books[0].Timestamp.Should().BeCloseTo(
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(30),
            "the book stamps the local receive moment, not the exchange ts");

        // The update frame publishes the FULL rebuilt state: the new bid level
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
    public async Task Gap_in_the_book_sequence_discards_the_state_and_resubscribes_the_channels()
    {
        await using var stack = await E2eStack.StartAsync();
        var server = stack.Server;

        var booksTask = stack.Adapter
            .SubscribeOrderBook(BtcUsdt, ServedBookDepth)
            .Take(3)
            .ToList()
            .ToTask(TaskCreationOptions.RunContinuationsAsynchronously)
            .WaitAsync(EventWait);

        var connection = await server.WaitForConnectionAsync(1, EventWait);
        await server.WaitForFrameAsync(f => f.Contains("\"op\":\"subscribe\""), EventWait);

        await connection.SendTextAsync(BookSnapshotFrame);
        await connection.SendTextAsync(BookDeltaFrame);

        // The gap frame (seq=103 after 101) publishes NOTHING: the state is
        // discarded and the channels are re-subscribed for a fresh snapshot.
        await connection.SendTextAsync(BookGapFrame);

        await TestPoll.PollUntilAsync(
            () => server.SnapshotFrames()
                .Count(frame => frame.Contains("\"op\":\"subscribe\"")) >= 2,
            EventWait);

        // The recovery is a fresh snapshot: it rebuilds from seq=200.
        var recoveredSnapshot = """
            {"arg":{"channel":"books","instId":"BTC-USDT"},"data":[{"action":"snapshot","asks":[["50001","1"]],"bids":[["49998","1"]],"checksum":0,"prevSeq":199,"seq":200,"ts":"1700000000300"}]}
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
        tickers[1].PriceChange24hPercent.Should().Be(25m, "the reconnect re-subscribed the channels");
        adapter.IsConnected.Should().BeTrue();
    }

    [Fact]
    public async Task Malformed_trade_entries_are_counted_and_dropped_never_thrown()
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

        await connection.SendTextAsync(TradeFrameWithMalformedEntries);

        // Both VALID entries of the frame publish; the non-object entries in
        // the middle of the array are counted, not thrown (a thrown
        // InvalidOperationException would publish NOTHING here).
        var trades = await tradesTask;
        trades[0].Id.Should().Be("42");
        trades[0].Side.Should().Be(OrderSide.Sell);
        trades[1].Id.Should().Be("43");
        trades[1].Side.Should().Be(OrderSide.Buy);

        await TestPoll.PollUntilAsync(
            () => stack.Logger.Entries.Any(entry =>
                entry.Message.Contains("2 of 4 OKX public trades for BTC/USDT are malformed")),
            EventWait);
    }

    [Fact]
    public async Task A_control_error_frame_is_logged_and_never_publishes_data()
    {
        await using var stack = await E2eStack.StartAsync();
        var server = stack.Server;

        var tickersTask = stack.Adapter
            .SubscribeTicker(BtcUsdt)
            .Take(1)
            .ToList()
            .ToTask(TaskCreationOptions.RunContinuationsAsynchronously)
            .WaitAsync(EventWait);

        var connection = await server.WaitForConnectionAsync(1, EventWait);
        await server.WaitForFrameAsync(f => f.Contains("\"op\":\"subscribe\""), EventWait);

        // The error ack rides the wire BEFORE any data: it must be logged
        // loudly and must never reach the feeds or kill the message path.
        await connection.SendTextAsync(ErrorFrame);
        await connection.SendTextAsync(TickerFrame);

        (await tickersTask)[0].LastPrice.Should().Be(ExpectedTicker.LastPrice);
        await TestPoll.PollUntilAsync(
            () => stack.Logger.Entries.Any(entry =>
                entry.Message.Contains("OKX WebSocket operation error") &&
                entry.Message.Contains("60013")),
            EventWait);
    }

    [Fact]
    public async Task The_server_ping_is_answered_with_the_literal_pong_text()
    {
        await using var stack = await E2eStack.StartAsync();
        var server = stack.Server;

        using var subscription = stack.Adapter
            .SubscribeTicker(BtcUsdt)
            .Subscribe(_ => { });

        var connection = await server.WaitForConnectionAsync(1, EventWait);
        await server.WaitForFrameAsync(f => f.Contains("\"op\":\"subscribe\""), EventWait);

        // The venue's keepalive is the LITERAL "ping" text (not JSON): the
        // adapter answers the literal "pong" on the same wire.
        await connection.SendTextAsync("ping");

        await TestPoll.PollUntilAsync(
            () => server.SnapshotFrames().Any(frame => frame == "pong"),
            EventWait);
        subscription.Dispose();
    }

    [Fact]
    public async Task A_supported_depth_is_honored_on_the_depth_free_channel()
    {
        await using var stack = await E2eStack.StartAsync();
        var server = stack.Server;

        using var subscription = stack.Adapter
            .SubscribeOrderBook(BtcUsdt, ServedBookDepth)
            .Subscribe(_ => { });

        _ = await server.WaitForConnectionAsync(1, EventWait);
        var subscribeFrame = await server.WaitForFrameAsync(
            f => f.Contains("\"op\":\"subscribe\""),
            EventWait);

        // The books channel is depth-free: the requested depth is validated
        // client-side and the wire carries NO depth parameter at all.
        subscribeFrame.Should().Contain("\"channel\":\"books\",\"instId\":\"BTC-USDT\"");
        subscribeFrame.Should().NotContain("400", "the wire never encodes the requested depth");
        subscription.Dispose();
    }

    [Fact]
    public async Task An_unsupported_depth_is_rejected_without_connecting()
    {
        await using var stack = await E2eStack.StartAsync();
        var server = stack.Server;

        var failures = new List<Exception>();
        using var subscription = stack.Adapter
            .SubscribeOrderBook(BtcUsdt, 20)
            .Subscribe(_ => { }, failures.Add);

        await TestPoll.PollUntilAsync(() => failures.Count >= 1, EventWait);

        failures[0].Should().BeOfType<ArgumentOutOfRangeException>();
        failures[0].Message.Should().Contain("400").And.Contain("requested 20");
        server.ConnectionCount.Should().Be(0, "the rejected depth never connects or starts streams");
        subscription.Dispose();
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
            .Select(item => (item.GetProperty("channel").GetString(), item.GetProperty("instId").GetString()))
            .Should().Equal(
                ("tickers", "BTC-USDT"),
                ("books", "BTC-USDT"),
                ("trades", "BTC-USDT"));
    }

    /// <summary>
    /// The adapter, its recording logger, its service provider and the
    /// loopback server disposed in dependency order (adapter first, host
    /// last).
    /// </summary>
    private sealed class E2eStack : IAsyncDisposable
    {
        #region Fields

        private readonly ServiceProvider _provider;

        #endregion

        #region Constructor / Properties

        private E2eStack(LoopbackWebSocketServer server, RecordingLogger logger, ServiceProvider provider, OkxAdapter adapter)
        {
            Server = server;
            Logger = logger;
            _provider = provider;
            Adapter = adapter;
        }

        public LoopbackWebSocketServer Server { get; }

        /// <summary>The recording logger: structured entries for the honest-drop assertions.</summary>
        public RecordingLogger Logger { get; }

        public OkxAdapter Adapter { get; }

        #endregion

        #region Methods: Factory

        public static async Task<E2eStack> StartAsync()
        {
            var server = await LoopbackWebSocketServer.StartAsync();

            var services = new ServiceCollection();
            services.AddHttpClient(OkxAdapter.HttpClientName);
            var provider = services.BuildServiceProvider();

            var logger = new RecordingLogger();
            var adapter = new OkxAdapter(
                logger,
                provider.GetRequiredService<IHttpClientFactory>(),
                new OkxEndpoints(
                    RestBaseUrl: $"http://127.0.0.1:{server.Port}",
                    WsUrl: server.CombinedStreamUrl));

            return new E2eStack(server, logger, provider, adapter);
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

    /// <summary>The recording logger: collects level + formatted message pairs.</summary>
    private sealed class RecordingLogger : ILogger<OkxAdapter>
    {
        /// <summary>Every logged entry so far, in arrival order.</summary>
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        /// <inheritdoc/>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc/>
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc/>
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    #endregion
}
