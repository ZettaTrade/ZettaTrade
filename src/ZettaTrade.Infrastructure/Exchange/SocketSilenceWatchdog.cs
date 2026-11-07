namespace ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// The one-session silence watchdog (T-105): watches one open WebSocket
/// session and fires a violation action when the session has been SILENT —
/// no inbound message within the window — although the session is alive
/// enough to expect data. This is what heals a half-open socket: the TCP
/// connection stays established after a network cut (no FIN/RST ever arrives),
/// so <c>ReceiveAsync</c> would otherwise park forever
/// on a dead session watching an exchange that stopped pushing.
/// </summary>
/// <remarks>
/// Arming is the anti-false-positive guard: a session with no activity in
/// EITHER direction (a bare connected socket with nothing subscribed) never
/// fires — only a session that exchanged at least one frame can go legitimately
/// silent. Outbound frames only ever ARM the check (a half-open socket still
/// accepts sends into its TCP buffer, so outbound traffic can never refresh the
/// silence); inbound frames both arm and refresh. Liveness timestamps ride
/// <see cref="Interlocked.Exchange"/>/<see cref="Volatile.Read"/> over bare
/// longs — no lock, no semaphore (async code never blocks).
/// </remarks>
public sealed class SocketSilenceWatchdog : IAsyncDisposable
{
    #region Properties

    private readonly TimeSpan _silenceWindow;
    private readonly Action<TimeSpan> _onSilenceViolation;
    private readonly PeriodicTimer _probeTimer;
    private readonly CancellationTokenSource _probeCts = new();
    private readonly Task _probeTask;

    /// <summary>Tick when the last inbound message arrived; seeded at construction, so a session that never receives heals too.</summary>
    private long _lastInboundTicks;

    /// <summary>Armed flag: 1 once any frame crossed the session in either direction.</summary>
    private int _armed;

    #endregion

    #region Constructor

    /// <summary>
    /// Create the watchdog and start its periodic probe. One instance per
    /// open session — the owning <see cref="WebSocketClient"/> creates it
    /// after every successful connect and disposes it before the reconnect.
    /// </summary>
    /// <param name="silenceWindow">
    /// Inbound silence that triggers the force-cycle. The shipped default
    /// covers the fastest streaming feeds by orders of magnitude (Binance
    /// depth pushes every 100 ms; Bybit answers its 20 s keepalive with a pong).
    /// </param>
    /// <param name="probeInterval">How often the silence is sampled; the smallest window granularity of the reaction.</param>
    /// <param name="onSilenceViolation">
    /// The violation action — the observed silence is passed through (the
    /// owning transport aborts the socket, which unblocks the parked receive
    /// and drives the usual reconnect path; the probe never touches the
    /// transport itself).
    /// </param>
    public SocketSilenceWatchdog(
        TimeSpan silenceWindow,
        TimeSpan probeInterval,
        Action<TimeSpan> onSilenceViolation)
    {
        if (silenceWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(silenceWindow),
                silenceWindow,
                "The silence window must be positive.");
        }

        if (probeInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(probeInterval),
                probeInterval,
                "The probe interval must be positive.");
        }

        _onSilenceViolation = onSilenceViolation
            ?? throw new ArgumentNullException(nameof(onSilenceViolation));
        _silenceWindow = silenceWindow;
        _probeTimer = new PeriodicTimer(probeInterval);
        _lastInboundTicks = DateTimeOffset.UtcNow.UtcTicks;
        _probeTask = ProbeAsync(_probeCts.Token);
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Report one inbound message: refreshes the silence clock and arms the
    /// watchdog (once data flowed, silence is meaningful).
    /// </summary>
    public void NotifyInbound()
    {
        Volatile.Write(ref _armed, 1);
        Interlocked.Exchange(ref _lastInboundTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    /// <summary>
    /// Report one outbound frame: arms the watch so a session that subscribed
    /// but never received anything (the freshest half-open socket) still heals.
    /// Deliberately does NOT refresh the silence clock — a half-open socket
    /// happily accepts sends into its TCP buffer.
    /// </summary>
    public void NotifyOutbound() => Volatile.Write(ref _armed, 1);

    #endregion

    #region Private Methods

    private async Task ProbeAsync(CancellationToken ct)
    {
        try
        {
            while (await _probeTimer.WaitForNextTickAsync(ct))
            {
                if (Volatile.Read(ref _armed) != 1)
                {
                    continue;
                }

                var silence = TimeSpan.FromTicks(
                    DateTimeOffset.UtcNow.UtcTicks - Volatile.Read(ref _lastInboundTicks));
                if (silence <= _silenceWindow)
                {
                    continue;
                }

                _onSilenceViolation(silence);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // Session teardown: cancellation is the probe's normal exit.
        }
    }

    #endregion

    #region IAsyncDisposable

    /// <summary>
    /// Stop the probe. The probe task is tracked from construction and
    /// awaited here — never abandoned.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _probeCts.Cancel();
        try
        {
            await _probeTask;
        }
        catch (OperationCanceledException)
        {
            // The probe's normal exit.
        }

        _probeTimer.Dispose();
        _probeCts.Dispose();
    }

    #endregion
}
