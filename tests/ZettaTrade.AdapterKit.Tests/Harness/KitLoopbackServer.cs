namespace ZettaTrade.AdapterKit.Tests.Harness;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

/// <summary>
/// The kit's own in-process loopback WebSocket rig (Kestrel): the transcript
/// battery rides it the way the adapter dials a real host — 127.0.0.1 only,
/// a rig that captures every received frame in arrival order and sends
/// recorded frames on demand. Session N is the rig's Nth accepted WebSocket;
/// a reconnecting adapter therefore addresses "the new session" by its
/// ordinal, which is exactly the reconnect evidence the kit needs.
/// </summary>
public sealed class KitLoopbackServer : IAsyncDisposable
{
    #region Fields

    /// <summary>How many freshly reserved ports the start tries on bind contention (the established loopback-rig rule).</summary>
    private const int StartBindRetries = 3;

    /// <summary>Interval between condition checks while polling.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>The queued transcript of everything the rig received, in arrival order.</summary>
    private readonly ConcurrentQueue<string> _receivedFrames = new();

    /// <summary>Accepted sessions by their 1-based ordinal; sending reads it back through <see cref="WaitForSessionAsync"/>.</summary>
    private readonly ConcurrentDictionary<int, KitLoopbackSession> _sessions = new();

    private int _sessionTotal;
    private WebApplication? _app;

    #endregion

    #region Properties

    /// <summary>Loopback port the rig listens on.</summary>
    public int Port { get; private set; }

    /// <summary>The stream URL adapters dial.</summary>
    public string StreamUrl => $"ws://127.0.0.1:{Port}/stream";

    /// <summary>How many WebSocket sessions were accepted in total (reconnect and churn evidence).</summary>
    public int SessionCount => Volatile.Read(ref _sessionTotal);

    #endregion

    #region Methods: Factory

    /// <summary>Start the rig on a loopback port and make it accept WebSocket sessions.</summary>
    /// <param name="ct">The start's cancellation token.</param>
    public static async Task<KitLoopbackServer> StartAsync(CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            var port = ReserveLoopbackPort();
            var server = new KitLoopbackServer();
            try
            {
                await server.StartOnPortAsync(port, ct);
                return server;
            }
            catch (IOException ex) when (attempt < StartBindRetries - 1 && IsAddressInUse(ex))
            {
                // The pre-reserved port lost to an unrelated binder: re-reserve and retry.
                await server.DisposeAsync();
            }
        }
    }

    private static int ReserveLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private static bool IsAddressInUse(IOException ex) =>
        ex.InnerException is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse };

    private async Task StartOnPortAsync(int port, CancellationToken ct)
    {
        // Log providers are stripped: nothing in the test host may write to stdout.
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

        var app = builder.Build();

        app.UseWebSockets();
        app.Map("/stream", HandleStreamRequestAsync);

        await app.StartAsync(ct);

        _app = app;
        Port = port;
    }

    #endregion

    #region Methods: Transcript

    /// <summary>
    /// The transcript snapshot in arrival order: a copy, safe to count while
    /// frames keep arriving (the queue's enumeration is thread-safe, so no
    /// lock belongs here). The battery's frame predicates read it only.
    /// </summary>
    public IReadOnlyList<string> SnapshotFrames() => _receivedFrames.ToList();

    #endregion

    #region Methods: Waiting

    /// <summary>
    /// Wait until the first captured frame matching <paramref name="predicate"/>
    /// exists. Arming the wait before the frames exist is safe: the wait is a
    /// bounded poll over the capture queue, so it answers both the
    /// already-arrived and the not-yet-arrived case.
    /// </summary>
    /// <param name="predicate">The frame matcher.</param>
    /// <param name="timeout">The bounded wait; on timeout a TimeoutException reports it, never a swallowed state.</param>
    public async Task<string> WaitForFrameAsync(Func<string, bool> predicate, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (true)
        {
            if (TryCapture(predicate, out var captured))
            {
                return captured!;
            }

            if (DateTime.UtcNow - start > timeout)
            {
                throw new TimeoutException("The captured frames never matched the predicate within the timeout.");
            }

            await Task.Delay(PollInterval);
        }
    }

    /// <summary>Wait for the rig's Nth accepted session (1-based). An already-accepted session answers instantly.</summary>
    /// <param name="index">The 1-based session ordinal.</param>
    /// <param name="timeout">The bounded wait.</param>
    public async Task<KitLoopbackSession> WaitForSessionAsync(int index, TimeSpan timeout)
    {
        // Both facts polled, never assumed: the accept loop bumps the counter
        // one statement before the dictionary write, so a count-only poll
        // could race the registry (a missing key would throw mid-leg).
        await TestPoll.PollUntilAsync(
            () => SessionCount >= index && _sessions.ContainsKey(index),
            timeout);
        return _sessions[index];
    }

    /// <summary>Send the given frames, in transmission order, into the rig's Nth accepted session.</summary>
    /// <param name="sessionIndex">The 1-based session ordinal.</param>
    /// <param name="frames">The raw frame texts.</param>
    public async Task SendFramesAsync(int sessionIndex, IReadOnlyList<string> frames)
    {
        var session = await WaitForSessionAsync(sessionIndex, TestWait);
        await session.SendTextAsync(frames);
    }

    private bool TryCapture(Func<string, bool> predicate, out string? matched)
    {
        matched = _receivedFrames.FirstOrDefault(predicate);
        return matched is not null;
    }

    /// <summary>The bounded wait any single rig call may spend on transport setup (sessions are accepted in milliseconds).</summary>
    private static readonly TimeSpan TestWait = TimeSpan.FromSeconds(10);

    #endregion

    #region Methods: Accept loop

    private async Task HandleStreamRequestAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var session = new KitLoopbackSession(socket);
        var ordinal = Interlocked.Increment(ref _sessionTotal);
        _sessions[ordinal] = session;

        await session.PumpReceiveLoopAsync(_receivedFrames);
    }

    #endregion

    #region IAsyncDisposable

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
    }

    #endregion
}

/// <summary>
/// One accepted WebSocket session of the kit rig: the send handoff and the
/// receive loop that captures every inbound frame onto the rig's transcript
/// queue. The loop runs to its natural completion (the client's close), never
/// abandoned.
/// </summary>
public sealed class KitLoopbackSession
{
    #region Constants

    /// <summary>The receive chunk size; kit frames are text and small, so one chunk per frame is the norm.</summary>
    private const int ReceiveChunkBytes = 8 * 1024;

    /// <summary>What the drop's close frame says (the kit's own, not any venue's).</summary>
    private const string DropReason = "the kit dropped this session";

    #endregion

    #region Fields

    private readonly WebSocket _socket;

    #endregion

    #region Constructor

    internal KitLoopbackSession(WebSocket socket)
    {
        _socket = socket;
    }

    #endregion

    #region Methods

    /// <summary>
    /// Drop this session the way a real venue would: begin the closing
    /// handshake from this end. <c>CloseOutputAsync</c>, never <c>CloseAsync</c>:
    /// the adapter's receive loop leaves the socket on the received Close
    /// frame and lets its reconnect machinery answer — a full-close wait
    /// would hang on a reply the client never sends (the standing rig
    /// pattern of the WS E2E suite).
    /// </summary>
    public async Task DropAsync()
    {
        await _socket.CloseOutputAsync(
            WebSocketCloseStatus.NormalClosure,
            DropReason,
            CancellationToken.None);
    }

    /// <summary>Send the given frames, in transmission order, to the client on this session.</summary>
    /// <param name="frames">The raw frame texts.</param>
    public async Task SendTextAsync(IReadOnlyList<string> frames)
    {
        foreach (var frame in frames)
        {
            var bytes = Encoding.UTF8.GetBytes(frame);
            await _socket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                CancellationToken.None);
        }
    }

    /// <summary>
    /// The receive loop: drain text frames until the client closes, capturing
    /// every completed frame onto <paramref name="captured"/> in arrival
    /// order. A session whose transport dies mid-loop (the reconnect's
    /// forced teardown) ends the loop without breaking the rig.
    /// </summary>
    /// <param name="captured">The rig's transcript queue.</param>
    public async Task PumpReceiveLoopAsync(ConcurrentQueue<string> captured)
    {
        var buffer = new byte[ReceiveChunkBytes];
        var pending = new StringBuilder();
        try
        {
            while (true)
            {
                var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.CloseStatus.HasValue)
                {
                    await _socket.CloseAsync(
                        result.CloseStatus.Value,
                        result.CloseStatusDescription,
                        CancellationToken.None);
                    return;
                }

                pending.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage)
                {
                    continue;
                }

                captured.Enqueue(pending.ToString());
                pending.Clear();
            }
        }
        catch (OperationCanceledException)
        {
            // The adapter's drop: the session is over.
        }
        catch (WebSocketException)
        {
            // The transport died mid-loop (forced teardown); the session ends.
        }
    }

    #endregion
}
