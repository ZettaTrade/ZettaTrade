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
    /// Connect to the WebSocket server. Retries up to <paramref name="maxConnectAttempts"/> tries
    /// (configured in the constructor) and yields the reconnect loop to the background.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        // A new session cancels any previous one.
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;

        while (!token.IsCancellationRequested && _reconnectAttempts < _maxReconnectAttempts)
        {
            try
            {
                await OpenSocketAsync(token);
                _logger.LogInformation("WebSocket connected to {Url}", _url);
                StartLoop(token);
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
                    await Task.Delay(_reconnectDelay, token);
            }
        }

        if (token.IsCancellationRequested)
            return;

        if (_reconnectAttempts >= _maxReconnectAttempts)
            throw new InvalidOperationException(
                $"Failed to connect to {_url} after {_maxReconnectAttempts} attempts.");
    }

    /// <summary>
    /// Disconnect from the WebSocket server and stop the background reconnect loop.
    /// </summary>
    public async Task DisconnectAsync()
    {
        _cts?.Cancel();

        if (_webSocket?.State == WebSocketState.Open)
        {
            try
            {
                await _webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Client disconnecting",
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error closing WebSocket");
            }
        }

        _webSocket?.Dispose();
        _webSocket = null;
        _isConnected = false;
        _connectionStateSubject.OnNext(false);
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

    private void StartLoop(CancellationToken ct)
    {
        // The loop below catches every non-cancellation failure internally,
        // so no exception escapes unobserved.
        _loopTask = Task.Run(() => RunBackgroundLoopAsync(ct), CancellationToken.None);
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

        _cts?.Dispose();
    }

    #endregion
}
