namespace ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// Phase of one adapter's transport connection as the reconnect supervisor
/// sees it (the vocabulary the T-098 health strip renders).
/// </summary>
public enum ReconnectionPhase
{
    /// <summary>No open session: the initial connect exhausted its budget or an explicit disconnect tore the session down.</summary>
    Disconnected,

    /// <summary>The initial connect loop is running.</summary>
    Connecting,

    /// <summary>A session is open and streaming.</summary>
    Connected,

    /// <summary>Waiting on the computed backoff before the next reconnect attempt.</summary>
    Reconnecting,

    /// <summary>The watchdog aborted a silent socket; the reconnect follows.</summary>
    ForceCycling,
}

/// <summary>
/// One transport-side reconnect event as published by a
/// <see cref="WebSocketClient"/> (the minimal shapes the supervisor folds into
/// its per-adapter state).
/// </summary>
/// <param name="Phase">The phase the transport entered.</param>
/// <param name="OccurredUtc">UTC moment of the transition.</param>
/// <param name="ConsecutiveFailures">
/// Failed open attempts since the last success (0 on every success, monotonic
/// through a failing streak).
/// </param>
/// <param name="BackoffDelay">The computed wait behind this transition (null when one does not apply).</param>
public sealed record ReconnectReport(
    ReconnectionPhase Phase,
    DateTimeOffset OccurredUtc,
    int ConsecutiveFailures,
    TimeSpan? BackoffDelay);

/// <summary>
/// The supervisor's per-adapter snapshot (what the T-098 health strip will
/// consume): the transport phase with the failure streak, the last transition
/// moment and the policy object the adapter reported.
/// </summary>
/// <param name="AdapterId">The adapter identity the events bind to (the adapter's <see cref="ExchangeAdapterBase.Id"/>).</param>
/// <param name="Phase">The last reported phase.</param>
/// <param name="ConsecutiveFailures">Failed open attempts since the last success.</param>
/// <param name="UpdatedUtc">UTC moment of the last applied report.</param>
/// <param name="Policy">The reconnect policy the adapter registered.</param>
public sealed record AdapterConnectionState(
    string AdapterId,
    ReconnectionPhase Phase,
    int ConsecutiveFailures,
    DateTimeOffset UpdatedUtc,
    ReconnectPolicy Policy);

/// <summary>
/// The state seam a transport publishes through: thread-observable pipes with
/// every method synchronous and non-awaiting (never a held-during-await
/// critical section — the trading-safety doctrine against locks in async).
/// </summary>
public interface IReconnectStateSink
{
    /// <summary>
    /// Publish one transport-side reconnect event. Never throws and never
    /// blocks (an unbounded, best-effort hand-off to the supervisor).
    /// </summary>
    void Publish(ReconnectReport report);
}
