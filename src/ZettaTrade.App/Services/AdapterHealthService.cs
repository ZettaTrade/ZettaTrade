namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// The status bar's adapter health fold (T-098): membership from the
/// active-exchange facade (the registered adapter catalog) folded with the
/// central reconnect supervisor's per-adapter transport stream into a hot
/// per-adapter health snapshot stream the strip renders.
/// </summary>
/// <remarks>
/// Pure observable composition — no polling, no timers: the supervisor's
/// <see cref="ReconnectSupervisor.StateStream"/> arrives already pumped and
/// hot, so the fold only maps phases into the strip vocabulary and re-rolls
/// the membership-ordered row list on every transport state update. The
/// chain reads the supervisor's <see cref="ReconnectSupervisor.Snapshot"/>
/// at subscription time inside <see cref="Observable.Defer"/> — a late
/// subscriber bootstraps with the states the supervisor already holds
/// instead of waiting for the next transport report. Each subscription
/// keeps its own fold state (the <see cref="Observable.Scan"/> accumulator),
/// so a re-subscription starts clean. The single known seam gap rides with
/// the supervisor's read-then-subscribe shape: a report published between
/// the snapshot fetch and the stream subscription is seen only with the
/// adapter's next report (the window is the subscription's own microseconds,
/// and every state flip heals its row).
/// </remarks>
public sealed class AdapterHealthService
{
    #region Properties

    private readonly ActiveExchangeService _exchanges;
    private readonly ReconnectSupervisor _supervisor;

    /// <summary>
    /// The strip's source: one full membership-ordered health list per
    /// transport state update, seeded with the supervisor's current
    /// snapshot for a fresh subscriber.
    /// </summary>
    public IObservable<IReadOnlyList<AdapterHealth>> HealthStream { get; }

    #endregion Properties

    #region Constructor

    /// <summary>
    /// Compose the fold over the facade's adapter catalog and the
    /// supervisor's transport states. Membership is fixed for the app
    /// lifetime (the DI composition registers the adapters once); the fold
    /// subscribes whenever the strip needs it, never before the facade is
    /// resolved (the registration order pins that).
    /// </summary>
    public AdapterHealthService(ActiveExchangeService exchanges, ReconnectSupervisor supervisor)
    {
        _exchanges = exchanges ?? throw new ArgumentNullException(nameof(exchanges));
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));

        HealthStream = Observable
            .Defer(() => _supervisor
                .StateStream
                .StartWith(_supervisor.Snapshot)
                .Scan(
                    new Dictionary<string, AdapterConnectionState>(StringComparer.Ordinal),
                    (states, update) =>
                    {
                        // Diagnostic roots only: a transport report for an id
                        // the supervisor never registered is dropped by the
                        // supervisor's own pump before it reaches the stream.
                        states[update.AdapterId] = update;
                        return states;
                    }))
            .Select(states => _exchanges
                .Registered
                .Select(adapter => CreateHealth(adapter, states))
                .ToArray());
    }

    #endregion Constructor

    #region Methods

    /// <summary>
    /// Map one supervisor transport phase into the strip's vocabulary: an
    /// open session is connected, every in-cycle phase (the initial connect
    /// loop, the backoff wait, the watchdog force-cycle) is reconnecting and
    /// the disconnected phase is down. An out-of-vocabulary phase is an
    /// error — never a silently substituted default.
    /// </summary>
    /// <param name="phase">The supervisor's transport phase.</param>
    /// <returns>The strip state.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A phase the strip vocabulary does not carry.</exception>
    internal static AdapterHealthState MapPhase(ReconnectionPhase phase) => phase switch
    {
        ReconnectionPhase.Connected => AdapterHealthState.Connected,
        ReconnectionPhase.Connecting => AdapterHealthState.Reconnecting,
        ReconnectionPhase.Reconnecting => AdapterHealthState.Reconnecting,
        ReconnectionPhase.ForceCycling => AdapterHealthState.Reconnecting,
        ReconnectionPhase.Disconnected => AdapterHealthState.Down,
        _ => throw new ArgumentOutOfRangeException(
            nameof(phase),
            phase,
            "The reconnect phase has no health strip mapping."),
    };

    /// <summary>
    /// Fold one membership entry with the fold's state dictionary: a bound
    /// adapter maps its supervisor state, an unbound one (no supervised
    /// transport — the demo's canned-data lane) renders unsupervised with
    /// no event moment and no error counter.
    /// </summary>
    private static AdapterHealth CreateHealth(
        IMarketDataAdapter adapter,
        IReadOnlyDictionary<string, AdapterConnectionState> states)
    {
        if (!states.TryGetValue(adapter.Id, out var state))
        {
            return new AdapterHealth(
                adapter.Id,
                adapter.Name,
                AdapterHealthState.Unsupervised,
                LastEventUtc: null,
                ConsecutiveFailures: 0);
        }

        return new AdapterHealth(
            adapter.Id,
            adapter.Name,
            MapPhase(state.Phase),
            state.UpdatedUtc,
            state.ConsecutiveFailures);
    }

    #endregion Methods
}
