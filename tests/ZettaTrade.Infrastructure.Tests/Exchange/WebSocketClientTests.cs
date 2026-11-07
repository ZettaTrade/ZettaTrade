namespace ZettaTrade.Infrastructure.Tests.Exchange;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// E2e transport tests for <see cref="WebSocketClient"/> against the in-process
/// loopback Kestrel server: handshake, verbatim framing, fragmented decode,
/// graceful close with real reconnect, refused endpoints and the send path.
/// All waits are bounded; no real hosts are ever contacted.
///
/// Wait budgets (T-116, 2026-11-07): <c>EventWait</c> is the 120 s house
/// completion budget shared by the whole WS E2E family, raised from 10 s.
/// The old cap tripped under full-solution parallel test load (xUnit hosts
/// across sibling assemblies chewing cores): a scheduler stall starved the
/// loopback handshake and frame beats past the cap and the collector died as
/// a bare System.TimeoutException inside WaitAsync(EventWait) while every
/// focused run stayed green — the transport behaved all along. This is load
/// patience, never a product timing claim: solo runs always passed and still
/// must, and deliberate latency ceilings (where any ever appear) stay
/// separate from this budget. ShortReconnectDelay and MaxReconnectDelay are
/// reconnect-policy configuration of the client under test, not waits.
/// </summary>
public sealed class WebSocketClientTests
{
    #region Constants

    /// <summary>
    /// The 120 s house load budget (T-116, see the class header): bounded wait
    /// for any expected observable event or connection — completion patience,
    /// never a latency ceiling.
    /// </summary>
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(120);

    /// <summary>Short reconnect delay so the reconnect test stays sub-second.</summary>
    private static readonly TimeSpan ShortReconnectDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>Attempts for the refused-endpoint test (the handshake failure is immediate).</summary>
    private const int RefusedConnectAttempts = 2;

    /// <summary>Reconnect attempts for the normal scenarios.</summary>
    private const int ReconnectAttempts = 5;

    /// <summary>An empty probe path — the server answers plain HTTP, never an upgrade.</summary>
    private const string MissingStreamPath = "/missing";

    private const string TextFrameOne = "probe-frame-one";

    private const string TextFrameTwo = "probe-frame-two";

    #endregion

    #region Tests

    [Fact]
    public async Task Connect_reports_state_and_completes_after_the_handshake()
    {
        await using var server = await LoopbackWebSocketServer.StartAsync();
        await using var client = CreateClient(server.CombinedStreamUrl, ShortReconnectDelay, ReconnectAttempts);

        // BehaviorSubject seeds false at subscription; the open emits true.
        var statesTask = CollectAsync(client.ConnectionStateStream, 2);

        await client.ConnectAsync();
        client.IsConnected.Should().BeTrue();

        var connection = await server.WaitForConnectionAsync(1, EventWait);
        connection.Index.Should().Be(1);

        (await statesTask).Should().Equal(new[] { false, true });
    }

    [Fact]
    public async Task Server_pushed_frames_flow_verbatim()
    {
        await using var server = await LoopbackWebSocketServer.StartAsync();
        await using var client = CreateClient(server.CombinedStreamUrl, ShortReconnectDelay, ReconnectAttempts);
        await client.ConnectAsync();

        var framesTask = CollectAsync(client.MessageStream, 2);
        var connection = await server.WaitForConnectionAsync(1, EventWait);

        await connection.SendTextAsync(TextFrameOne);
        await connection.SendTextAsync(TextFrameTwo);

        (await framesTask).Should().Equal(new[] { TextFrameOne, TextFrameTwo });
    }

    [Fact]
    public async Task Fragmented_utf8_message_decodes_as_one()
    {
        // Non-ASCII text: UTF-8 sequences span several bytes per character, and
        // the split index lands inside the first 2-byte alpha byte sequence.
        const string fragmentedText = "αβγ€ηφ";
        const int splitByteIndex = 1;

        await using var server = await LoopbackWebSocketServer.StartAsync();
        await using var client = CreateClient(server.CombinedStreamUrl, ShortReconnectDelay, ReconnectAttempts);
        await client.ConnectAsync();

        var framesTask = CollectAsync(client.MessageStream, 1);
        var connection = await server.WaitForConnectionAsync(1, EventWait);

        await connection.SendTextFragmentedAsync(fragmentedText, splitByteIndex);

        (await framesTask).Should().Equal(new[] { fragmentedText });
    }

    [Fact]
    public async Task Graceful_server_close_reconnects_and_state_flips()
    {
        await using var server = await LoopbackWebSocketServer.StartAsync();
        await using var client = CreateClient(server.CombinedStreamUrl, ShortReconnectDelay, ReconnectAttempts);

        // Seed false at subscribe, true on open, false when the server closes,
        // true again once the reconnect loop reopens the socket.
        var statesTask = CollectAsync(client.ConnectionStateStream, 4);

        await client.ConnectAsync();
        var firstConnection = await server.WaitForConnectionAsync(1, EventWait);

        var firstFrameTask = CollectAsync(client.MessageStream, 1);
        await firstConnection.SendTextAsync(TextFrameOne);
        (await firstFrameTask).Should().Equal(new[] { TextFrameOne });

        await firstConnection.CloseGracefullyAsync();

        // A fresh session (and a reconnected client) appears after the short delay;
        // the server-side accept may beat the client-side state write, so poll.
        var secondConnection = await server.WaitForConnectionAsync(2, EventWait);
        await TestPoll.PollUntilAsync(() => client.IsConnected, EventWait);

        var secondFrameTask = CollectAsync(client.MessageStream, 1);
        await secondConnection.SendTextAsync(TextFrameTwo);
        (await secondFrameTask).Should().Equal(new[] { TextFrameTwo });

        (await statesTask).Should().Equal(new[] { false, true, false, true });
        server.ConnectionCount.Should().Be(2);
    }

    [Fact]
    public async Task Refused_endpoint_fails_connect_after_configured_attempts()
    {
        // A real server on a missing path answers plain HTTP, never an
        // upgrade — a deterministic handshake refusal without port games.
        await using var server = await LoopbackWebSocketServer.StartAsync();
        await using var client = CreateClient(
            $"ws://127.0.0.1:{server.Port}{MissingStreamPath}",
            ShortReconnectDelay,
            RefusedConnectAttempts);

        Func<Task> connect = () => client.ConnectAsync();
        (await connect.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain(server.Port.ToString());
        client.IsConnected.Should().BeFalse();
        server.ConnectionCount.Should().Be(0, "a failed handshake never yields a session");
    }

    [Fact]
    public async Task Sends_deliver_to_the_server_and_inherit_the_connected_guard()
    {
        await using var server = await LoopbackWebSocketServer.StartAsync();
        await using var client = CreateClient(server.CombinedStreamUrl, ShortReconnectDelay, ReconnectAttempts);

        // Still unconnected: the send guard must reject before touching the wire.
        Func<Task> unconnectedSend = () => client.SendJsonAsync(new { hello = "world" });
        await unconnectedSend.Should().ThrowAsync<InvalidOperationException>();

        await client.ConnectAsync();
        await client.SendJsonAsync(new { hello = "world" });
        await server.WaitForFrameAsync(f => f.Contains("\"hello\""), EventWait);
        server.SnapshotFrames().Should().Contain("{\"hello\":\"world\"}");

        await client.SendAsync(TextFrameOne);
        await server.WaitForFrameAsync(f => f == TextFrameOne, EventWait);
    }

    /// <summary>
    /// Overlapping connects serialize on the lifecycle gate: the second call
    /// tears the first session down orderly (cancel → the loop is reaped →
    /// the socket is closed and disposed) BEFORE opening its own fresh
    /// session — never a loop racing a token or socket swap (BUG-029).
    /// </summary>
    [Fact]
    public async Task Overlapping_connects_serialize_their_sessions()
    {
        await using var server = await LoopbackWebSocketServer.StartAsync();
        var logger = new RecordingLogger();
        await using var client = CreateClient(
            server.CombinedStreamUrl,
            ShortReconnectDelay,
            ReconnectAttempts,
            logger);

        // Two racing connects: each completes its own whole session, and the
        // surviving (second) one carries frames on the wire.
        var firstConnect = client.ConnectAsync();
        var secondConnect = client.ConnectAsync();
        await Task.WhenAll(firstConnect, secondConnect);

        client.IsConnected.Should().BeTrue();

        var framesTask = CollectAsync(client.MessageStream, 1);
        var secondConnection = await server.WaitForConnectionAsync(2, EventWait);
        await secondConnection.SendTextAsync(TextFrameOne);
        (await framesTask).Should().Equal(new[] { TextFrameOne });

        server.ConnectionCount.Should().Be(2, "the session replace keeps one upgrade per connect");
        logger.Entries.Should().NotContain(
            entry => entry.Level == LogLevel.Error,
            "no overlapped session teardown may surface as a crashed background loop");
    }

    /// <summary>
    /// An explicit disconnect tears the session down in dependency order —
    /// cancel FIRST, then reap the loop, then close and dispose the socket —
    /// so the blocked <c>ReceiveAsync</c> never aborts underneath the loop
    /// and no spurious "background loop crashed" error is logged (BUG-030).
    /// </summary>
    [Fact]
    public async Task Disconnect_closes_cleanly_without_a_spurious_loop_crash()
    {
        await using var server = await LoopbackWebSocketServer.StartAsync();
        var logger = new RecordingLogger();
        await using var client = CreateClient(
            server.CombinedStreamUrl,
            ShortReconnectDelay,
            ReconnectAttempts,
            logger);

        // Seed false at subscribe, true on open, false on the disconnect.
        var statesTask = CollectAsync(client.ConnectionStateStream, 3);

        await client.ConnectAsync();
        _ = await server.WaitForConnectionAsync(1, EventWait);

        await client.DisconnectAsync();

        client.IsConnected.Should().BeFalse();
        (await statesTask).Should().Equal(new[] { false, true, false });
        server.ConnectionCount.Should().Be(1, "an explicit disconnect never reconnects");

        logger.Entries.Should().NotContain(
            entry => entry.Level == LogLevel.Error,
            "the cancel-before-teardown order keeps the blocked loop from crashing");
    }

    /// <summary>
    /// Every connect call starts with a fresh attempt budget: two consecutive
    /// connects against a refusing endpoint each burn their OWN attempts (two
    /// honest per-session warnings each; a shared budget would make the
    /// second call throw with none), so a failed session never shrinks the
    /// next one (BUG-029).
    /// </summary>
    [Fact]
    public async Task A_failed_session_keeps_a_full_attempt_budget_for_the_next_connect()
    {
        // A real server on a missing path answers plain HTTP, never an
        // upgrade — a deterministic handshake refusal without port games.
        await using var server = await LoopbackWebSocketServer.StartAsync();
        var logger = new RecordingLogger();
        await using var client = CreateClient(
            $"ws://127.0.0.1:{server.Port}{MissingStreamPath}",
            ShortReconnectDelay,
            RefusedConnectAttempts,
            logger);

        Func<Task> firstConnect = () => client.ConnectAsync().WaitAsync(EventWait);
        await firstConnect.Should().ThrowAsync<InvalidOperationException>();
        logger.Entries.Count(entry => entry.Message.Contains("WebSocket connection failed"))
            .Should().Be(RefusedConnectAttempts, "the whole budget is burned honestly");

        // The second call starts from a FRESH budget: the failed previous
        // session cannot shrink it — the refusal warning appears per attempt
        // again, instead of the second call dying silently.
        Func<Task> secondConnect = () => client.ConnectAsync().WaitAsync(EventWait);
        await secondConnect.Should().ThrowAsync<InvalidOperationException>();
        logger.Entries.Count(entry => entry.Message.Contains("WebSocket connection failed"))
            .Should().Be(RefusedConnectAttempts * 2, "every fresh session owns the full attempt budget");
    }

    #endregion

    #region Helpers

    /// <summary>Deterministic fast-retry policy for the transport tests: the short initial delay with the jitter switched off.</summary>
    private static ReconnectPolicy TimedPolicy(TimeSpan initialDelay, int maxAttempts) => new()
    {
        InitialDelay = initialDelay,
        MaxDelay = MaxReconnectDelay,
        BackoffMultiplier = 2.0,
        JitterFraction = 0.0,
        MaxAttempts = maxAttempts,
    };

    /// <summary>Growth cap of the test policies (retries stay sub-second-batched under the event wait).</summary>
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(2);

    private static WebSocketClient CreateClient(string url, TimeSpan reconnectDelay, int maxConnectAttempts) =>
        new(
            url,
            NullLogger.Instance,
            TimedPolicy(reconnectDelay, maxConnectAttempts));

    private static WebSocketClient CreateClient(
        string url,
        TimeSpan reconnectDelay,
        int maxConnectAttempts,
        ILogger logger) =>
        new(
            url,
            logger,
            TimedPolicy(reconnectDelay, maxConnectAttempts));

    /// <summary>Materialize the first <paramref name="expectedCount"/> stream events into a bounded task.</summary>
    private static Task<IList<T>> CollectAsync<T>(IObservable<T> source, int expectedCount) =>
        source
            .Take(expectedCount)
            .ToList()
            .ToTask(TaskCreationOptions.RunContinuationsAsynchronously)
            .WaitAsync(EventWait);

    /// <summary>
    /// The recording logger: collects level + formatted message pairs, so the
    /// tests can pin the absence of crash-style Error entries and count the
    /// honest per-session attempt warnings.
    /// </summary>
    private sealed class RecordingLogger : ILogger
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
