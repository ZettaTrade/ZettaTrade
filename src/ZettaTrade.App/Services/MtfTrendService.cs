namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Livery.Contracts;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// The multi-timeframe trend computer (T-239): per-symbol bull/bear/range
/// states across 15m/1h/4h/1d, computed from the local candle cache
/// (T-085) OFF the UI thread. One pass rides one screener page under a
/// cooldown (a symbol-set change bypasses it), the pass's cache reads
/// share a bounded two-slot budget, and a series the cache cannot judge
/// yet reports the honest warming state and asks the T-086 deep-history
/// fetcher for depth — a bounded number of series per pass, a dry fetch
/// waiting out its backoff before re-asking. A cold 64-symbol × 4-frame
/// universe therefore warms in over successive passes, never one REST
/// storm and never a frozen panel.
/// <para>
/// App-lifetime singleton: the subscription and the read budget mirror
/// the application's lifetime and need no explicit disposal, exactly like
/// every workspace panel's service.
/// </para>
/// </summary>
public sealed class MtfTrendService : IAsyncInitializable
{
    #region Constants

    /// <summary>The trend time frames in their column order (15m/1h/4h/1d).</summary>
    internal static readonly IReadOnlyList<TimeFrame> Frames =
        [TimeFrame.M15, TimeFrame.H1, TimeFrame.H4, TimeFrame.D1];

    /// <summary>The compute cooldown: a same-shape page re-plans at most this often; a symbol-set change bypasses it.</summary>
    internal static readonly TimeSpan ComputeCooldown = TimeSpan.FromSeconds(20);

    /// <summary>The dry backoff: a pair whose fetch pass covered no usable depth waits this long before re-asking.</summary>
    internal static readonly TimeSpan DryBackoff = TimeSpan.FromMinutes(10);

    /// <summary>The cache-read budget: at most this many series reads in flight (the bounded-concurrency discipline).</summary>
    internal const int ReadBudget = 2;

    /// <summary>
    /// The backfill budget: at most this many series per pass drain the
    /// deep-history fetcher — the rest wait for the next pass, so a cold
    /// universe walks in politely instead of storming the venue's budget
    /// (T-106) in one turn.
    /// </summary>
    internal const int BackfillBudgetPerPass = 4;

    /// <summary>
    /// The fetch window's margin over the minimum bars: the two-times walk
    /// covers the session venues' weekend gaps on the day-scale frames (a
    /// cached hole below a served tail just refetches — the write-through
    /// conflates the overlap).
    /// </summary>
    internal const int BackfillWindowMultiple = 2;

    #endregion Constants

    #region Private Fields

    /// <summary>The screener feed whose pages own the screened universe.</summary>
    private readonly ScreenerFeedService _feed;

    /// <summary>The local candle cache (T-085) every series read rides.</summary>
    private readonly ICandleCache _cache;

    /// <summary>The facade: its <c>Current</c> adapter is the pass's snapshot for every cache key and backfill request.</summary>
    private readonly ActiveExchangeService _activeExchanges;

    /// <summary>The deep-history fetcher (T-086) a warming pair's depth ask rides.</summary>
    private readonly CandleBackfillService _backfill;

    /// <summary>The settings service: the classifier's tuning loads at initialization.</summary>
    private readonly ISettingsService _settings;

    /// <summary>The trend computer's own logging lane.</summary>
    private readonly ILogger<MtfTrendService> _logger;

    /// <summary>The read budget gate: at most <see cref="ReadBudget"/> series reads in flight.</summary>
    private readonly SemaphoreSlim _readBudget = new(ReadBudget, ReadBudget);

    /// <summary>The published pass tables.</summary>
    private readonly Subject<MtfTrendSnapshot> _snapshots = new();

    /// <summary>
    /// The dry pairs: a (adapter, symbol, frame) key whose fetch pass
    /// covered no usable depth waits until its stamp before re-asking.
    /// Written and read only inside the serialized pass pipeline (the
    /// <c>Concat</c> chain) and awaited test calls — no concurrent writer
    /// exists.
    /// </summary>
    private readonly Dictionary<(string AdapterId, string SymbolText, TimeFrame Frame), DateTimeOffset> _dryUntil = [];

    /// <summary>The classifier's tuning, loaded at initialization from the settings store.</summary>
    private MtfTrendSettings _tuning = MtfTrendSettings.Default;

    /// <summary>The page pipeline's subscription (app-lifetime, never disposed).</summary>
    private IDisposable? _pipeline;

    /// <summary>The last pass's sorted symbol set (the cooldown's change key).</summary>
    private string? _lastSet;

    /// <summary>The last pass's instant (the cooldown's clock).</summary>
    private DateTimeOffset _lastPass = DateTimeOffset.MinValue;

    #endregion Private Fields

    #region Properties

    /// <summary>
    /// The published pass tables: one table per computed pass. The panel
    /// observes it on the UI thread and holds the latest table for the
    /// row formatting.
    /// </summary>
    public IObservable<MtfTrendSnapshot> Snapshots => _snapshots;

    #endregion Properties

    #region Constructor

    /// <summary>
    /// Create the trend computer over the feed, the cache, the facade, the
    /// fetcher and the settings. Nothing arms here: the subscription arms
    /// in <see cref="InitializeAsync"/> after the tuning loads (the feed's
    /// own warm-up fetch stays the startup walk's first leg).
    /// </summary>
    public MtfTrendService(
        ScreenerFeedService feed,
        ICandleCache cache,
        ActiveExchangeService activeExchanges,
        CandleBackfillService backfill,
        ISettingsService settings,
        ILogger<MtfTrendService> logger)
    {
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _activeExchanges = activeExchanges ?? throw new ArgumentNullException(nameof(activeExchanges));
        _backfill = backfill ?? throw new ArgumentNullException(nameof(backfill));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #endregion Constructor

    #region Initialization

    /// <summary>
    /// Arm the pass pipeline: load the tuning from the settings store and
    /// subscribe the feed's pages — one <c>ConsiderAsync</c> per page,
    /// serialized by <c>Concat</c> so two passes never interleave (the
    /// dry-pair table rides that serialization). The pipeline never
    /// throws: a failed pass logs and the next page re-arms.
    /// </summary>
    /// <param name="ct">The startup walk's cancellation token.</param>
    /// <returns>The initialization's completed signal.</returns>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _tuning = await MtfTrendSettings.LoadAsync(_settings, ct);

        _pipeline = _feed.Snapshots
            .Select(page => Observable.FromAsync(() => ConsiderAsync(page, CancellationToken.None)))
            .Concat()
            .Subscribe(
                static _ => { },
                error => _logger.LogError(error, "MtfTrend: the page pipeline stopped unexpectedly"));
    }

    #endregion Initialization

    #region Pass Pipeline

    /// <summary>
    /// Consider one screener page: compute the pass when the cooldown
    /// allows or the symbol set changed, publish the table, then drain a
    /// bounded number of warming pairs through the deep-history fetcher.
    /// Never throws to the pipeline: a failed pass logs and keeps the last
    /// table alive.
    /// </summary>
    /// <param name="page">The considered screener page.</param>
    /// <param name="ct">The pass's cancellation token.</param>
    /// <returns>The pass's completed signal.</returns>
    internal async Task ConsiderAsync(ScreenerSnapshot page, CancellationToken ct)
    {
        try
        {
            var symbols = page.Rows
                .Select(static row => row.SymbolText)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static text => text, StringComparer.Ordinal)
                .ToList();
            var set = string.Join("|", symbols);
            var now = DateTimeOffset.UtcNow;
            if (set == _lastSet && now - _lastPass < ComputeCooldown)
            {
                return;
            }

            _lastSet = set;
            _lastPass = now;

            var snapshot = await ComputeAsync(symbols, ct);
            _snapshots.OnNext(snapshot);
            await DrainWarmingPairsAsync(snapshot, ct);
        }
        catch (OperationCanceledException)
        {
            // The pass rides the app lifetime; a cancellation is a
            // shutdown, never a fault.
        }
        catch (Exception error)
        {
            _logger.LogError(error, "MtfTrend: the trend pass failed; the panel keeps its last table");
        }
    }

    /// <summary>
    /// Drain the table's warming pairs through the deep-history fetcher:
    /// at most <see cref="BackfillBudgetPerPass"/> series per pass, in the
    /// frames' column order under the sorted symbols, a pair whose last
    /// fetch stayed dry waiting out <see cref="DryBackoff"/> first. The
    /// facade's <c>Current</c> adapter is the pass's snapshot — a switch
    /// mid-pass finishes on the old adapter and the next pass re-snapshots.
    /// </summary>
    /// <param name="snapshot">The just-computed (or test-staged) verdict table.</param>
    /// <param name="ct">The drain's cancellation token.</param>
    /// <returns>The drain's completed signal.</returns>
    internal async Task DrainWarmingPairsAsync(MtfTrendSnapshot snapshot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var adapter = _activeExchanges.Current;
        var minimum = TrendClassifier.MinimumBars(_tuning.EmaPeriod, _tuning.AdxPeriod);
        var now = DateTimeOffset.UtcNow;

        var requested = 0;
        foreach (var verdict in snapshot.BySymbol.Values.OrderBy(
            static trend => trend.SymbolText,
            StringComparer.Ordinal))
        {
            if (requested >= BackfillBudgetPerPass)
            {
                return;
            }

            Symbol? symbol = null;
            try
            {
                symbol = Symbol.Parse(verdict.SymbolText);
            }
            catch (ArgumentException)
            {
                // The row's text owns no route on this exchange either; the
                // verdict stays warming and no fetch asks for it.
            }

            if (symbol is null)
            {
                continue;
            }

            foreach (var frame in Frames)
            {
                if (requested >= BackfillBudgetPerPass)
                {
                    return;
                }

                if (FrameState(verdict, frame) is not MtfTrendState.Warming)
                {
                    continue;
                }

                var key = (adapter.Id, verdict.SymbolText, frame);
                if (_dryUntil.TryGetValue(key, out var until) && until > now)
                {
                    continue;
                }

                requested++;
                var covered = await DrainPairAsync(symbol, adapter, frame, minimum, ct);
                if (covered)
                {
                    _dryUntil.Remove(key);
                }
                else
                {
                    _dryUntil[key] = DateTimeOffset.UtcNow + DryBackoff;
                }
            }
        }
    }

    /// <summary>
    /// One pair's depth ask: walk the fetch window backwards through the
    /// T-086 fetcher — every fetched segment was already persisted through
    /// the cache (write-before-emit), so the drain discards the batches
    /// and re-reads the cache for the verdict. A failed pass is
    /// bookkeeping noise (logged, never a broken turn) and reads as dry.
    /// </summary>
    /// <param name="symbol">The pair's symbol.</param>
    /// <param name="adapter">The pass's adapter snapshot.</param>
    /// <param name="frame">The pair's time frame.</param>
    /// <param name="minimum">The classifier's minimum bars for the tuning.</param>
    /// <param name="ct">The drain's cancellation token.</param>
    /// <returns>True when the cache now holds enough depth for the classifier.</returns>
    private async Task<bool> DrainPairAsync(
        Symbol symbol,
        IMarketDataAdapter adapter,
        TimeFrame frame,
        int minimum,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var window = TimeSpan.FromTicks(
            minimum * BackfillWindowMultiple * TimeFrameCalendar.NominalBarLength(frame).Ticks);
        var request = new CandleBackfillRequest(adapter, symbol, frame, now - window, now);

        try
        {
            await foreach (var batch in _backfill.FetchAsync(request, ct))
            {
                // The write-through already persisted the batch; the verdict
                // re-reads the cache below.
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            _logger.LogWarning(
                error,
                "MtfTrend: the backfill pass for {Symbol} {Frame} on {Exchange} failed; the pair waits out its dry backoff",
                symbol.Normalized,
                frame,
                adapter.Id);
        }

        var after = await _cache.GetLatestAsync(adapter.Id, symbol, frame, minimum, ct);
        return after.Count >= minimum;
    }

    /// <summary>The verdict member one frame maps to (the column order's twin).</summary>
    private static MtfTrendState FrameState(MtfSymbolTrend verdict, TimeFrame frame) => frame switch
    {
        TimeFrame.M15 => verdict.M15,
        TimeFrame.H1 => verdict.H1,
        TimeFrame.H4 => verdict.H4,
        TimeFrame.D1 => verdict.D1,
        _ => MtfTrendState.Warming,
    };

    #endregion Pass Pipeline

    #region Compute

    /// <summary>
    /// Compute one pass's verdict table: every symbol's four series read
    /// through the shared read budget, each verdict the classifier's (or
    /// the honest warming state for a series the cache cannot judge yet).
    /// The pass captures the facade's <c>Current</c> adapter once — every
    /// cache key and the drain's requests ride the same snapshot.
    /// </summary>
    /// <param name="symbolTexts">The screened symbols' normalized texts (distinct; duplicates collapse).</param>
    /// <param name="ct">The pass's cancellation token.</param>
    /// <returns>The pass's verdict table.</returns>
    public async Task<MtfTrendSnapshot> ComputeAsync(
        IReadOnlyList<string> symbolTexts,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(symbolTexts);

        var adapter = _activeExchanges.Current;
        var minimum = TrendClassifier.MinimumBars(_tuning.EmaPeriod, _tuning.AdxPeriod);
        var verdicts = await Task.WhenAll(symbolTexts
            .Distinct(StringComparer.Ordinal)
            .Select(text => Task.Run(() => ComputeSymbolAsync(text, adapter, minimum, ct), ct))
            .ToArray());
        return new MtfTrendSnapshot(
            DateTimeOffset.UtcNow,
            verdicts.ToDictionary(
                static verdict => verdict.SymbolText,
                static verdict => verdict,
                StringComparer.Ordinal));
    }

    /// <summary>One symbol's verdict: its four series, each through the read budget.</summary>
    private async Task<MtfSymbolTrend> ComputeSymbolAsync(
        string symbolText,
        IMarketDataAdapter adapter,
        int minimum,
        CancellationToken ct)
    {
        Symbol? symbol = null;
        try
        {
            symbol = Symbol.Parse(symbolText);
        }
        catch (ArgumentException)
        {
            // The text owns no route on this exchange; the verdict stays
            // the honest warming shape without a fabricated state.
        }

        return new(
            symbolText,
            await SeriesStateAsync(symbol, adapter, TimeFrame.M15, minimum, ct),
            await SeriesStateAsync(symbol, adapter, TimeFrame.H1, minimum, ct),
            await SeriesStateAsync(symbol, adapter, TimeFrame.H4, minimum, ct),
            await SeriesStateAsync(symbol, adapter, TimeFrame.D1, minimum, ct));
    }

    /// <summary>
    /// One series' verdict: the cache read under the read budget (the
    /// classifier's own run shares the slot — the whole leg stays inside
    /// the two-flight cap), the honest warming state below the minimum,
    /// the classifier's verdict above it.
    /// </summary>
    private async Task<MtfTrendState> SeriesStateAsync(
        Symbol? symbol,
        IMarketDataAdapter adapter,
        TimeFrame frame,
        int minimum,
        CancellationToken ct)
    {
        if (symbol is null)
        {
            return MtfTrendState.Warming;
        }

        await _readBudget.WaitAsync(ct);
        try
        {
            var bars = await _cache.GetLatestAsync(adapter.Id, symbol, frame, minimum, ct);
            if (bars.Count < minimum)
            {
                return MtfTrendState.Warming;
            }

            return TrendClassifier.Classify(bars, _tuning.EmaPeriod, _tuning.AdxPeriod, _tuning.AdxThreshold);
        }
        finally
        {
            _readBudget.Release();
        }
    }

    #endregion Compute
}
