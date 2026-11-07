namespace ZettaTrade.Infrastructure.Tests.RateLimiting;

using System.Collections.Concurrent;
using System.Reactive.Subjects;
using System.Threading;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// An in-memory <see cref="ISettingsService"/> for limiter facts (T-106):
/// values live in a dictionary, and <see cref="SetAsync"/> emits the same
/// reactive change record the real service does, so the limiter's live
/// reshape path runs against the real stream shape.
/// </summary>
internal sealed class TestSettingsService : ISettingsService, IDisposable
{
    private readonly ConcurrentDictionary<KeyId, object?> _values = new();

    private readonly Subject<SettingsChange> _changes = new();

    private int _reads;

    /// <summary>
    /// The total <c>GetAsync</c> reads issued so far: the hydration-once fact
    /// reads it (concurrent first acquires share one pass, so the count holds
    /// steady at the pass's size across later acquires).
    /// </summary>
    public int Reads => Interlocked.CompareExchange(ref _reads, 0, 0);

    /// <inheritdoc/>
    public IObservable<SettingsChange> Changes => _changes;

    /// <inheritdoc/>
    public async Task<T> GetAsync<T>(string section, string key, T defaultValue = default!)
    {
        Interlocked.Increment(ref _reads);
        await Task.Yield();

        return _values.TryGetValue(new KeyId(section, key), out var stored) && stored is T typed
            ? typed
            : defaultValue;
    }

    /// <inheritdoc/>
    public async Task SetAsync<T>(string section, string key, T value)
    {
        await Task.Yield();

        var identity = new KeyId(section, key);
        var previous = _values.TryGetValue(identity, out var old) ? old : null;
        _values[identity] = value;
        _changes.OnNext(new SettingsChange(section, key, previous, value));
    }

    /// <summary>The limiter facts never need persistence; the save is a no-op awaitable.</summary>
    public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>The limiter facts never reload from disk; the reload is a no-op awaitable.</summary>
    public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Dispose the change subject (the limiter's subscription drops cleanly).</summary>
    public void Dispose() => _changes.Dispose();

    /// <summary>The settings key identity: dictionary tuples would box; a record keys the dictionary exactly.</summary>
    private readonly record struct KeyId(string Section, string Key);
}
