namespace ZettaTrade.Infrastructure.Tests.Exchange;

using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// E2e tests for <see cref="BinanceAdapter"/> over real sockets: the whole
/// transport path runs against the in-process loopback server — Kestrel
/// handshake, the adapter's SUBSCRIBE frame, combined-envelope responses and
/// the published domain entities. This is the pinned regression for the T-013
/// combined-envelope framing bug (the one only a live probe used to catch).
///
/// Wait budgets (T-116, 2026-11-07): <c>EventWait</c> is the 60 s house
/// completion budget shared by the whole WS E2E family, raised from 10 s.
/// The old cap tripped under full-solution parallel test load (xUnit hosts
/// across sibling assemblies chewing cores): a scheduler stall starved the
/// loopback handshake and frame beats past the cap and
/// <see cref="Depth_and_trade_frames_publish_order_book_and_trades"/> died
/// as a bare System.TimeoutException inside WaitAsync(EventWait) at the 10 s
/// mark, while every focused run stayed green — the adapter pipeline was
/// healthy all along. This is load patience, never a product timing claim:
/// solo runs always passed and still must, and deliberate latency ceilings
/// (where any ever appear) stay separate from this budget.
/// </summary>
/// <remarks>
/// The reconnect delay stays with the production 5 s inside the adapter
/// (<c>WebSocketClient</c>'s own default); pacing scenarios live in
/// <see cref="WebSocketClientTests"/>, where the client is built directly.
/// </remarks>
public sealed class BinanceAdapterWsE2eTests
{
    #region Constants

    /// <summary>
    /// The 60 s house load budget (T-116, see the class header): bounded wait
    /// for any expected observable event or connection — completion patience,
    /// never a latency ceiling.
    /// </summary>
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(60);

    /// <summary>Symbol used in every scenario; its lowercase exchange form is "btcusdt".</summary>
    private static readonly Symbol BtcUsdt = new("btc", "usdt", AssetClass.Crypto);

    /// <summary>Ticker payload with number-strings (Binance's canonical encoding).</summary>
    private const string TickerStringFrame = """
        {"stream":"btcusdt@ticker","data":{"E":1699999999000,"c":"50000.5","b":"49999","a":"50000.9","v":"123.456","P":"2.5"}}
        """;

    /// <summary>The same ticker with bare numbers — both encodings must parse identically.</summary>
    private const string TickerNumericFrame = """
        {"stream":"btcusdt@ticker","data":{"E":1699999999000,"c":50000.5,"b":49999,"a":50000.9,"v":123.456,"P":2.5}}
        """;

    private const string DepthStringFrame = """
        {"stream":"btcusdt@depth20@100ms","data":{"lastUpdateId":123,"bids":[["49999","12.5"],["49999.5","3.21"]],"asks":[["50000.9","7.5"],["50000.95","0.125"]]}}
        """;

    private const string DepthNumericFrame = """
        {"stream":"btcusdt@depth20@100ms","data":{"lastUpdateId":123,"bids":[[49999,12.5],[49999.5,3.21]],"asks":[[50000.9,7.5],[50000.95,0.125]]}}
        """;

    /// <summary>"m" is "is buyer the maker": a maker buy means the aggressor sold.</summary>
    private const string TradeSellStringFrame = """
        {"stream":"btcusdt@trade","data":{"t":42,"m":true,"p":"49999.9","q":"0.5","T":1699999999500}}
        """;

    private const string TradeBuyNumericFrame = """
        {"stream":"btcusdt@trade","data":{"t":43,"m":false,"p":50000.25,"q":0.75,"T":1699999999600}}
        """;

    /// <summary>A bare ticker payload — no envelope, the raw /ws host trap from T-013.</summary>
    private const string BareTickerPayloadFrame = """
        {"E":1699999999000,"c":"50000.5","b":"49999","a":"50000.9","v":"123.456","P":"2.5"}
        """;

    /// <summary>Half an envelope: the "data" member missing entirely.</summary>
    private const string EnvelopeWithoutData = """
        {"stream":"btcusdt@ticker"}
        """;

    /// <summary>Half an envelope: the "stream" member missing entirely.</summary>
    private const string EnvelopeWithoutStream = """
        {"data":{"E":1,"c":"2"}}
        """;

    /// <summary>A second valid ticker with distinct values (tail probe against silent drops).</summary>
    private const string TickerSecondStringFrame = """
        {"stream":"btcusdt@ticker","data":{"E":1699999999500,"c":"60001","b":"60000","a":"60001.5","v":"223.45","P":"3.5"}}
        """;

    private static readonly Ticker ExpectedTickerOne = new(
        BtcUsdt,
        50000.5m,
        49999m,
        50000.9m,
        123.456m,
        2.5m,
        DateTimeOffset.FromUnixTimeMilliseconds(1699999999000));

    private static readonly Ticker ExpectedTickerSecond = new(
        BtcUsdt,
        60001m,
        60000m,
        60001.5m,
        223.45m,
        3.5m,
        DateTimeOffset.FromUnixTimeMilliseconds(1699999999500));

    private static readonly IReadOnlyList<PriceLevel> ExpectedBids = new PriceLevel[]
    {
        new(49999m, 12.5m),
        new(49999.5m, 3.21m),
    };

    private static readonly IReadOnlyList<PriceLevel> ExpectedAsks = new PriceLevel[]
    {
        new(50000.9m, 7.5m),
        new(50000.95m, 0.125m),
    };

    /// <summary>The exact subscription names the adapter must request per symbol (deterministic order).</summary>
    private static readonly string[] ExpectedStreamParams =
    [
        "btcusdt@ticker",
        "btcusdt@depth20@100ms",
        "btcusdt@trade",
    ];

    #endregion

    #region Tests

    [Fact]
    public async Task First_ticker_subscription_connects_subscribes_and_publishes_parsed_tickers()
    {
        await using var stack = await E2eStack.StartAsync();
        var server = stack.Server;

        // The subscription itself drives the connection; no explicit Connect call.
        var tickersTask = CollectTickerAsync(stack.Adapter);

        var connection = await server.WaitForConnectionAsync(1, EventWait);
        var subscribeFrame = await server.WaitForFrameAsync(f => f.Contains("\"SUBSCRIBE\""), EventWait);
        AssertSubscribeFrame(subscribeFrame, requestId: 1);

        // Two encodings, two pushes: string first, bare numbers second.
        await connection.SendTextAsync(TickerStringFrame);
        await connection.SendTextAsync(TickerNumericFrame);

        var tickers = await tickersTask;
        tickers.Should().Equal(new[] { ExpectedTickerOne, ExpectedTickerOne });

        stack.Adapter.IsConnected.Should().BeTrue();
    }

    [Fact]
    public async Task Depth_and_trade_frames_publish_order_book_and_trades()
    {
        await using var stack = await E2eStack.StartAsync();
        var server = stack.Server;

        // Two feeds: the second subscription re-runs the (idempotent) stream start.
        var booksTask = stack.Adapter
            .SubscribeOrderBook(BtcUsdt)
            .Take(2)
            .ToList()
            .ToTask(TaskCreationOptions.RunContinuationsAsynchronously)
            .WaitAsync(EventWait);
        var tradesTask = stack.Adapter
            .SubscribeTrades(BtcUsdt)
            .Take(2)
            .ToList()
            .ToTask(TaskCreationOptions.RunContinuationsAsynchronously)
            .WaitAsync(EventWait);

        var connection = await server.WaitForConnectionAsync(1, EventWait);
        await server.WaitForFrameAsync(f => f.Contains("\"SUBSCRIBE\""), EventWait);

        await connection.SendTextAsync(DepthStringFrame);
        await connection.SendTextAsync(DepthNumericFrame);
        await connection.SendTextAsync(TradeSellStringFrame);
        await connection.SendTextAsync(TradeBuyNumericFrame);

        var books = await booksTask;
        books[0].Symbol.Should().Be(BtcUsdt);
        books[0].Bids.Should().Equal(ExpectedBids);
        books[0].Asks.Should().Equal(ExpectedAsks);

        // The numeric encoding must parse identically to the string one.
        books[1].Bids.Should().Equal(ExpectedBids);
        books[1].Asks.Should().Equal(ExpectedAsks);

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
    public async Task Frames_without_the_combined_envelope_are_dropped()
    {
        await using var stack = await E2eStack.StartAsync();
        var server = stack.Server;

        // Two collectors catch the valid tail: a silent leak of bare frames
        // would surface here as a wrong first entry instead of going unnoticed.
        var tailTickersTask = stack.Adapter
            .SubscribeTicker(BtcUsdt)
            .Take(2)
            .ToList()
            .ToTask(TaskCreationOptions.RunContinuationsAsynchronously)
            .WaitAsync(EventWait);

        var connection = await server.WaitForConnectionAsync(1, EventWait);
        await server.WaitForFrameAsync(f => f.Contains("\"SUBSCRIBE\""), EventWait);

        await connection.SendTextAsync(BareTickerPayloadFrame);
        await connection.SendTextAsync(EnvelopeWithoutData);
        await connection.SendTextAsync(EnvelopeWithoutStream);
        await connection.SendTextAsync(TickerStringFrame);
        await connection.SendTextAsync(TickerSecondStringFrame);

        (await tailTickersTask).Should().Equal(new[] { ExpectedTickerOne, ExpectedTickerSecond });
    }

    [Fact]
    public async Task Adapter_connection_state_follows_the_socket()
    {
        await using var stack = await E2eStack.StartAsync();
        var server = stack.Server;
        var adapter = stack.Adapter;

        await adapter.ConnectAsync();
        adapter.IsConnected.Should().BeTrue();

        var connection = await server.WaitForConnectionAsync(1, EventWait);
        await connection.CloseGracefullyAsync();

        // The adapter mirrors ConnectionStateStream with no artificial wait:
        // disconnect is immediate, the production reconnect delay never starts.
        await TestPoll.PollUntilAsync(() => !adapter.IsConnected, EventWait);
    }

    #endregion

    #region Helpers

    private static async Task<IList<Ticker>> CollectTickerAsync(BinanceAdapter adapter) =>
        await adapter
            .SubscribeTicker(BtcUsdt)
            .Take(2)
            .ToList()
            .ToTask(TaskCreationOptions.RunContinuationsAsynchronously)
            .WaitAsync(EventWait);

    /// <summary>Pins the SUBSCRIBE frame the adapter sends on the first stream start.</summary>
    private static void AssertSubscribeFrame(string frame, int requestId)
    {
        using var json = JsonDocument.Parse(frame);
        var root = json.RootElement;

        root.GetProperty("method").GetString().Should().Be("SUBSCRIBE");
        root.GetProperty("id").GetInt32().Should().Be(requestId);
        root.GetProperty("params")
            .EnumerateArray()
            .Select(item => item.GetString())
            .Should().Equal(ExpectedStreamParams);
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
