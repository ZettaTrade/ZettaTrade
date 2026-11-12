namespace ZettaTrade.Infrastructure.Tests.Exchange;

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
/// An in-process loopback WebSocket server (Kestrel) for transport e2e tests:
/// tests connect the real <c>WebSocketClient</c>/<c>BinanceAdapter</c> stack
/// to it over 127.0.0.1 and drive handshake, framing, close and reconnect
/// scenarios without any real host.
/// </summary>
/// <remarks>
/// The port is pre-reserved with a <see cref="TcpListener"/> on :0 and released
/// immediately, so nothing depends on Kestrel reporting dynamically bound
/// addresses. The window between release and the Kestrel bind is tiny
/// (loopback test host), and a lost race fails hard (address in use), never
/// silently — <see cref="StartAsync"/> retries on a few freshly reserved
/// ports to keep even that deterministic.
/// </remarks>
public sealed class LoopbackWebSocketServer : IAsyncDisposable
{
    #region Fields

    /// <summary>Interval between checks of the received-frame predicate while waiting.</summary>
    private static readonly TimeSpan FramePollInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>How many freshly reserved ports <see cref="StartAsync"/> tries on bind contention.</summary>
    private const int StartBindRetries = 3;

    private readonly ConcurrentQueue<string> _receivedFrames = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<LoopbackConnection>> _connectionWaiters = new();
    private int _connectionTotal;

    private WebApplication? _app;

    #endregion

    #region Properties

    /// <summary>Loopback port the server listens on.</summary>
    public int Port { get; private set; }

    /// <summary>
    /// Combined-stream WebSocket URL (the path mirrors the production
    /// subscription endpoint the adapter connects to).
    /// </summary>
    public string CombinedStreamUrl => $"ws://127.0.0.1:{Port}/stream";

    /// <summary>How many WebSocket sessions were accepted in total (reconnect evidence).</summary>
    public int ConnectionCount => Volatile.Read(ref _connectionTotal);

    #endregion

    #region Methods: Factory

    /// <summary>
    /// Start the server on a loopback port and make it accept WebSocket
    /// sessions. Use <see cref="DisposeAsync"/> to stop it.
    /// </summary>
    public static async Task<LoopbackWebSocketServer> StartAsync(CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            var port = ReserveLoopbackPort();
            var server = new LoopbackWebSocketServer();
            try
            {
                await server.StartOnPortAsync(port, ct);
                return server;
            }
            catch (IOException ex) when (attempt < StartBindRetries - 1 && IsAddressInUse(ex))
            {
                // Pre-reserved port lost to an unrelated binder: re-reserve and retry.
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
        app.Map("/stream", HandleWebSocketRequestAsync);

        // Subpath dials (T-193): the Binance trading shell composes the
        // private-session URL as {PrivateWsUrl}/{listenKey}, so any subpath
        // under /stream (and /ws) is a dialable session route.
        app.Map("/stream/{*remainder}", HandleWebSocketRequestAsync);
        app.Map("/ws/{*remainder}", HandleWebSocketRequestAsync);

        await app.StartAsync(ct);

        _app = app;
        Port = port;
    }

    /// <summary>
    /// Per-connection request handler: accept the WebSocket upgrade, complete
    /// the session-number waiter and run the receive loop until the connection
    /// ends (peer close, abort or server shutdown).
    /// </summary>
    private async Task HandleWebSocketRequestAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var socket = await context.WebSockets.AcceptWebSocketAsync();
        var connection = new LoopbackConnection(Interlocked.Increment(ref _connectionTotal), socket);
        _connectionWaiters
            .GetOrAdd(
                connection.Index,
                _ => new TaskCompletionSource<LoopbackConnection>(TaskCreationOptions.RunContinuationsAsynchronously))
            .TrySetResult(connection);

        await RunReceiveLoopAsync(connection, socket, context.RequestAborted);
    }

    #endregion

    #region Methods: Waiting

    /// <summary>
    /// Fetch an already-accepted session by its 1-based number, or wait for
    /// its accept. Works both before and after the session is established.
    /// </summary>
    public async Task<LoopbackConnection> WaitForConnectionAsync(int sessionNumber, TimeSpan timeout)
    {
        var waiter = _connectionWaiters.GetOrAdd(
            sessionNumber,
            _ => new TaskCompletionSource<LoopbackConnection>(TaskCreationOptions.RunContinuationsAsynchronously));

        return await waiter.Task.WaitAsync(timeout);
    }

    /// <summary>
    /// Wait until a received frame matches the predicate, then return it
    /// (a bounded poll with a timeout; polls are decision-free, not sleeps).
    /// </summary>
    public async Task<string> WaitForFrameAsync(Func<string, bool> predicate, TimeSpan timeout)
    {
        if (TryFindFrame(predicate, out var found))
        {
            return found;
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                await Task.Delay(FramePollInterval, timeoutCts.Token);
                if (TryFindFrame(predicate, out found))
                {
                    return found;
                }
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException("No received frame matched the predicate within the timeout.");
        }
    }

    private bool TryFindFrame(Func<string, bool> predicate, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? frame)
    {
        foreach (var candidate in _receivedFrames)
        {
            if (predicate(candidate))
            {
                frame = candidate;
                return true;
            }
        }

        frame = null;
        return false;
    }

    /// <summary>A thread-safe snapshot of every frame received so far, in arrival order.</summary>
    public IReadOnlyList<string> SnapshotFrames() => _receivedFrames.ToList();

    #endregion

    #region Methods: Connection handling

    /// <summary>
    /// Decode whole messages (mirroring the client's chunk accumulation) and
    /// queue them for the tests; ends on a peer close frame, a hard drop or
    /// the server shutdown.
    /// </summary>
    private async Task RunReceiveLoopAsync(LoopbackConnection connection, WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var messageBuffer = new MemoryStream();

        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (WebSocketException)
                {
                    // The peer dropped hard; this session simply ends.
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                messageBuffer.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                {
                    continue;
                }

                var frame = Encoding.UTF8.GetString(messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length);
                messageBuffer.SetLength(0);
                _receivedFrames.Enqueue(frame);
            }
        }
        finally
        {
            connection.MarkClosed();
            try
            {
                socket.Dispose();
            }
            catch (WebSocketException)
            {
                // An already-aborted socket only ever fails its disposal.
            }
        }
    }

    #endregion

    #region IAsyncDisposable

    public async ValueTask DisposeAsync()
    {
        if (_app is not { } app)
        {
            return;
        }

        try
        {
            await app.StopAsync();
        }
        finally
        {
            await app.DisposeAsync();
            _app = null;
        }
    }

    #endregion
}

/// <summary>
/// One accepted WebSocket session as seen by the test: push frames, split
/// frames manually (fragmentation pins), close the server side gracefully.
/// </summary>
public sealed class LoopbackConnection
{
    #region Fields

    private readonly WebSocket _socket;
    private int _closed;

    #endregion

    #region Constructor / Properties

    internal LoopbackConnection(int index, WebSocket socket)
    {
        Index = index;
        _socket = socket;
    }

    /// <summary>1-based arrival order of the session (reconnect evidence).</summary>
    public int Index { get; }

    /// <summary>Whether the receive loop for this session has ended.</summary>
    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    #endregion

    #region Methods

    /// <summary>Push a complete text frame to the client.</summary>
    public async Task SendTextAsync(string frame, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(frame);
        await _socket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            ct);
    }

    /// <summary>
    /// Push one text frame split into two WebSocket fragments at the given
    /// UTF-8 byte boundary — used to pin the client's chunk/UTF-8 reassembly.
    /// </summary>
    public async Task SendTextFragmentedAsync(string frame, int splitByteIndex, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(frame);
        if (splitByteIndex <= 0 || splitByteIndex >= bytes.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(splitByteIndex));
        }

        await _socket.SendAsync(
            new ArraySegment<byte>(bytes, 0, splitByteIndex),
            WebSocketMessageType.Text,
            endOfMessage: false,
            ct);
        await _socket.SendAsync(
            new ArraySegment<byte>(bytes, splitByteIndex, bytes.Length - splitByteIndex),
            WebSocketMessageType.Text,
            endOfMessage: true,
            ct);
    }

    /// <summary>
    /// Begin the closing handshake from the server side (the client receives a
    /// Close message and reacts; this end does not wait for the reply).
    /// </summary>
    public async Task CloseGracefullyAsync(CancellationToken ct = default)
    {
        await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "server closing", ct);
    }

    /// <summary>Called by the receive loop when the session ends.</summary>
    internal void MarkClosed() => Interlocked.Exchange(ref _closed, 1);

    #endregion
}
