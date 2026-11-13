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
/// E2e tests for <see cref="AlpacaAdapter"/> (T-198) over real sockets: the
/// whole transport path runs against the in-process loopback server —
/// Kestrel handshake, the venue's auth window (every frame is a JSON array;
/// the adapter answers nothing until the ack), the merged
/// trade/quote/dailyBar ticker, the top-of-book quote face and the reconnect
/// re-authentication + re-subscribe of every tracked symbol.
///
/// Wait budgets (T-116): <c>EventWait</c> is the 240 s house completion
/// budget shared by the whole WS E2E family — completion patience under
/// full-solution parallel load, never a product latency claim (see the
/// Bybit family header for the load-stall postmortem).
/// </summary>
public sealed class AlpacaAdapterWsE2eTests
{
    #region Constants

    /// <summary>The 240 s house load budget (T-116, see the class header).</summary>
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(240);

    /// <summary>Symbol used in every scenario; its exchange form is "AAPL".</summary>
    private static readonly Symbol Aapl = Symbol.Parse("AAPL");

    /// <summary>The outside-the-subscription symbol the honest-drop leg rides.</summary>
    private static readonly Symbol Msft = Symbol.Parse("MSFT");

    /// <summary>The venue's connect hello: quiet by design.</summary>
    private const string ConnectHelloFrame =
        """[{"T":"success","msg":"connected"}]""";

    /// <summary>The session-1 trade of the reconnect leg (one event, one array).</summary>
    private const string TradeFirstFrame =
        """
        [{"T":"t","S":"AAPL","i":7,"x":"D","p":187.5,"s":100,"t":"2026-11-13T15:51:45.335689322Z","z":"C"}]
        """;

    /// <summary>The venue's auth ack: the only success text the adapter acts on.</summary>
    private const string AuthAckFrame =
        """[{"T":"success","msg":"authenticated"}]""";

    /// <summary>A venue error event: the auth's honest failure answer.</summary>
    private const string AuthErrorFrame =
        """[{"T":"error","code":40110000,"msg":"auth failed"}]""";

    /// <summary>
    /// A real v2 trade shape, TWO events batched in ONE array message (the
    /// wire's own batching): numbers as numbers, a NANOSECOND tape
    /// timestamp, no aggressor side anywhere on the wire.
    /// </summary>
    private const string TradeBatchFrame =
        """
        [{"T":"t","S":"AAPL","i":7,"x":"D","p":187.5,"s":100,"t":"2026-11-13T15:51:45.335689322Z","z":"C"},{"T":"t","S":"AAPL","i":8,"x":"D","p":187.55,"s":110,"t":"2026-11-13T15:51:45.435689322Z","z":"C"}]
        """;

    /// <summary>A full top-of-book quote (both sides active).</summary>
    private const string QuoteBothSidesFrame =
        """
        [{"T":"q","S":"AAPL","bx":"D","bp":187.49,"bs":100,"ax":"D","ap":187.51,"as":200,"t":"2026-11-13T15:51:44Z","z":"C"}]
        """;

    /// <summary>A quote whose ask side carries the venue's no-active-quote zero: the ask serves EMPTY.</summary>
    private const string QuoteBidOnlyFrame =
        """
        [{"T":"q","S":"AAPL","bx":"D","bp":187.48,"bs":90,"ax":"D","ap":0,"as":0,"t":"2026-11-13T15:51:46Z","z":"C"}]
        """;

    /// <summary>A quote with NO active side on either side: dropped, never published.</summary>
    private const string QuoteZeroSidedFrame =
        """
        [{"T":"q","S":"AAPL","bx":"D","bp":0,"bs":0,"ax":"D","ap":0,"as":0,"t":"2026-11-13T15:51:47Z","z":"C"}]
        """;

    /// <summary>A real v2 dailyBar shape: the running session volume.</summary>
    private const string DailyBarFrame =
        """
        [{"T":"d","S":"AAPL","o":186.5,"h":188,"l":186,"c":187.5,"v":123456,"t":"2026-11-13T15:51:45Z"}]
        """;

    /// <summary>The merged event burst in ONE array message: quote, dailyBar, trade.</summary>
    private const string MergedBatchFrame =
        """
        [{"T":"q","S":"AAPL","bx":"D","bp":187.49,"bs":100,"ax":"D","ap":187.51,"as":200,"t":"2026-11-13T15:51:44Z","z":"C"},{"T":"d","S":"AAPL","o":186.5,"h":188,"l":186,"c":187.5,"v":123456,"t":"2026-11-13T15:51:45Z"},{"T":"t","S":"AAPL","i":7,"x":"D","p":187.5,"s":100,"t":"2026-11-13T15:51:45.335689322Z","z":"C"}]
        """;

    /// <summary>A quote for a symbol this adapter never subscribed.</summary>
    private const string QuoteUntrackedSymbolFrame =
        """
        [{"T":"q","S":"MSFT","bx":"D","bp":420.1,"bs":10,"ax":"D","ap":420.3,"as":20,"t":"2026-11-13T15:52:00Z","z":"C"}]
        """;

    /// <summary>An event type the adapter never subscribed (and the wire never promised).</summary>
    private const string UnknownTypeFrame =
        """
        [{"T":"zz","S":"AAPL","t":"2026-11-13T15:53:00Z"}]
        """;

    /// <summary>A protocol violation: the v2 wire speaks ONLY arrays.</summary>
    private const string NonArrayFrame =
        """
        {"T":"t","S":"AAPL","i":9,"x":"D","p":187.5,"s":100,"t":"2026-11-13T15:54:00Z","z":"C"}
        """;

    /// <summary>The session-2 trade of the reconnect leg.</summary>
    private const string TradeSecondFrame =
        """
        [{"T":"t","S":"AAPL","i":9,"x":"D","p":187.6,"s":120,"t":"2026-11-13T15:55:00Z","z":"C"}]
        """;

    /// <summary>The trimmed stream timestamp of the merged batch's trade event.</summary>
    private static readonly DateTimeOffset MergedBatchTradeTimestamp =
        DateTimeOffset.Parse("2026-11-13T15:51:45.3356893Z");

    #endregion

    #region Tests

    [Fact]
    public async Task Auth_action_carries_the_secret_pair_then_subscribe_serves_all_channels()
    {
        await using var stack = await E2eStack.StartAsync();
        var server = stack.Server;

        using var subscription = stack.Adapter.SubscribeTrades(Aapl).Subscribe(_ => { });

        var connection = await server.WaitForConnectionAsync(1, EventWait);
        var authFrame = await server.WaitForFrameAsync(
            f => f.Contains("\"action\":\"auth\""),
            EventWait);

        authFrame.Should().Contain("\"action\":\"auth\"")
            .And.Contain("\"key\":\"e2e-key\"")
            .And.Contain("\"secret\":\"e2e-secret\"");

        await connection.SendTextAsync(ConnectHelloFrame);
        await connection.SendTextAsync(AuthAckFrame);

        var subscribeFrame = await server.WaitForFrameAsync(
            f => f.Contains("\"action\":\"subscribe\""),
            EventWait);

        subscribeFrame.Should().Contain("\"trades\":[\"AAPL\"]")
            .And.Contain("\"quotes\":[\"AAPL\"]")
            .And.Contain("\"dailyBars\":[\"AAPL\"]");
        stack.Adapter.IsConnected.Should().BeTrue();
    }

    [Fact]
    public async Task Batched_trade_events_publish_the_neutral_side_tape()
    {
        await using var stack = await E2eStack.StartAsync();
        var server = stack.Server;

        var tradesTask = stack.Adapter
            .SubscribeTrades(Aapl)
            .Take(2)
            .ToList()
            .ToTask(TaskCreationOptions.RunContinuationsAsynchronously)
            .WaitAsync(EventWait);

        var connection = await server.WaitForConnectionAsync(1, EventWait);
        await server.WaitForFrameAsync(f => f.Contains("\"action\":\"auth\""), EventWait);
        await connection.SendTextAsync(AuthAckFrame);
        await server.WaitForFrameAsync(f => f.Contains("\"action\":\"subscribe\""), EventWait);

        // ONE array message carrying BOTH events: the wire's own batching
        // must dispatch every element, and the tape carries NO aggressor
        // side — the wire never said one (never a fabricated side).
        await connection.SendTextAsync(TradeBatchFrame);

        var trades = await tradesTask;
        trades[0].Id.Should().Be("7");
        trades[0].Price.Should().Be(new Price(187.5m));
        trades[0].Quantity.Should().Be(new Quantity(100m));
        trades[0].Side.Should().Be(OrderSide.None, "the wire serves no aggressor side for a stock trade");
        trades[1].Id.Should().Be("8");
        trades[1].Price.Should().Be(new Price(187.55m));
        trades[1].Side.Should().Be(OrderSide.None);
        trades.Should().OnlyContain(trade => trade.Symbol == Aapl);
    }

    [Fact]
    public async Task Merged_ticker_publishes_once_priced_and_carries_quote_and_session_volume()
    {
        await using var stack = await E2eStack.StartAsync();
        var server = stack.Server;

        var tickersTask = stack.Adapter
            .SubscribeTicker(Aapl)
            .Take(1)
            .ToList()
            .ToTask(TaskCreationOptions.RunContinuationsAsynchronously)
            .WaitAsync(EventWait);

        var connection = await server.WaitForConnectionAsync(1, EventWait);
        await server.WaitForFrameAsync(f => f.Contains("\"action\":\"auth\""), EventWait);
        await connection.SendTextAsync(AuthAckFrame);
        await server.WaitForFrameAsync(f => f.Contains("\"action\":\"subscribe\""), EventWait);

        await connection.SendTextAsync(MergedBatchFrame);

        var tickers = await tickersTask;
        tickers.Should().ContainSingle("the quote and the dailyBar precede the trade: the merge publishes when the wire first says a price");
        var ticker = tickers[0];
        ticker.Symbol.Should().Be(Aapl);
        ticker.LastPrice.Should().Be(187.5m);
        ticker.Bid.Should().Be(187.49m, "the quote channel's best bid rides the merge");
        ticker.Ask.Should().Be(187.51m);
        ticker.Volume24h.Should().Be(123456m, "the dailyBars channel's running session volume rides the merge");
        ticker.PriceChange24hPercent.Should().Be(0m, "the stream serves no previous close: the percent stays the honest zero");
        ticker.Timestamp.Should().Be(MergedBatchTradeTimestamp);
    }

    [Fact]
    public async Task Top_of_book_serves_one_level_per_active_side()
    {
        await using var stack = await E2eStack.StartAsync();
        var server = stack.Server;

        var booksTask = stack.Adapter
            .SubscribeOrderBook(Aapl, 1)
            .Take(2)
            .ToList()
            .ToTask(TaskCreationOptions.RunContinuationsAsynchronously)
            .WaitAsync(EventWait);

        var connection = await server.WaitForConnectionAsync(1, EventWait);
        await server.WaitForFrameAsync(f => f.Contains("\"action\":\"auth\""), EventWait);
        await connection.SendTextAsync(AuthAckFrame);
        await server.WaitForFrameAsync(f => f.Contains("\"action\":\"subscribe\""), EventWait);

        await connection.SendTextAsync(QuoteBothSidesFrame);
        await connection.SendTextAsync(QuoteBidOnlyFrame);

        var books = await booksTask;
        books[0].Bids.Should().ContainSingle().Which.Should().Be(new PriceLevel(187.49m, 100m));
        books[0].Asks.Should().ContainSingle().Which.Should().Be(new PriceLevel(187.51m, 200m));

        // The venue's no-active-quote zero: that side serves EMPTY, never a
        // padded zero level.
        books[1].Bids.Should().ContainSingle().Which.Should().Be(new PriceLevel(187.48m, 90m));
        books[1].Asks.Should().BeEmpty();
    }

    [Fact]
    public async Task A_zero_sided_quote_is_dropped_with_a_warning()
    {
        await using var stack = await E2eStack.StartAsync();
        var server = stack.Server;

        var booksSeen = 0;
        using var subscription = stack.Adapter.SubscribeOrderBook(Aapl, 1).Subscribe(_ => booksSeen++);

        var connection = await server.WaitForConnectionAsync(1, EventWait);
        await server.WaitForFrameAsync(f => f.Contains("\"action\":\"auth\""), EventWait);
        await connection.SendTextAsync(AuthAckFrame);
        await server.WaitForFrameAsync(f => f.Contains("\"action\":\"subscribe\""), EventWait);

        await connection.SendTextAsync(QuoteZeroSidedFrame);

        await TestPoll.PollUntilAsync(
            () => stack.Logger.Entries.Any(entry =>
                entry.Message.Contains("no active quote")),
            EventWait);
        booksSeen.Should().Be(0, "a quote with no active side is not a book");
    }

    [Fact]
    public async Task A_frame_for_an_untracked_symbol_is_dropped_with_a_warning()
    {
        await using var stack = await E2eStack.StartAsync();
        var server = stack.Server;

        var booksSeen = 0;
        using var subscription = stack.Adapter.SubscribeOrderBook(Aapl, 1).Subscribe(_ => booksSeen++);

        var connection = await server.WaitForConnectionAsync(1, EventWait);
        await server.WaitForFrameAsync(f => f.Contains("\"action\":\"auth\""), EventWait);
        await connection.SendTextAsync(AuthAckFrame);
        await server.WaitForFrameAsync(f => f.Contains("\"action\":\"subscribe\""), EventWait);

        await connection.SendTextAsync(QuoteBothSidesFrame);
        await connection.SendTextAsync(QuoteUntrackedSymbolFrame);

        await TestPoll.PollUntilAsync(
            () => stack.Logger.Entries.Any(entry =>
                entry.Message.Contains("untracked symbol")),
            EventWait);
        booksSeen.Should().Be(1, "the tracked quote published exactly once; the untracked one never did");
    }

    [Fact]
    public async Task Non_array_frames_and_unknown_types_are_dropped_with_a_warning()
    {
        await using var stack = await E2eStack.StartAsync();
        var server = stack.Server;

        // The lazy start rides the first subscriber: open a feed so the
        // wire session stands before the violation frames arrive.
        using var subscription = stack.Adapter.SubscribeTrades(Aapl).Subscribe(_ => { });

        var connection = await server.WaitForConnectionAsync(1, EventWait);
        await server.WaitForFrameAsync(f => f.Contains("\"action\":\"auth\""), EventWait);
        await connection.SendTextAsync(AuthAckFrame);
        await server.WaitForFrameAsync(f => f.Contains("\"action\":\"subscribe\""), EventWait);

        await connection.SendTextAsync(NonArrayFrame);
        await connection.SendTextAsync(UnknownTypeFrame);

        await TestPoll.PollUntilAsync(
            () => stack.Logger.Entries.Any(entry =>
                entry.Message.Contains("not the protocol's event array")),
            EventWait);
        stack.Logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("unknown event type"));
    }

    [Fact]
    public async Task Reconnection_reauthenticates_and_resubscribes()
    {
        await using var stack = await E2eStack.StartAsync();
        var server = stack.Server;
        var adapter = stack.Adapter;

        var tradesTask = adapter
            .SubscribeTrades(Aapl)
            .Take(2)
            .ToList()
            .ToTask(TaskCreationOptions.RunContinuationsAsynchronously)
            .WaitAsync(EventWait);

        var first = await server.WaitForConnectionAsync(1, EventWait);
        await server.WaitForFrameAsync(f => f.Contains("\"action\":\"auth\""), EventWait);
        await first.SendTextAsync(AuthAckFrame);
        await server.WaitForFrameAsync(f => f.Contains("\"action\":\"subscribe\""), EventWait);
        await first.SendTextAsync(TradeFirstFrame);

        // Server drops the session; the client reconnects on its own.
        await first.CloseGracefullyAsync();
        await TestPoll.PollUntilAsync(() => server.ConnectionCount >= 2, EventWait);

        // The fresh session must re-authenticate: the venue holds no session
        // state across sockets.
        await TestPoll.PollUntilAsync(
            () => server.SnapshotFrames()
                .Count(frame => frame.Contains("\"action\":\"auth\"")) >= 2,
            EventWait);
        var second = await server.WaitForConnectionAsync(2, EventWait);
        await second.SendTextAsync(AuthAckFrame);

        // ... and re-subscribe the tracked symbol on the wire.
        await TestPoll.PollUntilAsync(
            () => server.SnapshotFrames()
                .Count(frame => frame.Contains("\"action\":\"subscribe\"")) >= 2,
            EventWait);

        await second.SendTextAsync(TradeSecondFrame);

        var trades = await tradesTask;
        trades[0].Id.Should().Be("7");
        trades[1].Id.Should().Be("9", "the reconnect re-subscribed the tracked symbol: session 2's data flows");
        adapter.IsConnected.Should().BeTrue();
    }

    [Fact]
    public async Task An_error_event_fails_the_session_with_the_venue_diagnosis()
    {
        await using var stack = await E2eStack.StartAsync();
        var server = stack.Server;

        var failures = new List<Exception>();
        using var subscription = stack.Adapter.SubscribeTrades(Aapl).Subscribe(_ => { }, failures.Add);

        var connection = await server.WaitForConnectionAsync(1, EventWait);
        await server.WaitForFrameAsync(f => f.Contains("\"action\":\"auth\""), EventWait);
        await connection.SendTextAsync(AuthErrorFrame);

        await TestPoll.PollUntilAsync(() => failures.Count >= 1, EventWait);
        failures.Should().ContainSingle().Which.Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should().Contain("authentication failed");
    }

    [Fact]
    public async Task A_missing_secret_pair_fails_the_session_start_naming_both_keys()
    {
        await using var stack = await E2eStack.StartAsync(new Dictionary<string, string>());

        var failures = new List<Exception>();
        using var subscription = stack.Adapter.SubscribeTrades(Aapl).Subscribe(_ => { }, failures.Add);

        await TestPoll.PollUntilAsync(() => failures.Count >= 1, EventWait);
        failures.Should().ContainSingle().Which.Message.Should()
            .Contain(AlpacaAdapter.ApiKeySecretKey)
            .And.Contain(AlpacaAdapter.ApiSecretSecretKey);
    }

    #endregion

    #region Harness

    /// <summary>
    /// The adapter, its recording logger, its service provider and the
    /// loopback server disposed in dependency order (adapter first, host
    /// last). The REST faces ride a reserved-invalid host: this suite's
    /// facts ride the STREAM only, so any unplanned REST dial fails loudly.
    /// </summary>
    private sealed class E2eStack : IAsyncDisposable
    {
        #region Fields

        private readonly ServiceProvider _provider;

        #endregion

        #region Constructor / Properties

        private E2eStack(LoopbackWebSocketServer server, RecordingLogger logger, ServiceProvider provider, AlpacaAdapter adapter)
        {
            Server = server;
            Logger = logger;
            _provider = provider;
            Adapter = adapter;
        }

        public LoopbackWebSocketServer Server { get; }

        /// <summary>The recording logger: structured entries for the honest-drop assertions.</summary>
        public RecordingLogger Logger { get; }

        public AlpacaAdapter Adapter { get; }

        #endregion

        #region Methods: Factory

        public static async Task<E2eStack> StartAsync(IReadOnlyDictionary<string, string>? secretPairs = null)
        {
            var server = await LoopbackWebSocketServer.StartAsync();

            var services = new ServiceCollection();
            services.AddHttpClient(AlpacaAdapter.HttpClientName);
            var provider = services.BuildServiceProvider();

            var logger = new RecordingLogger();
            var adapter = new AlpacaAdapter(
                logger,
                provider.GetRequiredService<IHttpClientFactory>(),
                new StubSecretStore(secretPairs),
                new AlpacaEndpoints(
                    RestBaseUrl: "http://alpaca-e2e-rest.invalid",
                    CatalogRestBaseUrl: "http://alpaca-e2e-catalog.invalid",
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
    private sealed class RecordingLogger : ILogger<AlpacaAdapter>
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

    /// <summary>The stub secret store: the e2e pair (never a real credential) or an explicit empty store.</summary>
    private sealed class StubSecretStore(IReadOnlyDictionary<string, string>? pairs = null) : Core.Interfaces.ISecretStore
    {
        private readonly IReadOnlyDictionary<string, string> _pairs =
            pairs ?? new Dictionary<string, string>
            {
                [AlpacaAdapter.ApiKeySecretKey] = "e2e-key",
                [AlpacaAdapter.ApiSecretSecretKey] = "e2e-secret",
            };

        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(_pairs.TryGetValue(key, out var value) ? value : null);

        public Task SetAsync(string key, string value, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RemoveAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListKeysAsync(string prefix = "", CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    #endregion
}
