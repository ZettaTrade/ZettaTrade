namespace ZettaTrade.Backtest.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// The in-memory candle cache the backtest facts feed: the exact T-085
/// interface over a dictionary of series, with optional paging and a
/// deterministic hold gate so the run-load and walk-back stay test-drivable.
/// Series seed through <see cref="WriteThroughAsync"/> (the cache's own
/// write-through surface), reads serve the stored order.
/// </summary>
public sealed class TestCandleCache : ICandleCache
{
    /// <summary>The series the reads serve, keyed by identity.</summary>
    private readonly Dictionary<(string AdapterId, string Symbol, TimeFrame Interval), List<Bar>> _series = new();

    /// <summary>The gate the first <see cref="GetLatestAsync"/> waits on (a run-load hold).</summary>
    public TaskCompletionSource LoadHold { get; private set; } = CreateHold(hold: false);

    /// <summary>
    /// Signalled when the first newest read arrives (before the hold gate
    /// answers): the handshake that pins WHEN a run has the harness's
    /// single-flight gate — the service reads the cache only after it holds
    /// the gate, so the test gates its assertions on this signal.
    /// </summary>
    private readonly TaskCompletionSource _newestReadArrival = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The handshake task: one run's first newest read has arrived.</summary>
    public Task WhenNewestReadArrives => _newestReadArrival.Task;

    /// <summary>The newest page's size (trimmed to the series' own count).</summary>
    public int NewestPageSize { get; set; } = int.MaxValue;

    /// <summary>How many newest reads have arrived (the sweep's load-once pin: the window loads one time, not per row).</summary>
    public int NewestReadCount { get; private set; }

    /// <summary>The size of every older page (the cache's own stride).</summary>
    public int OlderPageSize { get; set; } = int.MaxValue;

    private static TaskCompletionSource CreateHold(bool hold)
    {
        var holdTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!hold)
        {
            holdTask.SetResult();
        }

        return holdTask;
    }

    /// <summary>Arm a hold: the first newest read waits until the test releases it.</summary>
    public void HoldNewestRead() => LoadHold = CreateHold(hold: true);

    /// <summary>Release any armed hold.</summary>
    public void ReleaseHold() => LoadHold.TrySetResult();

    public Task<IReadOnlyList<Bar>> GetLatestAsync(
        string adapterId,
        Symbol symbol,
        TimeFrame timeFrame,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (!_series.TryGetValue((adapterId, symbol.Normalized, timeFrame), out var bars))
        {
            return Task.FromResult<IReadOnlyList<Bar>>([]);
        }

        return NewestPageAsync(bars, Math.Min(limit, bars.Count), cancellationToken);
    }

    private async Task<IReadOnlyList<Bar>> NewestPageAsync(List<Bar> bars, int limit, CancellationToken cancellationToken)
    {
        _newestReadArrival.TrySetResult();
        NewestReadCount++;
        await LoadHold.Task.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        // The newest bars (the page cap applies, but the window still ENDS
        // at the series' newest bar — never at an arbitrary interior start).
        var take = Math.Min(limit, NewestPageSize);
        var start = Math.Max(0, bars.Count - take);
        var page = new List<Bar>(take);
        for (var index = start; index < bars.Count; index++)
        {
            page.Add(bars[index]);
        }

        return page;
    }

    public Task<IReadOnlyList<Bar>> GetOlderThanAsync(
        string adapterId,
        Symbol symbol,
        TimeFrame timeFrame,
        DateTimeOffset before,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (!_series.TryGetValue((adapterId, symbol.Normalized, timeFrame), out var bars))
        {
            return Task.FromResult<IReadOnlyList<Bar>>([]);
        }

        // The real cache serves the NEWEST bars strictly older than the
        // boundary, oldest first — the walk-back's contiguous window.
        var page = new List<Bar>();
        for (var index = bars.Count - 1; index >= 0 && page.Count < Math.Min(OlderPageSize, limit); index--)
        {
            if (bars[index].Time < before)
            {
                page.Add(bars[index]);
            }
        }

        page.Sort(static (left, right) => left.Time.CompareTo(right.Time));
        return Task.FromResult<IReadOnlyList<Bar>>(page);
    }

    public Task WriteThroughAsync(
        string adapterId,
        Symbol symbol,
        TimeFrame timeFrame,
        IReadOnlyList<Bar> bars,
        CancellationToken cancellationToken = default)
    {
        if (!_series.TryGetValue((adapterId, symbol.Normalized, timeFrame), out var stored))
        {
            stored = [];
            _series[(adapterId, symbol.Normalized, timeFrame)] = stored;
        }

        foreach (var bar in bars)
        {
            stored.RemoveAll(existing => existing.Time == bar.Time);
            stored.Add(bar);
        }

        stored.Sort(static (left, right) => left.Time.CompareTo(right.Time));
        return Task.CompletedTask;
    }
}
