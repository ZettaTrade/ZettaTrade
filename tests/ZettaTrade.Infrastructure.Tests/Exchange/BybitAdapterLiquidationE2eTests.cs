namespace ZettaTrade.Infrastructure.Tests.Exchange;

using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
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
/// E2e tests for the Bybit adapter's liquidation lane (T-242) over real
/// sockets: the whole transport path runs against the in-process loopback
/// server — the dedicated liquidation socket's on-demand dial (it shares the
/// linear host by configuration), the per-contract <c>allLiquidation</c>
/// v5 subscribe frame, the <c>{"topic","type","ts","data"}</c> dispatch, the
/// POSITION-side normalization (S:Buy = a LONG died, S:Sell = a SHORT died),
/// the malformed rows' count-and-drop, the untracked contract's drop, the
/// ref-counted release (an unsubscribe and the socket drop when the last
/// subscriber leaves) and the reconnect re-subscribe of the tracked topic.
///
/// Wait budgets (T-116): <c>EventWait</c> is the 240 s house completion
/// budget shared by the whole WS E2E family — load patience, never a
/// product timing claim.
/// </summary>
public sealed class BybitAdapterLiquidationE2eTests
{
    #region Constants

    /// <summary>
    /// The 240 s house load budget (T-116, see the class header): bounded wait
    /// for any expected observable event or connection — completion patience,
    /// never a latency ceiling.
    /// </summary>
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(240);

    /// <summary>Symbol used in every scenario; its exchange form is "BTCUSDT".</summary>
    private static readonly Symbol BtcUsdt = new("btc", "usdt", AssetClass.Crypto);

    /// <summary>
    /// The measured live wire (2026-11-16): one row whose <c>T</c> is the
    /// event's epoch-milliseconds NUMBER, <c>v</c>/<c>p</c> the size and
    /// bankruptcy-price NUMERIC STRINGS and <c>S</c> the POSITION side the
    /// venue force-closed (S:Sell = a SHORT died).
    /// </summary>
    private const string LiquidationFrame = """
        {"topic":"allLiquidation.BTCUSDT","ts":1699999999000,"type":"snapshot","data":[{"T":1699999999000,"s":"BTCUSDT","S":"Sell","v":"13.53","p":"1826.73"}]}
        """;

    /// <summary>A Buy close: the LONG position was force-closed.</summary>
    private const string LiquidationBuyFrame = """
        {"topic":"allLiquidation.BTCUSDT","ts":1699999999500,"type":"snapshot","data":[{"T":1699999999500,"s":"BTCUSDT","S":"Buy","v":"2.5","p":"51000.25"}]}
        """;

    /// <summary>A frame whose array mixes malformed entries between two valid ones.</summary>
    private const string LiquidationFrameWithMalformedRows = """
        {"topic":"allLiquidation.BTCUSDT","ts":1699999999600,"type":"snapshot","data":["not-an-object",42,{"T":1699999999600,"s":"BTCUSDT","S":"Buy","v":"1","p":"100"},{"T":1699999999700,"s":"BTCUSDT","S":"Sell","v":"1.5","p":"100.5"}]}
        """;

    /// <summary>A liquidation row of a contract the lane never subscribed.</summary>
    private const string LiquidationUntrackedFrame = """
        {"topic":"allLiquidation.ETHUSDT","ts":1699999999800,"type":"snapshot","data":[{"T":1699999999800,"s":"ETHUSDT","S":"Sell","v":"1","p":"1800"}]}
        """;

    /// <summary>A control ack (minus the topic) that must never publish data.</summary>
    private const string SubscribeAckFrame = """
        {"success":true,"ret_msg":"subscribe","conn_id":"loopback-1","op":"subscribe"}
        """;

    #endregion

    #region Tests

    [Fact]
    public async Task First_liquidation_subscription_dials_and_publishes_events()
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
            f => f.Contains("\"op\":\"subscribe\"") && f.Contains("allLiquidation."),
            EventWait);
        subscribeFrame.Should().Contain("\"args\":[\"allLiquidation.BTCUSDT\"]", "the contract's own topic rides the dedicated socket");

        // The control ack carries no topic: it must never reach the feeds.
        await connection.SendTextAsync(SubscribeAckFrame);
        await connection.SendTextAsync(LiquidationFrame);

        var events = await eventsTask;
        events[0].Symbol.Should().Be(BtcUsdt);
        events[0].LiquidatedSide.Should().Be(LiquidationSide.Short, "S:Sell force-closed a SHORT position");
        events[0].Price.Value.Should().Be(1826.73m, "the venue's served bankruptcy price");
        events[0].Quantity.Value.Should().Be(13.53m, "the venue's served size");
        events[0].Timestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1699999999000));
        stack.Adapter.IsConnected.Should().BeTrue("the dedicated socket's state aggregates into the adapter's");
    }

    [Fact]
    public async Task A_Buy_row_names_the_force_closed_LONG()
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
        await server.WaitForFrameAsync(f => f.Contains("allLiquidation."), EventWait);

        await connection.SendTextAsync(LiquidationBuyFrame);

        var events = await eventsTask;
        events[0].LiquidatedSide.Should().Be(LiquidationSide.Long, "S:Buy force-closed a LONG position");
        events[0].Price.Value.Should().Be(51000.25m);
        events[0].Quantity.Value.Should().Be(2.5m);
    }

    [Fact]
    public async Task Malformed_rows_are_counted_and_dropped_never_thrown()
    {
        await using var stack = await E2eStack.StartAsync();
        var server = stack.Server;

        var eventsTask = stack.Adapter
            .SubscribeLiquidations(BtcUsdt)
            .Take(2)
            .ToList()
            .ToTask(TaskCreationOptions.RunContinuationsAsynchronously)
            .WaitAsync(EventWait);

        var connection = await server.WaitForConnectionAsync(1, EventWait);
        await server.WaitForFrameAsync(f => f.Contains("allLiquidation."), EventWait);

        await connection.SendTextAsync(LiquidationFrameWithMalformedRows);

        // Both VALID rows of the frame publish; the non-object entries in
        // the middle of the array are counted, not thrown (a thrown
        // InvalidOperationException would publish NOTHING here).
        var events = await eventsTask;
        events[0].LiquidatedSide.Should().Be(LiquidationSide.Long);
        events[1].LiquidatedSide.Should().Be(LiquidationSide.Short);

        await TestPoll.PollUntilAsync(
            () => stack.Logger.Entries.Any(entry =>
                entry.Message.Contains("2 of 4 Bybit liquidation events for BTC/USDT are malformed")),
            EventWait);
    }

    [Fact]
    public async Task Untracked_contract_frames_drop_never_publish()
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
        await server.WaitForFrameAsync(f => f.Contains("allLiquidation."), EventWait);

        // The untracked contract's rows must drop: the Take(1) task can only
        // complete on the tracked contract's event — a leak would have
        // completed it with the ETHUSDT fact.
        await connection.SendTextAsync(LiquidationUntrackedFrame);
        await connection.SendTextAsync(LiquidationFrame);

        var events = await eventsTask;
        events[0].Symbol.Should().Be(BtcUsdt);
        events[0].Price.Value.Should().Be(1826.73m, "the FIRST published event is the tracked one, never the dropped one");
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
        await server.WaitForFrameAsync(f => f.Contains("\"op\":\"subscribe\""), EventWait);

        subscription.Dispose();
        await TestPoll.PollUntilAsync(
            () => server.SnapshotFrames().Any(frame => frame.Contains("\"op\":\"unsubscribe\"")),
            EventWait);

        await TestPoll.PollUntilAsync(() => connection.IsClosed, EventWait);
        await TestPoll.PollUntilAsync(() => !stack.Adapter.IsConnected, EventWait);
    }

    [Fact]
    public async Task Reconnection_resubscribes_the_tracked_topic()
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
        await server.WaitForFrameAsync(f => f.Contains("allLiquidation."), EventWait);

        await first.SendTextAsync(LiquidationFrame);

        // Server drops the session; the client reconnects on its own.
        await first.CloseGracefullyAsync();
        await TestPoll.PollUntilAsync(() => server.ConnectionCount >= 2, EventWait);

        // The reconnection must re-subscribe the tracked topic on the wire.
        await TestPoll.PollUntilAsync(
            () => server.SnapshotFrames()
                .Count(frame => frame.Contains("\"op\":\"subscribe\"") && frame.Contains("allLiquidation.")) >= 2,
            EventWait);

        // Feed a row on the SECOND session: it must reach the still-live
        // (outer) subscription — the data path survived the reconnect.
        var second = await server.WaitForConnectionAsync(2, EventWait);
        await second.SendTextAsync(LiquidationBuyFrame);

        var events = await eventsTask;
        events[0].LiquidatedSide.Should().Be(LiquidationSide.Short);
        events[1].LiquidatedSide.Should().Be(LiquidationSide.Long, "the reconnect re-subscribed the topic");
        adapter.IsConnected.Should().BeTrue();
    }

    #endregion

    #region Helpers

    /// <summary>
    /// The adapter, its recording logger, its service provider and the
    /// loopback server disposed in dependency order (adapter first, host
    /// last). The liquidation socket shares the linear host BY
    /// CONFIGURATION: a blank LinearWsUrl would derive the shipped
    /// PRODUCTION linear host and dial out.
    /// </summary>
    private sealed class E2eStack : IAsyncDisposable
    {
        #region Fields

        private readonly ServiceProvider _provider;

        #endregion

        #region Constructor / Properties

        private E2eStack(LoopbackWebSocketServer server, RecordingLogger logger, ServiceProvider provider, BybitAdapter adapter)
        {
            Server = server;
            Logger = logger;
            _provider = provider;
            Adapter = adapter;
        }

        public LoopbackWebSocketServer Server { get; }

        /// <summary>The recording logger: structured entries for the honest-drop assertions.</summary>
        public RecordingLogger Logger { get; }

        public BybitAdapter Adapter { get; }

        #endregion

        #region Methods: Factory

        public static async Task<E2eStack> StartAsync()
        {
            var server = await LoopbackWebSocketServer.StartAsync();

            var services = new ServiceCollection();
            services.AddHttpClient(BybitAdapter.HttpClientName);
            var provider = services.BuildServiceProvider();

            var logger = new RecordingLogger();
            var adapter = new BybitAdapter(
                logger,
                provider.GetRequiredService<IHttpClientFactory>(),
                new BybitEndpoints(
                    RestBaseUrl: $"http://127.0.0.1:{server.Port}",
                    WsUrl: server.CombinedStreamUrl,
                    LinearWsUrl: server.CombinedStreamUrl));

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
    private sealed class RecordingLogger : ILogger<BybitAdapter>
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
