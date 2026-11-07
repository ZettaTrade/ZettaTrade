namespace ZettaTrade.Infrastructure.Exchange;

using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

/// <summary>
/// Central reconnect supervisor (T-105): one service that holds every
/// adapter's explicit <see cref="ReconnectPolicy"/> and folds the transports'
/// reported state transitions into per-adapter
/// <see cref="AdapterConnectionState"/> snapshots — the surface the T-098
/// adapter health strip will consume later. The transports still behave
/// exactly as the per-adapter fixes (T-048, T-052) shaped them: the supervisor
/// only surfaces what they already do, it never drives them.
/// </summary>
/// <remarks>
/// Reports travel through an unbounded <see cref="Channel{T}"/> (never
/// fire-and-forget, never abandoned) and ONE pump task applies them strictly
/// sequentially — a single-threaded fold is what keeps the state reads and the
/// hot state stream free of the concurrent-notification hazards a raw
/// <c>Subject</c> publish would carry. <see cref="IAsyncDisposable"/> reaps the
/// pump; the DI host disposes the singleton at shutdown.
/// </remarks>
public sealed class ReconnectSupervisor : IAsyncDisposable
{
    #region Properties

    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, AdapterConnectionState> _states = new();
    private readonly Subject<AdapterConnectionState> _stateSubject = new();
    private readonly Channel<(string adapterId, ReconnectReport report)> _reports =
        Channel.CreateUnbounded<(string, ReconnectReport)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _pumpCts = new();
    private readonly Task _pumpTask;
    private int _disposed;

    /// <summary>Every registered adapter's current state, ordered by adapter id (a copy — callers cannot mutate the registry).</summary>
    public IReadOnlyList<AdapterConnectionState> Snapshot =>
        [.. _states.Values.OrderBy(state => state.AdapterId, StringComparer.Ordinal)];

    /// <summary>Hot stream of every applied per-adapter state (T-098's future subscription point).</summary>
    public IObservable<AdapterConnectionState> StateStream => _stateSubject.AsObservable();

    #endregion

    #region Constructor

    /// <summary>Create the supervisor and start its sequential report pump.</summary>
    public ReconnectSupervisor(ILogger<ReconnectSupervisor> logger)
    {
        _logger = logger;
        _pumpTask = Task.Run(() => ApplyReportsAsync(_pumpCts.Token), CancellationToken.None);
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Bind one adapter to the supervisor: the policy is registered (first
    /// registration wins — adapters construct once) and the returned sink is
    /// what that adapter's transport publishes its transitions through.
    /// Direct constructions without a supervisor keep working: an absent
    /// binding simply reports nothing.
    /// </summary>
    /// <param name="adapterId">The adapter identity (never blank).</param>
    /// <param name="policy">The adapter's explicit reconnect policy.</param>
    /// <returns>The adapter's state sink.</returns>
    /// <exception cref="ArgumentException">A blank adapter id.</exception>
    /// <exception cref="ArgumentNullException">A null policy.</exception>
    public IReconnectStateSink For(string adapterId, ReconnectPolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);
        ArgumentNullException.ThrowIfNull(policy);

        _states.TryAdd(
            adapterId,
            new AdapterConnectionState(
                adapterId,
                ReconnectionPhase.Disconnected,
                0,
                DateTimeOffset.UtcNow,
                policy));

        return new AdapterStateSink(this, adapterId);
    }

    /// <summary>Whether the adapter id carries a registered policy (wiring assertion helper for the tests).</summary>
    public bool IsRegistered(string adapterId) => _states.ContainsKey(adapterId);

    #endregion

    #region Private Methods

    private void Apply((string adapterId, ReconnectReport report) bound)
    {
        var (adapterId, report) = bound;
        if (!_states.TryGetValue(adapterId, out var current))
        {
            _logger.LogWarning(
                "Reconnect report for unknown adapter {AdapterId} dropped.",
                adapterId);
            return;
        }

        var updated = current with
        {
            Phase = report.Phase,
            ConsecutiveFailures = report.ConsecutiveFailures,
            UpdatedUtc = report.OccurredUtc,
        };
        _states[adapterId] = updated;

        try
        {
            _stateSubject.OnNext(updated);
        }
        catch (Exception ex)
        {
            // A throwing subscriber must never kill the pump: the fold keeps
            // applying, the broken stream is the subscriber's own problem.
            _logger.LogWarning(ex, "Reconnect state publication threw; the pump keeps applying reports.");
        }
    }

    private async Task ApplyReportsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var bound in _reports.Reader.ReadAllAsync(ct))
            {
                Apply(bound);
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal: the only exit of the report pump.
        }
        catch (Exception ex)
        {
            // Belt and braces: the pump must never fault unobserved.
            _logger.LogError(ex, "Reconnect supervisor pump crashed");
        }
    }

    #endregion

    #region Nested Types

    /// <summary>The adapter-bound sink: the binding happened once at <see cref="For"/>, reports ride the unbounded channel in publish order.</summary>
    private sealed class AdapterStateSink(
        ReconnectSupervisor owner,
        string adapterId) : IReconnectStateSink
    {
        public void Publish(ReconnectReport report)
        {
            // A non-blocking, never-failing hand-off: TryWrite on an
            // unbounded channel always succeeds until the supervisor is
            // disposed (then it is a no-op) — the publish contract holds.
            owner._reports.Writer.TryWrite((adapterId, report));
        }
    }

    #endregion

    #region IAsyncDisposable

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _pumpCts.Cancel();
        try
        {
            await _pumpTask;
        }
        catch (OperationCanceledException)
        {
            // The pump's normal exit.
        }

        _pumpCts.Dispose();
        _stateSubject.OnCompleted();
        _stateSubject.Dispose();
    }

    #endregion
}
