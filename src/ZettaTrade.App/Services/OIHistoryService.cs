namespace ZettaTrade.App.Services;

using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// The open-interest history's one front (T-241): the chart's OI pane
/// resolves the venue's settled snapshots through this service, never
/// through the adapter directly. The service resolves the ACTIVE adapter
/// the same facade the chart's bars ride, probes its OI-history capability
/// (an adapter without the endpoint answers the honest no-capability null
/// — the pane just does not publish), and keeps a bounded, stale-aware
/// per-(symbol, interval) cache so every delivered page, prepend and
/// re-publish is cache-first: a window the cache covers serves without a
/// network round-trip, a wider or stale window refetches the venue's page
/// (the endpoint's own 200-snapshot cap bounds one fetch — the pane shows
/// honest gaps beyond it, never a fabricated stretch).
/// </summary>
public sealed class OIHistoryService
{
    #region Constants

    /// <summary>The per-(symbol, interval) snapshot cap one fetch asks for (the venue's own page bound).</summary>
    private const int FetchLimit = 200;

    /// <summary>How long a cached page answers without a refetch (the pane is an overview, not a firehose).</summary>
    private static readonly TimeSpan CacheFreshness = TimeSpan.FromMinutes(15);

    /// <summary>The cache's entry bound (evict the oldest fetch when exceeded — a session flips far fewer windows than this).</summary>
    internal const int MaxCacheEntries = 32;

    #endregion Constants

    #region Private Fields

    private readonly ActiveExchangeService _activeExchanges;
    private readonly TimeProvider _clock;

    /// <summary>
    /// The cache's one gate (the house semaphore): the reads and the writes
    /// serialize through it; no section inside holds it across an await.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    #endregion Private Fields

    #region Constructors

    /// <summary>Create the front over the active-exchange facade and the system clock.</summary>
    public OIHistoryService(ActiveExchangeService activeExchanges, TimeProvider clock)
    {
        _activeExchanges = activeExchanges ?? throw new ArgumentNullException(nameof(activeExchanges));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    #endregion Constructors

    #region Public Methods

    /// <summary>
    /// Fetch the settled open-interest snapshots of a perpetual symbol over
    /// the window at the bar interval, ascending by time, cache-first.
    /// </summary>
    /// <returns>The snapshots; NOTHING when the active exchange carries no
    /// OI-history capability (the caller's honest no-pane), never an empty
    /// stand-in for a capable venue.</returns>
    /// <exception cref="InvalidOperationException">The venue refused the request (a protocol-level fault the caller logs as the pane's degrade lane).</exception>
    public async Task<IReadOnlyList<OpenInterestPoint>?> GetAsync(
        Symbol symbol,
        TimeFrame interval,
        DateTimeOffset from,
        DateTimeOffset? to = null,
        CancellationToken ct = default)
    {
        // A non-perpetual symbol has no open-interest history at any venue:
        // the honest no-pane answers BEFORE the adapter snapshot (the
        // chart's kicks ride every delivered page, spot picks included —
        // the front answers without a fetch, never a refused provider
        // call).
        if (!symbol.IsPerpetual)
        {
            return null;
        }

        // The facade's Current adapter is the request's snapshot: the probe,
        // the cache key and the fetch ride the same adapter (a switch
        // mid-request belongs to the next call, never half of this one).
        var current = _activeExchanges.Current;
        if (current is not IOIHistoryProvider provider)
        {
            return null;
        }

        var key = $"{current.Id}|{symbol.Normalized}|{interval}";
        var now = _clock.GetUtcNow();
        var windowEnd = to ?? now;

        IReadOnlyList<OpenInterestPoint>? cached;
        await _gate.WaitAsync(ct);
        try
        {
            cached = TryReadCache(key, from, windowEnd, now);
        }
        finally
        {
            _gate.Release();
        }

        if (cached is { } points)
        {
            return points;
        }

        var fetched = await provider.GetOpenInterestHistoryAsync(symbol, interval, from, windowEnd, FetchLimit, ct);

        await _gate.WaitAsync(ct);
        try
        {
            StoreCache(key, fetched, from, windowEnd, now);
        }
        finally
        {
            _gate.Release();
        }

        return fetched;
    }

    /// <summary>Whether the ACTIVE exchange carries the OI-history capability (the chart's kick gate — no fetch for a spot-only venue).</summary>
    public bool IsSupported => _activeExchanges.Current is IOIHistoryProvider;

    #endregion Public Methods

    #region Private Methods

    /// <summary>Read a cache entry that covers the window and is still fresh; the covered-but-stale entry does NOT answer (the pane refetches). Runs under the held gate.</summary>
    private IReadOnlyList<OpenInterestPoint>? TryReadCache(
        string key,
        DateTimeOffset from,
        DateTimeOffset to,
        DateTimeOffset now)
    {
        if (!_cache.TryGetValue(key, out var entry))
        {
            return null;
        }

        return entry.FromUtc <= from && to <= entry.ToUtc && now - entry.FetchedUtc < CacheFreshness
            ? entry.Points
            : null;
    }

    /// <summary>Store one fetched page; a full cache evicts its oldest fetch (bounded, never unbounded growth). Runs under the held gate.</summary>
    private void StoreCache(
        string key,
        IReadOnlyList<OpenInterestPoint> points,
        DateTimeOffset from,
        DateTimeOffset to,
        DateTimeOffset now)
    {
        if (_cache.Count >= MaxCacheEntries && !_cache.ContainsKey(key))
        {
            var oldestKey = _cache.Keys
                .OrderBy(candidate => _cache[candidate].FetchedUtc, System.Collections.Generic.Comparer<DateTimeOffset>.Default)
                .First();
            _cache.Remove(oldestKey);
        }

        _cache[key] = new CacheEntry(points, now, from, to);
    }

    #endregion Private Methods

    /// <summary>One cached page: the snapshots plus the window they cover and the moment they were fetched (the freshness read).</summary>
    private sealed record CacheEntry(
        IReadOnlyList<OpenInterestPoint> Points,
        DateTimeOffset FetchedUtc,
        DateTimeOffset FromUtc,
        DateTimeOffset ToUtc);
}
