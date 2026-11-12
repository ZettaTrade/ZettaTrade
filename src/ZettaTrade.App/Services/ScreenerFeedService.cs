namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Livery.Contracts;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.ValueObjects;
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// The screener feed (T-195): the adapter's tradable universe fetched once
/// through the active-exchange facade and capped to a bounded candidate
/// window, ONE ticker pump per candidate, the candidates' latest tick state
/// coalesced into ONE page per panel pulse — a wire ticker storm stays
/// inside the service while the panel sees one table page.
/// <para>
/// The candidate window: Core's <c>IMarketDataAdapter</c> offers no
/// all-pairs ticker stream, so the screen honestly covers only what the
/// feed can observe. The window takes the adapter's symbol list in the
/// adapter's own order up to <see cref="CandidateBudget"/> subscriptions,
/// and the page carries the ticked candidates in that same order — the
/// ranking (the presets and the column sorts) is the panel's own concern,
/// never the feed's.
/// </para>
/// <para>
/// An exchange switch re-fetches the universe (the <c>AdapterChanges</c>
/// kick rides the ticket-based <c>Observable.FromAsync</c> + epoch guard,
/// the heatmap feed's pattern), which drops the previous exchange's ticker
/// state entirely — a stale pair never rides a page under a new range's
/// name.
/// </para>
/// <para>
/// The page states degrade honestly: a faulting (or absent) universe fetch
/// ships <see cref="ScreenerFeedState.CatalogUnavailable"/> — the panel
/// renders its reason line instead of a broken table — and an armed window
/// with no tick yet ships <see cref="ScreenerFeedState.NoTicksYet"/> until
/// the first ticker lands.
/// </para>
/// <remarks>
/// State transitions stay lock-free: every candidate owns one immutable
/// <see cref="CandidateTick"/> swapped in with a single atomic reference
/// write, and the candidate window itself is an immutable list swapped by a
/// single atomic reference write (the refresh populates it fully before the
/// swap; the pump handlers only touch their own candidate). The optional
/// <c>pulse</c> seam injects a controllable interval source for the
/// deterministic facts (the default is the plain Rx interval clock — exists
/// per-byte for the test seam, nothing speculative).
/// </remarks>
public sealed class ScreenerFeedService : IAsyncInitializable
{
    #region Constants

    /// <summary>
    /// The subscription budget: how many candidate tick pumps the feed arms
    /// at most — the panel screens the pairs this window observed, bounded
    /// so a large venue's catalog cannot blow the subscription count.
    /// </summary>
    public const int CandidateBudget = 64;

    /// <summary>The panel cadence: full pages at this rate only.</summary>
    private static readonly TimeSpan PulseInterval = TimeSpan.FromMilliseconds(500);

    #endregion Constants

    #region Private Fields

    /// <summary>The live candidate window, in the adapter's own order.</summary>
    private List<CandidateState> _window = [];

    /// <summary>One ticker pump per live candidate; swapped atomically by a refresh.</summary>
    private IDisposable? _candidateSubscriptions;

    /// <summary>The raw ticker arrival signal the pulse samples, not shipped directly.</summary>
    private readonly Subject<RxVoid> _dataTick = new();

    /// <summary>
    /// Whether the last settled fetch delivered a served catalog (a failed
    /// fetch or a venue without a symbol list keeps it false, and the page
    /// stays on the reason line). Written under the refresh gate, read by
    /// the pulse path: a torn-free bool read races nothing.
    /// </summary>
    private bool _catalogAvailable;

    /// <summary>The adapter change subscription re-fetching the universe per switch.</summary>
    private readonly IDisposable? _switchSubscription;

    /// <summary>The epoch counter invalidating a refresh racing another.</summary>
    private int _epoch;

    /// <summary>Serialization gate for the universe refreshes.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The facade: the universe fetch and every candidate pump ride.</summary>
    private readonly ActiveExchangeService _activeExchanges;

    /// <summary>The feed's own logging lane.</summary>
    private readonly ILogger<ScreenerFeedService> _logger;

    /// <summary>The pulse source: the default Rx interval clock or the test seam.</summary>
    private readonly IObservable<long> _pulse;

    #endregion Private Fields

    #region Properties

    /// <summary>
    /// The panel's page feed: one coalesced page per pulse — the ticked
    /// candidates in the adapter's own order with the page's honest state.
    /// An empty page (nothing ticked yet, or no catalog) still ships so the
    /// panel renders its waiting or reason state instead of a stale surface.
    /// </summary>
    public IObservable<ScreenerSnapshot> Snapshots { get; }

    #endregion Properties

    #region Constructor

    /// <summary>
    /// Create the feed: compose the coalesced page stream over the pulse and
    /// arm the adapter-change re-fetch. The candidate window itself arms in
    /// <see cref="InitializeAsync"/> (the startup walk owns the warm-up
    /// fetch) — never a blocking constructor network call.
    /// </summary>
    public ScreenerFeedService(
        ActiveExchangeService activeExchanges,
        ILogger<ScreenerFeedService> logger,
        IObservable<long>? pulse = null)
    {
        _activeExchanges = activeExchanges ?? throw new ArgumentNullException(nameof(activeExchanges));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _pulse = pulse ?? Observable.Interval(PulseInterval);

        Snapshots = _dataTick
            .StartWith(RxVoid.Default)
            .Sample(_pulse)
            .Select(_ => BuildSnapshot());

        // An exchange switch re-fetches the universe (the heatmap feed's
        // kick pattern): a new adapter's pairs are what the window arms,
        // and nothing of the old exchange's ticker state may ride along.
        // The behavioral stream's seed is skipped — subscribing is not a
        // switch; the warm-up owns the first composition. The epoch bumps
        // BEFORE the kick: an in-flight fetch of the previous composition
        // is dropped at its finish (its adapters list belongs to a
        // superseded exchange).
        _switchSubscription = _activeExchanges
            .AdapterChanges
            .Skip(1)
            .Subscribe(
                _ =>
                {
                    Interlocked.Increment(ref _epoch);
                    KickRefresh();
                },
                ex => _logger.LogError(ex, "The screener's adapter-change stream stopped unexpectedly"));
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>
    /// The warm-up fetch: arm the candidate window once without blocking the
    /// startup walk. A failure only means the reason line until an adapter
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
                ex => _logger.LogWarning(ex, "Screener universe warm-up failed; the next adapter change re-fetches"));

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
        Volatile.Write(ref _window, []);
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
                ex => _logger.LogWarning(ex, "Screener universe refresh failed; the next adapter change re-tries"));
    }

    /// <summary>
    /// One refresh attempt: take the active adapter's symbol list, swap the
    /// candidate window and its pumps atomically (the previous exchange's
    /// ticker state dies with the old window), then publish the fresh page —
    /// a switch renders the honest waiting state immediately and lets the
    /// new candidate tickers fill it. A stale attempt (a newer switch
    /// arrived in flight) is dropped at its finish: nothing of the
    /// superseded composition publishes.
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
            Volatile.Write(ref _window, []);

            IReadOnlyList<Symbol> symbols;
            try
            {
                symbols = await _activeExchanges.GetSymbolsAsync(ct);
            }
            catch (Exception ex)
            {
                _catalogAvailable = false;
                _logger.LogWarning(ex, "Screener: the universe fetch failed; the panel stays on the reason line until a re-fetch");
                _dataTick.OnNext(RxVoid.Default);
                return;
            }

            if (Volatile.Read(ref _epoch) != epochAtFetch)
            {
                return;
            }

            // An empty catalog is the honest "the adapter serves no pairs":
            // the reason line stands until a re-fetch composes a window.
            _catalogAvailable = symbols.Count > 0;

            var pumpGroup = new CompositeDisposable();
            var window = new List<CandidateState>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var symbol in symbols.Take(CandidateBudget))
            {
                if (!seen.Add(symbol.Normalized))
                {
                    continue;
                }

                var candidate = new CandidateState(symbol);
                window.Add(candidate);
                pumpGroup.Add(SubscribeCandidate(candidate));
            }

            _candidateSubscriptions = pumpGroup;
            Volatile.Write(ref _window, window);

            // The fresh-but-unpriced page: the panel switches to the honest
            // waiting state the moment a swap lands, never one pulse later.
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
                    "Screener: ticker stream for {Symbol} failed; the candidate keeps its last served data",
                    candidate.SymbolText));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Screener: no ticker available for {Symbol}; the candidate stays unpriced",
                candidate.SymbolText);
            return Disposable.Empty;
        }
    }

    /// <summary>
    /// Build the page: the ticked candidates in the window's own order (the
    /// adapter's catalog order — the panel owns the ranking), with the
    /// page's honest state. An unpriced window ships the waiting state, a
    /// missing or failed catalog ships the reason state.
    /// </summary>
    private ScreenerSnapshot BuildSnapshot()
    {
        var window = Volatile.Read(ref _window);
        var rows = new List<ScreenerRow>(window.Count);
        foreach (var candidate in window)
        {
            if (candidate.ToRow() is { } row)
            {
                rows.Add(row);
            }
        }

        var state = rows.Count > 0
            ? ScreenerFeedState.Live
            : Volatile.Read(ref _catalogAvailable)
                ? ScreenerFeedState.NoTicksYet
                : ScreenerFeedState.CatalogUnavailable;

        return new ScreenerSnapshot(rows, state);
    }

    #endregion Private Methods

    #region Candidate State

    /// <summary>
    /// One candidate pair's self-coherent state: the ticker handler swaps
    /// one immutable tick snapshot in; the pulse builder only reads it. The
    /// single atomic write keeps price, volume and change consistent in
    /// every page.
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

        /// <summary>The candidate's row for the current page (null until it ticks).</summary>
        public ScreenerRow? ToRow()
        {
            return Latest is not { } tick
                ? null
                : new ScreenerRow(SymbolText, tick.LastPrice, tick.Volume24h, tick.Change24hPercent);
        }
    }

    /// <summary>
    /// One ticker arrival's full candidate state (immutable, swapped atomically).
    /// </summary>
    private sealed record CandidateTick(
        decimal LastPrice,
        decimal Volume24h,
        decimal Change24hPercent);

    #endregion Candidate State
}
