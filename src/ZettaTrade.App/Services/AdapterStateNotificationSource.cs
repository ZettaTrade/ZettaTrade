namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using System.Reactive.Disposables;
using System.Threading;
using System.Threading.Tasks;
using Livery.Contracts;
using Microsoft.Extensions.Logging;

/// <summary>
/// The notification source over the T-098 adapter health fold (T-097):
/// the fold's per-adapter snapshots diff into transport FLIPS, and every
/// meaningful flip announces itself — a supervised adapter entering the
/// reconnect cycle, one that went fully down, and one that recovered.
/// The unsupervised adapters (the canned-data demo lane carries no
/// supervised transport) never announce: no transport fact exists to
/// report (the no-fabrication rule), and the adapter-state mute/dnD
/// machinery still applies to every announcement it makes.
/// </summary>
/// <remarks>
/// The subscription starts through the startup walk
/// (<see cref="IAsyncInitializable"/>): the composition root orders this
/// source after the health fold, so its subscription sees the fold's
/// composed stream immediately (the same pattern the status bar's own
/// VM subscription keeps). The first snapshot per adapter is the
/// bootstrap state — it does not announce (the pre-report bound states
/// the fold seeds are NOT transport flips, announcing them would raise a
/// disconnected shout at every startup). Every later distinct state per
/// adapter is a real flip.
/// </remarks>
public sealed class AdapterStateNotificationSource : IAsyncInitializable, IDisposable
{
    #region Private Fields

    /// <summary>The per-adapter last-known states the diff reads against (complete on each fold emission).</summary>
    private readonly Dictionary<string, AdapterHealthState> _lastStates = new(StringComparer.Ordinal);

    private readonly AdapterHealthService _healthService;
    private readonly NotificationCenter _notifications;
    private readonly ILogger<AdapterStateNotificationSource> _logger;

    /// <summary>The fold subscription (resident for the app lifetime once the walk starts it).</summary>
    private IDisposable? _subscription;

    #endregion Private Fields

    #region Constructor

    /// <summary>Compose the fold→flip tap. Registration order supplies the consumers' wiring.</summary>
    public AdapterStateNotificationSource(
        AdapterHealthService healthService,
        NotificationCenter notifications,
        ILogger<AdapterStateNotificationSource> logger)
    {
        _healthService = healthService ?? throw new ArgumentNullException(nameof(healthService));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #endregion Constructor

    #region Initialization

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        if (_subscription is not null)
        {
            return Task.CompletedTask;
        }

        // The app-lifetime subscription: the source starts with the walk
        // and stays resident for the app (the status bar keeps the same
        // contract over the same composed stream — a fault would tear a
        // plain subscription, but the fold is a pure Rx composition with
        // no throwing step, so the stream never errors in practice).
        _subscription = _healthService.HealthStream.Subscribe(
            snapshot => OnSnapshot(snapshot),
            ex => _logger.LogError(ex, "The adapter-state notification stream stopped unexpectedly"));

        return Task.CompletedTask;
    }

    #endregion Initialization

    #region Flip Detection

    /// <summary>Diff one fold snapshot: announce each supervised adapter's state flip from the last known one.</summary>
    private void OnSnapshot(IReadOnlyList<AdapterHealth> snapshot)
    {
        foreach (var health in snapshot)
        {
            if (health.State == AdapterHealthState.Unsupervised)
            {
                // No transport, no flip: the demo lane stays silent and its
                // last-known entry clears (a binding the supervisor no
                // longer carries re-announces its FIRST real state again).
                _lastStates.Remove(health.AdapterId);
                continue;
            }

            var first = !_lastStates.TryGetValue(health.AdapterId, out var previousState);
            _lastStates[health.AdapterId] = health.State;
            if (first || previousState == health.State)
            {
                // The bootstrap state never announces; repeats don't either.
                continue;
            }

            _notifications.Publish(Notification.ForAdapterState(
                health.AdapterName,
                health.State,
                health.LastEventUtc ?? DateTimeOffset.UtcNow));
            _logger.LogInformation(
                "Adapter-state tap: {Adapter} flipped to {State}",
                health.AdapterName,
                health.State);
        }
    }

    #endregion Flip Detection

    #region IDisposable

    /// <inheritdoc/>
    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }

    #endregion IDisposable
}
