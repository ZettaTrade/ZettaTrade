namespace ZettaTrade.App.Services;

/// <summary>
/// Health state of one exchange adapter as the status bar's adapter health
/// strip (T-098) renders it: the strip's own vocabulary mapped from the
/// reconnect supervisor's transport phases (the mapping is one-to-one per
/// state class — see <see cref="AdapterHealthService.MapPhase"/>).
/// </summary>
public enum AdapterHealthState
{
    /// <summary>The supervised transport reports an open, streaming session.</summary>
    Connected,

    /// <summary>The supervised transport is inside a reconnect cycle (an initial connect loop, a backoff wait or a watchdog force-cycle).</summary>
    Reconnecting,

    /// <summary>The supervised transport reported no open session (the initial connect budget exhausted or an explicit disconnect tore the session down).</summary>
    Down,

    /// <summary>The adapter carries no supervised transport (it never bound the reconnect supervisor, like the canned-data demo adapter) — no transport health exists to render.</summary>
    Unsupervised,
}

/// <summary>
/// One adapter's folded health snapshot: the membership's identity (the
/// active-exchange facade supplies id and display name) plus the
/// supervisor's last transport state (the mapped strip state, the moment of
/// the last applied report and the consecutive-failure counter the
/// supervisor already tracks).
/// </summary>
/// <param name="AdapterId">The adapter identity the health binds to (the adapter's <see cref="Core.Interfaces.IMarketDataAdapter.Id"/>).</param>
/// <param name="AdapterName">The adapter's display name (the tooltip's opening member).</param>
/// <param name="State">The strip state mapped from the adapter's last transport phase.</param>
/// <param name="LastEventUtc">
/// UTC moment of the last applied transport report; null only for
/// <see cref="AdapterHealthState.Unsupervised"/> (a never-reported adapter
/// has no transport event to timestamp).
/// </param>
/// <param name="ConsecutiveFailures">Failed open attempts since the last success (0 when the session is eligible as fresh).</param>
public sealed record AdapterHealth(
    string AdapterId,
    string AdapterName,
    AdapterHealthState State,
    DateTimeOffset? LastEventUtc,
    int ConsecutiveFailures);
