namespace ZettaTrade.Infrastructure.Exchange;

using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

/// <summary>
/// WebSocket client with automatic reconnection and reactive message streams.
/// </summary>
public sealed class WebSocketClient : IAsyncDisposable
{
    #region Properties

    private readonly ILogger _logger;
    private readonly string _url;
    private readonly TimeSpan _reconnectDelay;
    private readonly int _maxReconnectAttempts;

    private readonly Subject<string> _messageSubject = new();
    private readonly BehaviorSubject<bool> _connectionStateSubject = new(false);

    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private int _reconnectAttempts;
    private bool _disposed;
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
    /// Connect to the WebSocket server.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WebSocketClient));

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _reconnectAttempts = 0;

        await ConnectInternalAsync(_cts.Token);
    }

    /// <summary>
    /// Disconnect from the WebSocket server.
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
        if (!IsConnected)
            throw new InvalidOperationException("WebSocket is not connected");

        var bytes = Encoding.UTF8.GetBytes(message);
        await _webSocket!.SendAsync(
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

    private async Task ConnectInternalAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _reconnectAttempts < _maxReconnectAttempts)
        {
            try
            {
                _webSocket?.Dispose();
                _webSocket = new ClientWebSocket();

                await _webSocket.ConnectAsync(new Uri(_url), ct);
                _isConnected = true;
                _reconnectAttempts = 0;
                _connectionStateSubject.OnNext(true);

                _logger.LogInformation("WebSocket connected to {Url}", _url);

                _receiveTask = ReceiveLoopAsync(ct);
                await _receiveTask;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _isConnected = false;
                _connectionStateSubject.OnNext(false);
                _reconnectAttempts++;

                _logger.LogWarning(
                    ex,
                    "WebSocket connection failed (attempt {Attempt}/{MaxAttempts}). Reconnecting in {Delay}...",
                    _reconnectAttempts,
                    _maxReconnectAttempts,
                    _reconnectDelay);

                await Task.Delay(_reconnectDelay, ct);
            }
        }

        if (_reconnectAttempts >= _maxReconnectAttempts)
        {
            _logger.LogError("Max reconnection attempts reached. Giving up.");
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        var messageBuilder = new StringBuilder();

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

                messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                if (result.EndOfMessage)
                {
                    var message = messageBuilder.ToString();
                    messageBuilder.Clear();

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

    #region IDisposable

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        await DisconnectAsync();

        _messageSubject.OnCompleted();
        _messageSubject.Dispose();

        _connectionStateSubject.OnCompleted();
        _connectionStateSubject.Dispose();

        _cts?.Dispose();
    }

    #endregion
}
