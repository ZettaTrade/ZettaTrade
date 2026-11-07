namespace ZettaTrade.Infrastructure.Tests.RateLimiting;

using System.Collections.Concurrent;

/// <summary>
/// A manually driven clock for limiter facts (T-106): the injected
/// <see cref="TimeProvider"/>'s clock marches only when a fact advances it,
/// so the drain/refill and park spans hold exactly, without a single real
/// sleep. <c>Task.Delay(_, provider, _)</c> rides <see cref="CreateTimer"/>,
/// and the timers fire when the advance crosses their due stamp.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    /// <summary>The stamp the clock carries until an advance moves it.</summary>
    private static readonly DateTimeOffset StartStamp = new(2026, 11, 7, 12, 0, 0, TimeSpan.FromHours(0));
    private DateTimeOffset _now = StartStamp;
    private readonly ConcurrentDictionary<int, ManualTimer> _timers = new();
    private int _nextTimerId;

    /// <summary>Create a clock stamping the shipped alpha cut until advanced.</summary>
    public ManualTimeProvider()
    {
    }

    /// <inheritdoc/>
    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>Move the clock forward and fire every timer whose due stamp the advance crosses (in due order).</summary>
    /// <param name="span">The time span to sweep.</param>
    public void Advance(TimeSpan span)
    {
        var target = _now + span;
        _now = target;

        var due = _timers
            .Values
            .Where(timer => timer.DueAt is { } dueAt && dueAt <= target)
            .OrderBy(timer => timer.DueAt)
            .ToList();

        foreach (var timer in due)
        {
            if (!_timers.TryGetValue(timer.Id, out var live) || !ReferenceEquals(live, timer))
            {
                continue; // Disposed or replaced between the snapshot and the fire.
            }

            if (timer.Period is { } period && period > TimeSpan.Zero)
            {
                timer.DueAt = timer.DueAt + period;
                timer.Fire();
            }
            else
            {
                _timers.TryRemove(timer.Id, out _);
                timer.DueAt = null;
                timer.Fire();
            }
        }
    }

    /// <summary>Jump the clock straight to a stamp (the drift-free twin of a chained advance).</summary>
    /// <param name="stamp">The stamp to present.</param>
    public void Set(DateTimeOffset stamp) => _now = stamp;

    /// <inheritdoc/>
    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var id = Interlocked.Increment(ref _nextTimerId);
        var timer = new ManualTimer(
            this,
            id,
            callback,
            state,
            NormalizeDue(dueTime) is { } due ? _now + due : null,
            period < TimeSpan.Zero ? null : period);
        _timers[id] = timer;
        return timer;
    }

    /// <summary>Normalized due: the .NET <c>-1</c> infinite idiom, presented as no due at all.</summary>
    private static TimeSpan? NormalizeDue(TimeSpan dueTime) =>
        dueTime < TimeSpan.Zero ? null : dueTime;

    /// <summary>
    /// One provider timer: fires its callback inline (the limiter's own
    /// continuations complete the waiting acquire inside the advance chain),
    /// on a <see cref="Task.Delay"/> cadence of a single due stamp. Re-stars
    /// ride the provider's clock the same way the initial due does.
    /// </summary>
    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _provider;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private int _disposed;

        /// <summary>Bind the timer's identity, callback and cadence to the drive.</summary>
        public ManualTimer(
            ManualTimeProvider provider,
            int id,
            TimerCallback callback,
            object? state,
            DateTimeOffset? dueAt,
            TimeSpan? period)
        {
            _provider = provider;
            Id = id;
            _callback = callback;
            _state = state;
            DueAt = dueAt;
            Period = period;
        }

        /// <summary>The provider-assigned identity.</summary>
        public int Id { get; }

        /// <summary>The absolute stamp this timer fires at (null: never).</summary>
        public DateTimeOffset? DueAt { get; set; }

        /// <summary>The repeating period, or null for a single-shot timer.</summary>
        public TimeSpan? Period { get; set; }

        /// <inheritdoc/>
        public bool HasStarted => DueAt is not null;

        /// <summary>Invoke the bound callback now (the provider's advance chain).</summary>
        public void Fire() => _callback(_state);

        /// <inheritdoc/>
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            DueAt = NormalizeDue(dueTime) is { } due ? _provider.GetUtcNow() + due : null;
            Period = period < TimeSpan.Zero ? null : period;
            return true;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            DueAt = null;
        }

        /// <inheritdoc/>
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
