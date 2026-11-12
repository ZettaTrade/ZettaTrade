namespace ZettaTrade.App.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Livery.Contracts;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.UI.Charting;
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// The heatmap feed (T-121): the adapter's tradable universe fetched once
/// through the active-exchange facade and capped to a bounded candidate
/// window, ONE ticker pump per candidate, the candidates' latest tick state
/// coalesced into ONE ranked snapshot per panel pulse — a wire ticker storm
/// stays inside the service while the panel sees one ranked tile page.
/// <para>
/// The candidate window: Core's <c>IMarketDataAdapter</c> offers no
/// all-pairs ticker stream, so ranking honestly covers only what the feed
/// can observe. The window takes the adapter's symbol list in the adapter's
/// own order up to <see cref="CandidateBudget"/> subscriptions, and the
/// samples rank by the report's weight — the largest weight owns the
/// largest tile. A pair with no tick yet (or a non-positive weight under the
/// metric: zero volume, a zero change) owns no area and no row in the
/// snapshot — nothing fabricated for missing data.
/// </para>
/// <para>
/// An exchange switch re-fetches the universe (the <c>AdapterChanges</c>
/// kick rides the ticket-based <c>Observable.FromAsync</c> + epoch guard,
/// the symbol palette source's pattern), which drops the previous
/// exchange's ticker state entirely — a stale pair never rides a snapshot
/// under a new range's name.
/// </para>
/// <remarks>
/// State transitions stay lock-free: every candidate owns one immutable
/// <see cref="CandidateTick"/> swapped in with a single atomic reference
/// write; the pulse builder only reads those. The optional <c>pulse</c>
/// seam injects a controllable interval source for the deterministic facts
/// (the default is the plain Rx interval clock — exists per-byte for the
/// test seam, nothing speculative).
/// </remarks>
public sealed class HeatmapFeedService : IAsyncInitializable
{
    #region Constants

    /// <summary>
    /// The subscription budget: how many candidate tick pumps the feed arms
    /// at most — the panel ranks the pairs this window observed, bounded so
    /// a large venue's catalog cannot blow the subscription count.
    /// </summary>
    public const int CandidateBudget = 48;

    /// <summary>The panel cadence: full ranked snapshots at this rate only.</summary>
    private static readonly TimeSpan PulseInterval = TimeSpan.FromMilliseconds(500);

    #endregion Constants

    #region Private Fields

    /// <summary>The live candidate set, keyed by the normalized symbol text.</summary>
    private readonly ConcurrentDictionary<string, CandidateState> _candidates = new(StringComparer.Ordinal);

    /// <summary>One ticker pump per live candidate; swapped atomically by a refresh.</summary>
    private IDisposable? _candidateSubscriptions;

    /// <summary>The raw ticker arrival signal the pulse samples, not shipped directly.</summary>
    private readonly Subject<RxVoid> _dataTick = new();

    /// <summary>
    /// The metric the weights map by; the pulse honors the flip on the next
    /// snapshot (the ranking re-plans at the panel's own cadence — a metric
    /// flip never storms a re-plan either). Written by the panel's UI
    /// thread, read by the pulse path: assignment visibility is the worst a
    /// torn-free reference read races, and a one-pulse-late flip is the
    /// honest outcome, not a defect.
    /// </summary>
    private volatile HeatmapMetric _metric = HeatmapMetric.QuoteVolume24h;

    /// <summary>The adapter change subscription re-fetching the universe per switch.</summary>
    private readonly IDisposable? _switchSubscription;

    /// <summary>The epoch counter invalidating a refresh racing another.</summary>
    private int _epoch;

    /// <summary>Serialization gate for the universe refreshes.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The facade: the universe fetch and every candidate pump ride.</summary>
    private readonly ActiveExchangeService _activeExchanges;

    /// <summary>The feed's own logging lane.</summary>
    private readonly ILogger<HeatmapFeedService> _logger;

    /// <summary>The pulse source: the default Rx interval clock or the test seam.</summary>
    private readonly IObservable<long> _pulse;

    #endregion Private Fields

    #region Properties

    /// <summary>
    /// The panel's tile feed: one coalesced ranked snapshot per pulse — the
    /// candidates whose weight carries area, largest first (ties resolve by
    /// the normalized symbol text, so a re-plan stays deterministic). An
    /// empty page (nothing ticked yet, every weight zero) still ships so
    /// the panel renders its empty state instead of a stale surface.
    /// </summary>
    public IObservable<IReadOnlyList<HeatmapTile>> Snapshots { get; }

    /// <summary>
    /// The metric the tile weights map by. The panel writes the selection;
    /// the pulse applies it on the next snapshot.
    /// </summary>
    public HeatmapMetric Metric
    {
        get => _metric;
        set => _metric = value;
    }

    #endregion Properties

    #region Constructor

    /// <summary>
    /// Create the feed: compose the coalesced snapshot stream over the pulse
    /// and arm the adapter-change re-fetch. The candidate window itself arms
    /// in <see cref="InitializeAsync"/> (the startup walk owns the warm-up
    /// fetch) — never a blocking constructor network call.
    /// </summary>
    public HeatmapFeedService(
        ActiveExchangeService activeExchanges,
        ILogger<HeatmapFeedService> logger,
        IObservable<long>? pulse = null)
    {
        _activeExchanges = activeExchanges ?? throw new ArgumentNullException(nameof(activeExchanges));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _pulse = pulse ?? Observable.Interval(PulseInterval);

        Snapshots = _dataTick
            .StartWith(RxVoid.Default)
            .Sample(_pulse)
            .Select(_ => BuildSnapshot());

        // An exchange switch re-fetches the universe (the palette source's
        // kick pattern): a new adapter's pairs are what the map ranks, and
        // nothing of the old exchange's ticker state may ride along. The
        // behavioral stream's seed is skipped — subscribing is not a switch;
        // the warm-up owns the first composition. The epoch bumps BEFORE the
        // kick: an in-flight fetch of the previous composition is dropped at
        // its finish (its adapters list belongs to a superseded exchange).
        _switchSubscription = _activeExchanges
            .AdapterChanges
            .Skip(1)
            .Subscribe(
                _ =>
                {
                    Interlocked.Increment(ref _epoch);
                    KickRefresh();
                },
                ex => _logger.LogError(ex, "The heatmap's adapter-change stream stopped unexpectedly"));
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>
    /// The warm-up fetch: arm the candidate window once without blocking the
    /// startup walk. A failure only means the empty state until an adapter
    /// change re-composes.
    /// </summary>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        // The warm-up intentionally ignores the startup token: the fetch
        // outlives initialization (it is the panel's session source) and an
        // abandoned half-fetch on shutdown is harmless.
        Observable
            .FromAsync(_ => RefreshOnceAsync(CancellationToken.None))
            .Subscribe(
                _ => { },
                ex => _logger.LogWarning(ex, "Heatmap universe warm-up failed; the next adapter change re-fetches"));

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stop the adapter-change subscription, the candidate pumps and the
    /// pulse sink (the app-lifetime singleton usually outlives the process,
    /// but the pose stays honest). The gate outlives this call on purpose:
    /// an in-flight refresh finishing after the stop is an abandoned
    /// half-composition on shutdown — harmless, never spun until disposed.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        _switchSubscription?.Dispose();
        _candidateSubscriptions?.Dispose();
        _candidateSubscriptions = null;
        _candidates.Clear();
        _dataTick.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Kick a universe refresh: the ticket-based FromAsync keeps the kick
    /// serialized behind the running one, never a bare fire-and-forget task.
    /// </summary>
    private void KickRefresh()
    {
        Observable
            .FromAsync(_ => RefreshOnceAsync(CancellationToken.None))
            .Subscribe(
                _ => { },
                ex => _logger.LogWarning(ex, "Heatmap universe refresh failed; the next adapter change re-tries"));
    }

    /// <summary>
    /// One refresh attempt: take the active adapter's symbol list, swap the
    /// candidate window and its pumps atomically (the previous exchange's
    /// ticker state dies with the old window), then publish the empty page —
    /// a switch renders the honest empty state immediately and lets the new
    /// candidate tickers fill it. A stale attempt (a newer switch arrived in
    /// flight) is dropped at its finish: nothing of the superseded
    /// composition publishes.
    /// </summary>
    /// <remarks>
    /// Public on purpose: the subscription paths only kick it and guard
    /// their own errors, and the tests await it for a deterministic settle.
    /// </remarks>
    public async Task RefreshOnceAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var epochAtFetch = Volatile.Read(ref _epoch);

            _candidateSubscriptions?.Dispose();
            _candidateSubscriptions = null;
            _candidates.Clear();

            IReadOnlyList<Symbol> symbols;
            try
            {
                symbols = await _activeExchanges.GetSymbolsAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Heatmap: the universe fetch failed; the map stays empty until a re-fetch");
                _dataTick.OnNext(RxVoid.Default);
                return;
            }

            if (Volatile.Read(ref _epoch) != epochAtFetch)
            {
                return;
            }

            var pumpGroup = new CompositeDisposable();
            foreach (var symbol in symbols.Take(CandidateBudget))
            {
                var candidate = new CandidateState(symbol);
                if (_candidates.TryAdd(symbol.Normalized, candidate))
                {
                    pumpGroup.Add(SubscribeCandidate(candidate));
                }
            }

            _candidateSubscriptions = pumpGroup;

            // The empty-but-shipping page: the panel switches to the honest
            // empty state the moment a swap lands, never one pulse later.
            _dataTick.OnNext(RxVoid.Default);
        }
        finally
        {
            _gate.Release();
        }
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>
    /// Start one candidate's ticker pump: every tick lands in the
    /// candidate's own atomic state swap (the handler owns the growth — Rx
    /// serializes each stream's notifications). A stream failure leaves the
    /// candidate on its last served data (the honest value it already had)
    /// and the facade's switch re-arms the window anyway.
    /// </summary>
    private IDisposable SubscribeCandidate(CandidateState candidate)
    {
        try
        {
            var pumpSource = _activeExchanges.SubscribeTicker(candidate.Symbol);
            return pumpSource.Subscribe(
                ticker =>
                {
                    candidate.Apply(new CandidateTick(
                        ticker.LastPrice,
                        ticker.Volume24h,
                        ticker.PriceChange24hPercent));
                    _dataTick.OnNext(RxVoid.Default);
                },
                error => _logger.LogWarning(
                    error,
                    "Heatmap: ticker stream for {Symbol} failed; the candidate keeps its last served data",
                    candidate.SymbolText));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Heatmap: no ticker available for {Symbol}; the candidate stays unpriced",
                candidate.SymbolText);
            return Disposable.Empty;
        }
    }

    /// <summary>
    /// Build the ranked page: every ticked candidate's weight under the
    /// current metric, positive weights only, largest first (ties by the
    /// normalized text, ordinal). The tile carries the change percent for
    /// the palette and its own weight for the layout.
    /// </summary>
    private IReadOnlyList<HeatmapTile> BuildSnapshot()
    {
        var metric = _metric;
        var ranked = _candidates
            .Values
            .Select(candidate => candidate.ToTile(metric))
            .Where(tile => tile.Weight is { } weight && weight > 0m)
            .OrderByDescending(tile => tile.Weight!.Value)
            .ThenBy(tile => tile.SymbolText, StringComparer.Ordinal)
            .ToList();
        return ranked;
    }

    #endregion Private Methods

    #region Candidate State

    /// <summary>
    /// One candidate pair's self-coherent state: the ticker handler swaps
    /// one immutable tick snapshot in; the pulse builder only reads it. The
    /// single atomic write keeps price, volume and change consistent in
    /// every snapshot.
    /// </summary>
    private sealed class CandidateState(Symbol symbol)
    {
        private CandidateTick? _latest;

        /// <summary>The candidate's normalized symbol text.</summary>
        public string SymbolText => Symbol.Normalized;

        /// <summary>The candidate's symbol the ticker pump rides.</summary>
        public Symbol Symbol { get; } = symbol;

        /// <summary>The candidate's newest complete state, or null until it ticks.</summary>
        public CandidateTick? Latest => Volatile.Read(ref _latest);

        /// <summary>Swap in the candidate's newest snapshot (one atomic write).</summary>
        public void Apply(CandidateTick tick) => Volatile.Write(ref _latest, tick);

        /// <summary>The candidate's tile for the current snapshot (null weight = no area).</summary>
        public HeatmapTile ToTile(HeatmapMetric metric)
        {
            var latest = Latest;
            if (latest is not { } tick)
            {
                return new HeatmapTile(SymbolText, null, null);
            }

            var weight = metric switch
            {
                HeatmapMetric.Change24hPercent => tick.Change24hPercent is { } change
                    ? (change > 0m ? change : -change)
                    : null,
                _ => (decimal?)(tick.LastPrice * tick.Volume24h),
            };

            return new HeatmapTile(SymbolText, tick.Change24hPercent, weight);
        }
    }

    /// <summary>
    /// One ticker arrival's full candidate state (immutable, swapped atomically).
    /// </summary>
    private sealed record CandidateTick(
        decimal LastPrice,
        decimal Volume24h,
        decimal? Change24hPercent);

    #endregion Candidate State
}
