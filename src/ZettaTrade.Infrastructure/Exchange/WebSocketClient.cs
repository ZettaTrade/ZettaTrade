namespace ZettaTrade.Infrastructure.Exchange;

using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

/// <summary>
/// WebSocket client with automatic reconnection and reactive message streams.
/// <see cref="ConnectAsync"/> returns once the initial connection is established;
/// the receive/reconnect loop then keeps running in the background until
/// <see cref="DisconnectAsync"/> or disposal.
/// </summary>
public sealed class WebSocketClient : IAsyncDisposable
{
    /// <summary>Size of a single WebSocket receive chunk in bytes.</summary>
    private const int ReceiveChunkSize = 8192;

    #region Properties

    private readonly ILogger _logger;
    private readonly string _url;
    private readonly TimeSpan _reconnectDelay;
    private readonly int _maxReconnectAttempts;

    private readonly Subject<string> _messageSubject = new();
    private readonly BehaviorSubject<bool> _connectionStateSubject = new(false);

    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    /// <summary>
    /// Serializes the whole connect/disconnect lifecycle, so overlapping
    /// calls can never race the session state (BUG-029): a replaced session
    /// is cancelled, its loop awaited and its socket disposed BEFORE any new
    /// session is built.
    /// </summary>
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    private int _reconnectAttempts;
    private int _disposed;
    private bool _isConnected;

    /// <summary>Whether the WebSocket is currently connected.</summary>
    public bool IsConnected => _isConnected && _webSocket?.State == WebSocketState.Open;

    /// <summary>Observable stream of received raw messages.</summary>
    public IObservable<string> MessageStream => _messageSubject.AsObservable();

    /// <summary>Observable stream of connection state changes.</summary>
    public IObservable<bool> ConnectionStateStream => _connectionStateSubject.AsObservable();

    #endregion

    #region Constructor

    /// <summary>
    /// Create a new WebSocketClient instance.
    /// </summary>
    public WebSocketClient(
        string url,
        ILogger logger,
        TimeSpan? reconnectDelay = null,
        int maxReconnectAttempts = 5)
    {
        _url = url;
        _logger = logger;
        _reconnectDelay = reconnectDelay ?? TimeSpan.FromSeconds(5);
        _maxReconnectAttempts = maxReconnectAttempts;
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Connect to the WebSocket server, retrying up to <paramref name="maxConnectAttempts"/>
    /// times (configured in the constructor). The open phase honours <paramref name="ct"/>:
    /// a cancelled caller aborts the in-flight connect. Once the connection is open, the
    /// background receive/reconnect loop runs on the session token owned here — callers may
    /// only own their token for the length of the call (a stream start), while the session
    /// must live until <see cref="DisconnectAsync"/> or disposal.
    /// Calls are serialized: overlapping connects tear the PREVIOUS session down
    /// orderly (cancel → the loop exits → CTS/socket reaped) instead of racing the
    /// shared state (BUG-029), and every new session starts with a fresh attempt
    /// budget, so a failed previous session can never shrink it.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        await _lifecycleGate.WaitAsync(ct);
        try
        {
            // The replace-session teardown: the previous session is cancelled
            // and reaped BEFORE this caller's own session is built, so no
            // loop ever runs through a token or socket swap (BUG-029).
            await TearDownSessionAsync();

            var sessionCts = new CancellationTokenSource();
            _cts = sessionCts;
            var session = sessionCts.Token;
            var loopStarted = false;

            try
            {
                // A fresh session resets the budget: the failed window of a
                // previous session must not consume this caller's attempts.
                _reconnectAttempts = 0;

                while (!ct.IsCancellationRequested && _reconnectAttempts < _maxReconnectAttempts)
                {
                    try
                    {
                        await OpenSocketAsync(ct);
                        _logger.LogInformation("WebSocket connected to {Url}", _url);

                        // The loop below catches every non-cancellation
                        // failure internally, so no exception escapes unobserved.
                        _loopTask = Task.Run(() => RunBackgroundLoopAsync(session), CancellationToken.None);
                        loopStarted = true;
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _reconnectAttempts++;
                        _isConnected = false;
                        _connectionStateSubject.OnNext(false);

                        _logger.LogWarning(
                            ex,
                            "WebSocket connection failed (attempt {Attempt}/{MaxAttempts}).",
                            _reconnectAttempts,
                            _maxReconnectAttempts);

                        if (_reconnectAttempts < _maxReconnectAttempts)
                            await Task.Delay(_reconnectDelay, ct);
                    }
                }

                if (ct.IsCancellationRequested)
                    return;

                throw new InvalidOperationException(
                    $"Failed to connect to {_url} after {_maxReconnectAttempts} attempts.");
            }
            finally
            {
                // A session that never started a loop owns no long-lived work:
                // its token is released eagerly, keeping the CTS lifecycle
                // deterministic (a leftover half-session never leaks a token).
                if (!loopStarted)
                {
                    if (ReferenceEquals(_cts, sessionCts))
                    {
                        _cts = null;
                    }

                    sessionCts.Dispose();
                }
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// Disconnect from the WebSocket server and stop the background reconnect
    /// loop. The session is cancelled FIRST — the receive loop's
    /// cancellation-aware awaits exit promptly — so the socket is closed and
    /// disposed only after the loop has left it, never underneath a blocked
    /// <c>ReceiveAsync</c> (which surfaced as a spurious "background loop
    /// crashed" error, BUG-030).
    /// </summary>
    public async Task DisconnectAsync()
    {
        await _lifecycleGate.WaitAsync(CancellationToken.None);
        try
        {
            await TearDownSessionAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// Send a message through the WebSocket.
    /// </summary>
    public async Task SendAsync(string message, CancellationToken ct = default)
    {
        // Capture the reference so a concurrent reconnect cannot null it mid-send.
        var socket = _webSocket;
        if (socket is null || socket.State != WebSocketState.Open)
            throw new InvalidOperationException("WebSocket is not connected");

        var bytes = Encoding.UTF8.GetBytes(message);
        await socket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            true,
            ct);
    }

    /// <summary>
    /// Send a JSON-serialized object through the WebSocket.
    /// </summary>
    public async Task SendJsonAsync<T>(T obj, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(obj);
        await SendAsync(json, ct);
    }

    #endregion

    #region Private Methods

    private async Task OpenSocketAsync(CancellationToken ct)
    {
        _webSocket?.Dispose();
        _webSocket = new ClientWebSocket();

        await _webSocket.ConnectAsync(new Uri(_url), ct);
        _isConnected = true;
        _reconnectAttempts = 0;
        _connectionStateSubject.OnNext(true);
    }

    /// <summary>
    /// Tear the current session down in dependency order: cancel the session
    /// token (the receive loop's cancellation-aware awaits exit promptly so
    /// the socket is never closed underneath a blocked loop — BUG-030), await
    /// the catch-all loop, close and dispose the socket, then reap the CTS.
    /// Runs under <see cref="_lifecycleGate"/> only.
    /// </summary>
    private async Task TearDownSessionAsync()
    {
        var cts = _cts;
        cts?.Cancel();

        var hadSession = cts is not null || _loopTask is not null || _webSocket is not null;

        if (_loopTask is { } loop)
        {
            // The loop never faults (it catches every failure), but awaiting
            // a completed task that faulted would rethrow — guard regardless.
            try
            {
                await loop;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WebSocket loop task ended with an error");
            }

            _loopTask = null;
        }

        var socket = _webSocket;
        _webSocket = null;

        if (socket?.State == WebSocketState.Open)
        {
            try
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Client disconnecting",
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error closing WebSocket");
            }
        }

        socket?.Dispose();

        cts?.Dispose();
        _cts = null;

        if (hadSession)
        {
            // Only a REAL session flips the state: a no-op teardown (the
            // first connect) must never paint a second disconnected event.
            _isConnected = false;
            _connectionStateSubject.OnNext(false);
        }
    }

    private async Task RunBackgroundLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await ReceiveLoopAsync(ct);

                if (ct.IsCancellationRequested)
                    break;

                _logger.LogWarning(
                    "WebSocket disconnected; reconnecting in {Delay}...",
                    _reconnectDelay);

                try
                {
                    await Task.Delay(_reconnectDelay, ct);
                    await OpenSocketAsync(ct);
                    _logger.LogInformation("WebSocket reconnected to {Url}", _url);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _isConnected = false;
                    _logger.LogWarning(ex, "WebSocket reconnect failed; retrying");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Session teardown: cancellation is the loop's normal exit.
        }
        catch (Exception ex)
        {
            // Belt and braces: the background loop must never fault.
            _logger.LogError(ex, "WebSocket background loop crashed");
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[ReceiveChunkSize];

        // UTF-8 sequences may span fragment boundaries: accumulate bytes, decode once.
        using var messageBuffer = new MemoryStream();

        while (!ct.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
        {
            try
            {
                var result = await _webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("WebSocket closed by server");
                    break;
                }

                messageBuffer.Write(buffer, 0, result.Count);

                if (result.EndOfMessage)
                {
                    var message = Encoding.UTF8.GetString(
                        messageBuffer.GetBuffer(),
                        0,
                        (int)messageBuffer.Length);

                    messageBuffer.SetLength(0);

                    try
                    {
                        _messageSubject.OnNext(message);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error emitting WebSocket message");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (WebSocketException ex)
            {
                _logger.LogWarning(ex, "WebSocket error during receive");
                break;
            }
        }

        _isConnected = false;
        _connectionStateSubject.OnNext(false);
    }

    #endregion

    #region IAsyncDisposable

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        await DisconnectAsync();

        _messageSubject.OnCompleted();
        _messageSubject.Dispose();

        _connectionStateSubject.OnCompleted();
        _connectionStateSubject.Dispose();

        _lifecycleGate.Dispose();
    }

    #endregion
}
