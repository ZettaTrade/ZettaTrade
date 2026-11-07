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
/// <see cref="DisconnectAsync"/> or disposal. The reconnect waits come from the
/// explicit <see cref="ReconnectPolicy"/> (exponential + bounded jitter + max
/// delay, T-105), and a per-session silence watchdog force-cycles a half-open
/// socket — a cut network never delivers FIN/RST, so the parked receive would
/// otherwise watch a dead session forever.
/// </summary>
public sealed class WebSocketClient : IAsyncDisposable
{
    /// <summary>Size of a single WebSocket receive chunk in bytes.</summary>
    private const int ReceiveChunkSize = 8192;

    /// <summary>Inbound silence after which the watchdog force-cycles the socket (heals inside the window; see the watchdog's arming rule).</summary>
    private static readonly TimeSpan DefaultSilenceWindow = TimeSpan.FromSeconds(DefaultSilenceWindowSeconds);

    /// <summary>The shipped default silence window, in seconds — fast streaming feeds are orders of magnitude under it.</summary>
    private const int DefaultSilenceWindowSeconds = 60;

    /// <summary>The probe samples the silence every window/N; too fine wastes ticks, too coarse adds reaction latency.</summary>
    private const int SilenceProbeWindowDivisor = 4;

    /// <summary>Smallest probe interval, in milliseconds — the reaction floor for the tiny test windows.</summary>
    private const int MinSilenceProbeMilliseconds = 100;

    /// <summary>Largest probe interval, in milliseconds — the worst added reaction latency for the long production windows.</summary>
    private const int MaxSilenceProbeMilliseconds = 2000;

    #region Properties

    private readonly ILogger _logger;
    private readonly string _url;
    private readonly ReconnectPolicy _policy;
    private readonly IReconnectStateSink? _stateSink;
    private readonly TimeSpan _silenceWindow;

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

    /// <summary>
    /// The ACTIVE session's silence watchdog: written right after the
    /// per-session construction and cleared on teardown — outbound sends arm
    /// through it (Volatile reads/writes only; the send path must stay
    /// lock-free and never block).
    /// </summary>
    private SocketSilenceWatchdog? _activeWatchdog;

    /// <summary>Whether the WebSocket is currently connected.</summary>
    public bool IsConnected => _isConnected && _webSocket?.State == WebSocketState.Open;

    /// <summary>Observable stream of received raw messages.</summary>
    public IObservable<string> MessageStream => _messageSubject.AsObservable();

    /// <summary>Observable stream of connection state changes.</summary>
    public IObservable<bool> ConnectionStateStream => _connectionStateSubject.AsObservable();

    #endregion

    #region Constructor

    /// <summary>
    /// Create a new WebSocketClient instance. The reconnect timing and the
    /// watchdog window come from the explicit policy knobs (no hidden
    /// constants): adapters pass their own <see cref="ReconnectPolicy"/> and
    /// state sink (the transport reports its transitions there, T-105); every
    /// knob keeps its shipped default so existing call sites compile and
    /// behave as before.
    /// </summary>
    public WebSocketClient(
        string url,
        ILogger logger,
        ReconnectPolicy? policy = null,
        IReconnectStateSink? stateSink = null,
        TimeSpan? silenceWindow = null)
    {
        _url = url;
        _logger = logger;
        _policy = policy ?? ReconnectPolicy.Default;
        _stateSink = stateSink;
        _silenceWindow = silenceWindow ?? DefaultSilenceWindow;
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
                Report(ReconnectionPhase.Connecting, 0, null);

                while (!ct.IsCancellationRequested && _reconnectAttempts < _policy.MaxAttempts)
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
                            _policy.MaxAttempts);

                        if (_reconnectAttempts < _policy.MaxAttempts)
                        {
                            var delay = _policy.ComputeDelay(_reconnectAttempts, Random.Shared.NextDouble());
                            Report(ReconnectionPhase.Reconnecting, _reconnectAttempts, delay);
                            await Task.Delay(delay, ct);
                        }
                    }
                }

                if (ct.IsCancellationRequested)
                    return;

                Report(ReconnectionPhase.Disconnected, _reconnectAttempts, null);
                throw new InvalidOperationException(
                    $"Failed to connect to {_url} after {_policy.MaxAttempts} attempts.");
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

        // Outbound frames arm the silence watchdog (see its arming rule) but
        // deliberately never refresh its silence clock.
        NotifyOutbound();

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
        Report(ReconnectionPhase.Connected, 0, null);
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
            Report(
                ReconnectionPhase.Disconnected,
                Volatile.Read(ref _reconnectAttempts),
                backoffDelay: null);
        }
    }

    private async Task RunBackgroundLoopAsync(CancellationToken ct)
    {
        try
        {
            // The reconnect attempt position of the CURRENT failing streak:
            // zero whenever a socket opened successfully, so every streak
            // restarts the exponential backoff from the policy's initial delay.
            var reconnectAttempt = 0;

            while (!ct.IsCancellationRequested)
            {
                // One watchdog per open session: created here, registered for
                // the outbound-arming path (sends bubble through the field in
                // Volatile scope), disposed BEFORE the reconnect step — the
                // probe never watches a dead socket or races a socket swap.
                var watchdog = CreateSilenceWatchdog();
                Volatile.Write(ref _activeWatchdog, watchdog);
                try
                {
                    await ReceiveLoopAsync(ct, watchdog);
                }
                finally
                {
                    Volatile.Write(ref _activeWatchdog, null);
                    await watchdog.DisposeAsync();
                }

                if (ct.IsCancellationRequested)
                    break;

                reconnectAttempt++;
                var delay = _policy.ComputeDelay(reconnectAttempt, Random.Shared.NextDouble());

                _logger.LogWarning(
                    "WebSocket disconnected; reconnecting in {Delay}...",
                    delay);
                Report(ReconnectionPhase.Reconnecting, reconnectAttempt, delay);

                try
                {
                    await Task.Delay(delay, ct);
                    await OpenSocketAsync(ct);
                    reconnectAttempt = 0;
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

    private async Task ReceiveLoopAsync(CancellationToken ct, SocketSilenceWatchdog watchdog)
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
                    // Armed-and-refreshed silence clock: data just crossed.
                    watchdog.NotifyInbound();

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

    /// <summary>
    /// The per-session <see cref="SocketSilenceWatchdog"/>: the shipped window
    /// and a probe derived from it (every Nth of the window, clamped to a
    /// sane reaction band). The violation action is the force-cycle below.
    /// </summary>
    private SocketSilenceWatchdog CreateSilenceWatchdog() => new(
        _silenceWindow,
        ProbeIntervalFor(_silenceWindow),
        CycleSilentSocket);

    /// <summary>Probe cadence for a window: window/N clamped into the reaction band (the floor keeps tiny test windows observable).</summary>
    private static TimeSpan ProbeIntervalFor(TimeSpan silenceWindow) => TimeSpan.FromMilliseconds(
        Math.Clamp(
            silenceWindow.TotalMilliseconds / SilenceProbeWindowDivisor,
            MinSilenceProbeMilliseconds,
            MaxSilenceProbeMilliseconds));

    /// <summary>
    /// The watchdog's violation action, running on the probe tick: force-cycle
    /// the silent socket — report the phase, then abort the dying TCP
    /// connection so the parked <c>ReceiveAsync</c> throws and the usual
    /// reconnect path (policy-computed backoff, re-subscribe burst) takes
    /// over. A half-open socket never heals by itself: this IS the heal.
    /// </summary>
    private void CycleSilentSocket(TimeSpan silence)
    {
        _logger.LogWarning(
            "WebSocket silent for {Silence} (window {Window}); the watchdog force-cycles the socket.",
            silence,
            _silenceWindow);
        Report(ReconnectionPhase.ForceCycling, Volatile.Read(ref _reconnectAttempts), silence);

        var socket = _webSocket;
        if (socket is null || socket.State != WebSocketState.Open)
        {
            return;
        }

        try
        {
            socket.Abort();
        }
        catch (Exception ex)
        {
            // A race with the session teardown (socket already disposed) is a
            // non-event: the cycle already happened through that exit.
            _logger.LogWarning(ex, "Watchdog socket abort raced the session teardown");
        }
    }

    /// <summary>
    /// Arm the active session's watchdog for an outbound frame (sends prove
    /// the session intended to exchange data — but never refresh its silence
    /// clock, see the watchdog's rule). A silently-racing session swap simply
    /// arms the stale instance, whose probes already end with the swap.
    /// </summary>
    private void NotifyOutbound()
    {
        if (Volatile.Read(ref _activeWatchdog) is { } watchdog)
        {
            watchdog.NotifyOutbound();
        }
    }

    /// <summary>
    /// Push one transport transition into the supervisor's sink (the T-098
    /// health strip's future source). A transport without a sink (direct
    /// constructions, all existing tests) reports nothing — zero drift.
    /// </summary>
    private void Report(ReconnectionPhase phase, int consecutiveFailures, TimeSpan? backoffDelay)
    {
        if (_stateSink is not { } sink)
        {
            return;
        }

        sink.Publish(new ReconnectReport(phase, DateTimeOffset.UtcNow, consecutiveFailures, backoffDelay));
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
