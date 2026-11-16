namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// The leverage brackets' one front (T-243): the liquidation-level estimate
/// resolves the venue's public risk table through this service, never
/// through the adapter directly. The service resolves the ACTIVE adapter
/// the same facade the chart's bars and the OI pane ride, probes its
/// bracket capability (an adapter without the risk-limit endpoint answers
/// the honest no-capability null — no estimate is computable without a
/// bracket table), and keeps a bounded, stale-aware per-symbol cache so
/// every kick, re-kick and re-publish is cache-first: a table fetched
/// within the freshness window serves without a network round-trip, a
/// stale one refetches the venue's page.
/// <para>
/// The brackets change at the venue's risk-review cadence, not per tick —
/// the freshness window is deliberately the same quarter hour the OI
/// history front keeps. A spot symbol has no risk table at any venue: the
/// honest null answers BEFORE the adapter snapshot (the chart's kicks ride
/// every delivered page, spot picks included — the front answers without a
/// fetch, never a refused provider call).
/// </para>
/// </summary>
public sealed class LeverageBracketsService
{
    #region Constants

    /// <summary>How long a cached table answers without a refetch (the tables move at the venue's risk-review cadence).</summary>
    private static readonly TimeSpan CacheFreshness = TimeSpan.FromMinutes(15);

    /// <summary>The cache's entry bound (evict the oldest fetch when exceeded — a session flips far fewer symbols than this).</summary>
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
    public LeverageBracketsService(ActiveExchangeService activeExchanges, TimeProvider clock)
    {
        _activeExchanges = activeExchanges ?? throw new ArgumentNullException(nameof(activeExchanges));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    #endregion Constructors

    #region Public Methods

    /// <summary>
    /// Fetch the venue's public leverage-bracket table of a perpetual
    /// symbol, cache-first.
    /// </summary>
    /// <returns>The brackets ascending by notional floor; NOTHING when the
    /// active exchange carries no bracket capability (the caller's honest
    /// no-estimate), never an empty stand-in for a capable venue.</returns>
    /// <exception cref="InvalidOperationException">The venue refused the request (a protocol-level fault the caller logs as the estimate's degrade lane).</exception>
    public async Task<IReadOnlyList<LeverageBracket>?> GetAsync(
        Symbol symbol,
        CancellationToken ct = default)
    {
        // A spot symbol has no risk table at any venue: the honest
        // no-estimate answers BEFORE the adapter snapshot (the chart's
        // kicks ride every delivered page, spot picks included — the front
        // answers without a fetch, never a refused provider call).
        if (!symbol.IsPerpetual)
        {
            return null;
        }

        // The facade's Current adapter is the request's snapshot: the probe,
        // the cache key and the fetch ride the same adapter (a switch
        // mid-request belongs to the next call, never half of this one).
        var current = _activeExchanges.Current;
        if (current is not ILeverageBracketsProvider provider)
        {
            return null;
        }

        var key = $"{current.Id}|{symbol.Normalized}";
        var now = _clock.GetUtcNow();

        IReadOnlyList<LeverageBracket>? cached;
        await _gate.WaitAsync(ct);
        try
        {
            cached = TryReadCache(key, now);
        }
        finally
        {
            _gate.Release();
        }

        if (cached is { } brackets)
        {
            return brackets;
        }

        var fetched = await provider.GetLeverageBracketsAsync(symbol, ct);

        await _gate.WaitAsync(ct);
        try
        {
            StoreCache(key, fetched, now);
        }
        finally
        {
            _gate.Release();
        }

        return fetched;
    }

    /// <summary>Whether the ACTIVE exchange carries the bracket capability (the estimate's kick gate — no fetch for a lane-less venue).</summary>
    public bool IsSupported => _activeExchanges.Current is ILeverageBracketsProvider;

    #endregion Public Methods

    #region Private Methods

    /// <summary>Read a cache entry that is still fresh; the stale entry does NOT answer (the front refetches). Runs under the held gate.</summary>
    private IReadOnlyList<LeverageBracket>? TryReadCache(string key, DateTimeOffset now)
    {
        if (!_cache.TryGetValue(key, out var entry))
        {
            return null;
        }

        return now - entry.FetchedUtc < CacheFreshness ? entry.Brackets : null;
    }

    /// <summary>Store one fetched table; a full cache evicts its oldest fetch (bounded, never unbounded growth). Runs under the held gate.</summary>
    private void StoreCache(
        string key,
        IReadOnlyList<LeverageBracket> brackets,
        DateTimeOffset now)
    {
        if (_cache.Count >= MaxCacheEntries && !_cache.ContainsKey(key))
        {
            var oldestKey = _cache.Keys
                .OrderBy(candidate => _cache[candidate].FetchedUtc, System.Collections.Generic.Comparer<DateTimeOffset>.Default)
                .First();
            _cache.Remove(oldestKey);
        }

        _cache[key] = new CacheEntry(brackets, now);
    }

    #endregion Private Methods

    /// <summary>One cached table: the brackets plus the moment they were fetched (the freshness read).</summary>
    private sealed record CacheEntry(
        IReadOnlyList<LeverageBracket> Brackets,
        DateTimeOffset FetchedUtc);
}
