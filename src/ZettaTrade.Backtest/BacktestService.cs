namespace ZettaTrade.Backtest;

using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Fees;
using ZettaTrade.Infrastructure.Persistence;
using ZettaTrade.Strategies;

/// <summary>
/// The offline deterministic backtest harness (T-113): a run pages the
/// newest cached candles for its (adapter, symbol, interval) series,
/// replays them through the strategy seam's exact T-111 shapes (a real
/// <see cref="StrategyHost"/> evaluation loop), converts stance changes
/// into fills riding the paper ledger's T-051-corrected bookkeeping and
/// produces the equity report. Every number derives from the candle
/// sequence and the seeded draws — no wall clock inside the replay, no
/// unseeded randomness — so the same request over the same cache yields a
/// byte-identical report.
/// </summary>
/// <remarks>
/// Entirely offline by construction: the service reads the local candle
/// cache only; it never constructs an adapter, never touches the rate
/// limiter and can never reach a trading adapter's order surface. A
/// cancelled run is the stop (never an error downstream): the orchestrating
/// loops inspect the token cooperatively and return the cancelled result
/// with no report and nothing written anywhere.
/// <para>
/// The events stream (Started/Progress/terminal) rides a
/// <see cref="Subject{T}"/> like every other service-side notification
/// stream of the codebase; UI consumers marshal it through
/// ObserveOnUiThread themselves.
/// </para>
/// </remarks>
public sealed class BacktestService : IBacktestService
{
    #region Constants

    /// <summary>How many candles the replay processes between two Progress events
    /// (the stride the run/progress VM renders). Events carry no semantics.</summary>
    private const int ProgressStride = 256;

    #endregion Constants

    #region Private Fields

    private readonly IStrategyCatalog _strategies;
    private readonly ICandleCache _candles;
    private readonly IFeeScheduleProvider _feeSchedules;
    private readonly ILogger<BacktestService> _logger;
    private readonly ILogger<StrategyHost> _hostLogger;

    /// <summary>The per-run event stream (one live subscription per listener).</summary>
    private readonly Subject<BacktestRunEvent> _events = new();

    /// <summary>The single-flight gate across the whole module: one run at a
    /// time keeps the event stream coherent (runs never interleave) and the
    /// refusal is an honest error, never a silent queue.</summary>
    private readonly SemaphoreSlim _runGate = new(1, 1);

    #endregion Private Fields

    #region Constructors

    /// <summary>Create the harness over the strategy seam, the candle cache and the fee registry.</summary>
    /// <param name="strategies">The T-111 strategy catalog (the replay resolves one id per run).</param>
    /// <param name="candles">The local candle cache (the replay's only data source).</param>
    /// <param name="feeSchedules">The fee registry commissions stamp from (paper parity).</param>
    /// <param name="loggerFactory">The logger factory: the harness and the host get separate typed loggers.</param>
    public BacktestService(
        IStrategyCatalog strategies,
        ICandleCache candles,
        IFeeScheduleProvider feeSchedules,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _strategies = strategies ?? throw new ArgumentNullException(nameof(strategies));
        _candles = candles ?? throw new ArgumentNullException(nameof(candles));
        _feeSchedules = feeSchedules ?? throw new ArgumentNullException(nameof(feeSchedules));
        _logger = loggerFactory.CreateLogger<BacktestService>();
        _hostLogger = loggerFactory.CreateLogger<StrategyHost>();
    }

    #endregion Constructors

    #region Events

    /// <inheritdoc/>
    public IObservable<BacktestRunEvent> Events => _events.AsObservable();

    private void Publish(BacktestRunEvent @event) => _events.OnNext(@event);

    #endregion Events

    #region Run

    /// <inheritdoc/>
    public async Task<BacktestRunResult> RunAsync(
        BacktestRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        // The single-flight gate: a refused second run never starts loading,
        // never touches the events stream and says so explicitly.
        if (!await _runGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("A backtest run is already in flight; runs refuse concurrent starts.");
        }

        try
        {
            return await RunLockedAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _runGate.Release();
        }
    }

    private async Task<BacktestRunResult> RunLockedAsync(
        BacktestRunRequest request,
        CancellationToken cancellationToken)
    {
        var strategy = _strategies.Get(request.StrategyId)
            ?? throw new InvalidOperationException(
                $"Strategy '{request.StrategyId}' is unknown to the catalog; the run is refused, never defaulted.");

        BacktestReport report;
        var loaded = false;

        try
        {
            var bars = await LoadCandlesAsync(request, cancellationToken).ConfigureAwait(false);
            loaded = true;
            _logger.LogInformation(
                "Backtest '{Strategy}' on {Symbol} {Interval} (adapter {AdapterId}, seed {Seed}): {Bars} cached bars loaded",
                request.StrategyId,
                request.Symbol.Normalized,
                request.Interval,
                request.AdapterId,
                request.Seed,
                bars.Count);

            report = await ReplayAsync(request, strategy, bars, publishEvents: true, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The stop is the outcome: the cancelled run produces NO report
            // and NO artifacts. The replay publishes its own cancelled event
            // when the stop landed inside it; a stop during the load never
            // started a run, so the event goes out here with the zero count.
            if (!loaded)
            {
                Publish(new BacktestRunEvent(BacktestRunEventKind.Cancelled, request.Bars, BarsProcessed: 0, Report: null));
                _logger.LogInformation(
                    "Backtest '{Strategy}' cancelled during the load; nothing was produced",
                    request.StrategyId);
            }

            return BacktestRunResult.Cancelled;
        }

        return new BacktestRunResult(BacktestRunOutcome.Completed, report);
    }

    /// <summary>
    /// Replay one fully-loaded window through the strategy seam and fold the
    /// report (T-113's evaluation loop, extracted so a sweep replays the same
    /// bars per parameterization): a fresh host, ledger and seeded draw per
    /// replay, the per-run events published as the run shape owns them. The
    /// feed and the signal read run concurrently (T-264): the feed paces one
    /// candle at a time beside the read, so the in-flight queues stay
    /// near-empty across long replays while the channel's FIFO order keeps
    /// every report byte-identical to the serial shape. The cancellation is
    /// cooperative per signal; a stop inside the replay publishes the
    /// cancelled event (with the bars processed so far) and rethrows — the
    /// caller owns the cancelled result.
    /// </summary>
    /// <param name="publishEvents">Publish the run-lane event shapes
    /// (Started/Progress/Cancelled). The T-263 best-report re-replay rides
    /// the silent shape: it must never re-count the sweep's row progress the
    /// surface already folded from the ranked rows' own replays.</param>
    private async Task<BacktestReport> ReplayAsync(
        BacktestRunRequest request,
        IStrategy strategy,
        IReadOnlyList<Bar> bars,
        bool publishEvents,
        CancellationToken cancellationToken)
    {
        // Deterministic seed: the replay's only stochastic input (the slippage
        // draw) derives from the explicit seed — xoshiro256** (the explicit-
        // seed Random ctor), stable across runs of one runtime. A fresh draw
        // per replay: every parameterization of a sweep reproduces its own
        // run exactly.
        var prng = new Random(request.Seed);

        var processed = 0;
        var pumpTask = Task.CompletedTask;

        try
        {
            // The T-111 canon, consumed exactly as registered: a real host runs
            // the candle-close evaluation loop over its own retained window; the
            // replay feeds it the loaded series and reads the stamped signals.
            var host = new StrategyHost([strategy], _hostLogger);

            var ledger = new BacktestLedger(request.AdapterId, request.Symbol, _feeSchedules, request.InitialQuoteAmount);
            if (publishEvents)
            {
                Publish(new BacktestRunEvent(BacktestRunEventKind.Started, request.Bars, BarsProcessed: 0, Report: null));
            }

            // The pump runs with the SAME token: cancellation is the stop (the
            // host completes its output; the reader sees a completed channel,
            // not an exception). The orchestrating loops below inspect the token
            // cooperatively per step and per signal.
            pumpTask = host.RunAsync(cancellationToken);

            var previousStance = StrategyStance.Neutral;

            // The feed runs BESIDE the read (T-264): it is started here and held
            // to the finally below, so the reader consumes the stamped signals
            // while the feed is still feeding bars — the in-flight queues stay
            // near-empty across long replays instead of accumulating O(N)
            // stamped events ahead of the first applied one. The channels are
            // FIFO and the pump is sequential, so the applied signal sequence —
            // and therefore every report — is byte-identical to the serial shape.
            var feedTask = FeedBarsAsync(host, bars, request, cancellationToken);

            try
            {
                await foreach (var signal in host.Signals
                    .ReadAllAsync(CancellationToken.None)
                    .ConfigureAwait(false))
                {
                    // The cooperative stop: checked per signal, nothing re-arms.
                    cancellationToken.ThrowIfCancellationRequested();
                    ApplySignal(ledger, prng, previousStance, signal, request, ref previousStance);
                    processed++;

                    if (processed % ProgressStride == 0)
                    {
                        Publish(new BacktestRunEvent(BacktestRunEventKind.Progress, request.Bars, processed, Report: null));
                    }
                }

                // The stop may have landed while the reader waited on an emptied
                // channel (the paced feed lets the pump idle between candles):
                // a token that fired mid-run is the cancelled outcome, never a
                // partial report dressed as a completion.
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally
            {
                // The feed settles on every path; its token-fired stop is the
                // same stop the replay rides, so whatever surfaces here unwinds
                // through the same cancelled-event catch below.
                await feedTask.ConfigureAwait(false);
            }

            // The final mark rides the last candle's close (or the empty
            // honest zero-activity baseline when the cache served nothing).
            var finalClose = bars.Count > 0 ? bars[^1].Close : 0m;
            var report = new BacktestReport(
                request,
                ledger.EquityCurve,
                ledger.Fills,
                ledger.Refusals,
                ledger.ComputeStatistics(finalClose));

            if (publishEvents)
            {
                Publish(new BacktestRunEvent(
                    BacktestRunEventKind.Completed,
                    request.Bars,
                    bars.Count,
                    report));
            }

            _logger.LogInformation(
                "Backtest '{Strategy}' completed: {Fills} fill(s), {Refusals} refusal(s), {Trades} closed trade(s), final equity {FinalEquity}",
                request.StrategyId,
                report.Statistics.Fills,
                report.Refusals.Count,
                report.Statistics.Trades,
                report.Statistics.FinalEquity);

            return report;
        }
        catch (OperationCanceledException)
        {
            // The stop inside the replay: the cancelled run publishes the
            // cancelled event and produces NO report and NO artifacts.
            if (publishEvents)
            {
                Publish(new BacktestRunEvent(BacktestRunEventKind.Cancelled, request.Bars, processed, Report: null));
            }

            _logger.LogInformation(
                "Backtest '{Strategy}' cancelled after {Processed} candle(s); nothing was produced",
                request.StrategyId,
                processed);
            throw;
        }
        finally
        {
            // The pump ends on its own (input completed, or its token fired)
            // and cannot throw — its RunAsync swallow-the-stop canon.
            await pumpTask.ConfigureAwait(false);
        }
    }

    #endregion Run

    #region Sweep

    /// <inheritdoc/>
    public async Task<BacktestSweepResult> SweepAsync(
        BacktestSweepRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        // The same single-flight gate a run takes, held for the WHOLE sweep:
        // the per-run events must never interleave with another run's, and a
        // run started while a sweep is in flight refuses exactly as a second
        // run would.
        if (!await _runGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("A backtest run is already in flight; runs refuse concurrent starts.");
        }

        try
        {
            return await SweepLockedAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _runGate.Release();
        }
    }

    private async Task<BacktestSweepResult> SweepLockedAsync(
        BacktestSweepRequest request,
        CancellationToken cancellationToken)
    {
        var strategy = _strategies.Get(request.Template.StrategyId)
            ?? throw new InvalidOperationException(
                $"Strategy '{request.Template.StrategyId}' is unknown to the catalog; the sweep is refused, never defaulted.");

        _logger.LogInformation(
            "Backtest sweep '{Strategy}': grid {First} x {Second} over {Bars} bars (adapter {AdapterId}, seed {Seed})",
            request.Template.StrategyId,
            request.FirstAxis.Parameter,
            request.SecondAxis.Parameter,
            request.Template.Bars,
            request.Template.AdapterId,
            request.Template.Seed);

        // The window loads ONCE: every parameterization replays the SAME
        // bars, so the grid compares parameterizations of one honest window
        // (and the load's cost stays one walk-back, not one per row).
        IReadOnlyList<Bar> bars;
        try
        {
            bars = await LoadCandlesAsync(request.Template, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The stop during the load: no parameterization ever started, so
            // no cancelled event goes out (the stream carries per-run events
            // only) — the cancelled result is the whole answer.
            _logger.LogInformation(
                "Backtest sweep '{Strategy}' cancelled during the load; nothing was produced",
                request.Template.StrategyId);
            return new BacktestSweepResult(BacktestSweepOutcome.Cancelled, request, []);
        }

        _logger.LogInformation(
            "Backtest sweep '{Strategy}': {Bars} cached bars loaded, {Parameterizations} parameterization(s) to replay",
            request.Template.StrategyId,
            bars.Count,
            request.FirstAxis.Values.Count * request.SecondAxis.Values.Count);

        // The rows in enumeration order (first axis outer, second axis
        // inner); the ranking happens once over the completed rows.
        var rows = new List<BacktestSweepRow>();
        var outcome = BacktestSweepOutcome.Completed;
        var index = 0;

        try
        {
            foreach (var firstValue in request.FirstAxis.Values)
            {
                foreach (var secondValue in request.SecondAxis.Values)
                {
                    // The cooperative stop BETWEEN parameterizations — the
                    // acceptance's "never mid-run" discipline: each replay
                    // rides no token and completes fully; a stop request
                    // lands at the next boundary, so the row whose replay
                    // already completed stays and the next one never starts.
                    cancellationToken.ThrowIfCancellationRequested();

                    var rowRequest = request.Apply(firstValue, secondValue);
                    var report = await ReplayAsync(rowRequest, strategy, bars, publishEvents: true, CancellationToken.None).ConfigureAwait(false);
                    rows.Add(new BacktestSweepRow(
                        Rank: 0,
                        Index: index,
                        FirstValue: firstValue,
                        SecondValue: secondValue,
                        Request: rowRequest,
                        Statistics: report.Statistics));
                    index++;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The stop landed at a boundary: the parameterizations whose
            // replays completed stay (each a complete, byte-stable report —
            // nothing half-run exists), the rest never started. The stop is
            // the outcome, never an error.
            outcome = BacktestSweepOutcome.Cancelled;
            _logger.LogInformation(
                "Backtest sweep '{Strategy}' cancelled after {Count} parameterization(s); the completed rows stay",
                request.Template.StrategyId,
                rows.Count);
        }

        var ranked = Rank(rows);
        _logger.LogInformation(
            "Backtest sweep '{Strategy}' {End}: {Count} of {Total} parameterization(s) replayed, best net {BestNet}",
            request.Template.StrategyId,
            outcome == BacktestSweepOutcome.Completed ? "completed" : "cancelled at a boundary",
            ranked.Count,
            request.FirstAxis.Values.Count * request.SecondAxis.Values.Count,
            ranked.Count > 0 ? ranked[0].Net : 0m);

        // The best row's FULL report (T-263): one silent re-replay of the
        // rank-1 parameterization on the SAME loaded window — the same seed
        // and the same bars fold the byte-identical report that row's own
        // replay produced, so the auto-selected report surface is the
        // sweep's own fact and the retained result carries ONE equity
        // curve, not one per row. The re-replay rides no token: the grid
        // completed, and this is the completion's own presentation step (a
        // stop request lands after the sweep answers, as between rows).
        BacktestReport? bestReport = null;
        if (outcome == BacktestSweepOutcome.Completed && ranked.Count > 0)
        {
            bestReport = await ReplayAsync(
                ranked[0].Request,
                strategy,
                bars,
                publishEvents: false,
                CancellationToken.None).ConfigureAwait(false);
        }

        return new BacktestSweepResult(outcome, request, ranked, bestReport);
    }

    /// <summary>
    /// Rank the completed rows deterministically: net descending, ties broken
    /// by the enumeration order — the same grid over the same cache always
    /// yields the same table. The rank is 1-based, assigned here once.
    /// </summary>
    private static IReadOnlyList<BacktestSweepRow> Rank(List<BacktestSweepRow> rows) =>
    [.. rows
        .OrderByDescending(static row => row.Net)
        .ThenBy(static row => row.Index)
        .Select((row, rank) => row with { Rank = rank + 1 })];

    #endregion Sweep

    #region Monte Carlo

    /// <inheritdoc/>
    public async Task<BacktestMonteCarloResult> MonteCarloAsync(
        BacktestMonteCarloRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The pre-flight: a refused shape never reaches the worker lane.
        request.Validate();

        // The pure fold rides the worker lane (off-UI): the caller's context
        // stays responsive while the paths sample. The compute consults no
        // gate and no stream — an immutable report may be sampled while a
        // replay runs.
        try
        {
            var distribution = await Task.Run(
                () => BacktestMonteCarlo.Compute(request, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            return new BacktestMonteCarloResult(BacktestMonteCarloOutcome.Completed, distribution);
        }
        catch (OperationCanceledException)
        {
            // The stop is the outcome: the cancelled sampling produces no
            // distribution and no numbers.
            _logger.LogInformation(
                "Backtest Monte Carlo over '{Strategy}' cancelled between paths; nothing was produced",
                request.Report.Request.StrategyId);
            return BacktestMonteCarloResult.Cancelled;
        }
    }

    #endregion Monte Carlo

    #region Walk-Forward

    /// <inheritdoc/>
    public async Task<BacktestWalkForwardResult> WalkForwardAsync(
        BacktestWalkForwardRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        // The same single-flight gate a run takes, held for the WHOLE study:
        // the per-run events must never interleave with another run's, and a
        // run started while a walk-forward is in flight refuses exactly as a
        // second run would.
        if (!await _runGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("A backtest run is already in flight; runs refuse concurrent starts.");
        }

        try
        {
            return await WalkForwardLockedAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _runGate.Release();
        }
    }

    private async Task<BacktestWalkForwardResult> WalkForwardLockedAsync(
        BacktestWalkForwardRequest request,
        CancellationToken cancellationToken)
    {
        var strategy = _strategies.Get(request.Template.StrategyId)
            ?? throw new InvalidOperationException(
                $"Strategy '{request.Template.StrategyId}' is unknown to the catalog; the walk-forward is refused, never defaulted.");

        var parameterizations = request.FirstAxis.Values.Count * request.SecondAxis.Values.Count;

        _logger.LogInformation(
            "Backtest walk-forward '{Strategy}': {Folds} fold(s) over a {Parameterizations}-parameterization grid, {Bars} bars (adapter {AdapterId}, seed {Seed})",
            request.Template.StrategyId,
            request.Folds,
            parameterizations,
            request.Template.Bars,
            request.Template.AdapterId,
            request.Template.Seed);

        // The window loads ONCE: every fold replays the SAME series, so the
        // study compares folds of one honest window (and the load's cost
        // stays one walk-back, not one per fold).
        IReadOnlyList<Bar> bars;
        try
        {
            bars = await LoadCandlesAsync(request.Template, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The stop during the load: no fold ever started, so no cancelled
            // event goes out (the stream carries per-run events only) — the
            // cancelled result is the whole answer.
            _logger.LogInformation(
                "Backtest walk-forward '{Strategy}' cancelled during the load; nothing was produced",
                request.Template.StrategyId);
            return new BacktestWalkForwardResult(BacktestWalkForwardOutcome.Cancelled, request, []);
        }

        // The carve refuses a window that cannot host every fold's two honest
        // halves (each at least the run's own minimum) — before any replay
        // starts, nothing half-carved ever exists.
        var splits = request.FoldPlan(bars.Count);

        _logger.LogInformation(
            "Backtest walk-forward '{Strategy}': {Bars} cached bars loaded, {Folds} fold(s) x ({Parameterizations} in-sample replay(s) + 1 out-of-sample replay) to run",
            request.Template.StrategyId,
            bars.Count,
            splits.Count,
            parameterizations);

        var rows = new List<BacktestWalkForwardRow>();
        var outcome = BacktestWalkForwardOutcome.Completed;

        try
        {
            for (var fold = 0; fold < splits.Count; fold++)
            {
                // The cooperative stop BETWEEN folds — the boundary
                // discipline a fold is atomic under: a fold's row exists
                // only when its whole optimize-then-verify pass completed,
                // so a stop keeps every completed fold and abandons the one
                // still optimizing (nothing half-fold exists).
                cancellationToken.ThrowIfCancellationRequested();

                var split = splits[fold];
                var inSampleBars = bars.Skip(split.Offset).Take(split.InSampleBars).ToList();
                var outOfSampleBars = bars.Skip(split.Offset + split.InSampleBars).Take(split.OutOfSampleBars).ToList();

                // The in-sample optimization: every parameterization replays
                // the fold's IS half (a fresh host, ledger and seeded draw
                // each, replays ride no token and complete fully), the best
                // IS net wins, ties stay with the enumeration order.
                var bestIndex = -1;
                var bestNet = 0m;
                var bestFirst = 0m;
                var bestSecond = 0m;
                var index = 0;

                foreach (var firstValue in request.FirstAxis.Values)
                {
                    foreach (var secondValue in request.SecondAxis.Values)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var replayRequest = BacktestSweepRequest.ApplyKnob(
                            BacktestSweepRequest.ApplyKnob(
                                request.Template with { Bars = split.InSampleBars },
                                request.FirstAxis.Parameter,
                                firstValue),
                            request.SecondAxis.Parameter,
                            secondValue);
                        var report = await ReplayAsync(replayRequest, strategy, inSampleBars, publishEvents: false, CancellationToken.None).ConfigureAwait(false);
                        var net = report.Statistics.FinalEquity - report.Request.InitialQuoteAmount;
                        if (bestIndex < 0 || net > bestNet)
                        {
                            bestIndex = index;
                            bestNet = net;
                            bestFirst = firstValue;
                            bestSecond = secondValue;
                        }

                        index++;
                    }
                }

                // The chosen pair rides the fold's OOS half — the honest
                // unseen window the whole study exists to measure.
                var chosenRequest = BacktestSweepRequest.ApplyKnob(
                    BacktestSweepRequest.ApplyKnob(
                        request.Template with { Bars = split.OutOfSampleBars },
                        request.FirstAxis.Parameter,
                        bestFirst),
                    request.SecondAxis.Parameter,
                    bestSecond);
                var chosenReport = await ReplayAsync(chosenRequest, strategy, outOfSampleBars, publishEvents: true, CancellationToken.None).ConfigureAwait(false);

                rows.Add(new BacktestWalkForwardRow(
                    Fold: fold + 1,
                    InSampleBars: split.InSampleBars,
                    OutOfSampleBars: split.OutOfSampleBars,
                    FirstValue: bestFirst,
                    SecondValue: bestSecond,
                    InSampleNet: bestNet,
                    OutOfSampleNet: chosenReport.Statistics.FinalEquity - request.Template.InitialQuoteAmount,
                    OutOfSampleMaxDrawdown: chosenReport.Statistics.MaxDrawdown));
            }
        }
        catch (OperationCanceledException)
        {
            // The stop landed at a boundary: the folds whose whole pass
            // completed stay (each an honest optimize-then-verify fact), the
            // rest never started. The stop is the outcome, never an error.
            outcome = BacktestWalkForwardOutcome.Cancelled;
            _logger.LogInformation(
                "Backtest walk-forward '{Strategy}' cancelled after {Count} fold(s); the completed folds stay",
                request.Template.StrategyId,
                rows.Count);
        }

        _logger.LogInformation(
            "Backtest walk-forward '{Strategy}' {End}: {Count} of {Total} fold(s) replayed, out-of-sample net {OosNet}",
            request.Template.StrategyId,
            outcome == BacktestWalkForwardOutcome.Completed ? "completed" : "cancelled at a boundary",
            rows.Count,
            splits.Count,
            rows.Count > 0 ? rows.Sum(row => row.OutOfSampleNet) : 0m);

        return new BacktestWalkForwardResult(outcome, request, rows);
    }

    #endregion Walk-Forward

    #region Candle Loading

    /// <summary>
    /// Load the requested window from the cache: the newest
    /// <see cref="BacktestRunRequest.Bars"/> bars, oldest first. The cache's
    /// own paging shape (the T-085/T-086 canon) serves at most its own page
    /// cap per call, so the load walks back by open time until the target is
    /// reached or the cache runs dry — the honest shorter window. The
    /// walk-back COLLECTS its pages as fetched (newest page first) and
    /// assembles the ascending series in ONE pass from the oldest page back:
    /// every bar is copied exactly once, instead of re-shifting the whole
    /// accumulated list on every page (the prepend cost grew as
    /// O(N² / page cap)).
    /// </summary>
    private async Task<List<Bar>> LoadCandlesAsync(
        BacktestRunRequest request,
        CancellationToken cancellationToken)
    {
        var newest = await _candles.GetLatestAsync(
            request.AdapterId,
            request.Symbol,
            request.Interval,
            request.Bars,
            cancellationToken).ConfigureAwait(false);

        // An empty newest answer ends the walk immediately: the cache has
        // nothing — the honest zero-activity baseline, never a crash.
        if (newest.Count == 0)
        {
            return [];
        }

        // The pages as fetched, newest page first. Each page is ascending
        // (the cache's own order) and lands entirely older than every page
        // collected before it.
        var pages = new List<IReadOnlyList<Bar>> { newest };
        var loaded = newest.Count;
        var anchor = newest[0].Time;

        while (loaded < request.Bars)
        {
            // The cooperative stop: checked between pages.
            cancellationToken.ThrowIfCancellationRequested();

            var page = await _candles.GetOlderThanAsync(
                request.AdapterId,
                request.Symbol,
                request.Interval,
                anchor,
                request.Bars - loaded,
                cancellationToken).ConfigureAwait(false);
            if (page.Count == 0)
            {
                break;
            }

            // Each page lands entirely older than the current anchor, so
            // walking the collected pages from the oldest back keeps the
            // assembled series ascending.
            pages.Add(page);
            loaded += page.Count;
            anchor = page[0].Time;
        }

        // The ONE assembly pass: walk the collected pages from the oldest
        // back, every bar copied exactly once — no re-shifting, the whole
        // walk costs O(loaded) element moves instead of O(N² / page cap).
        var bars = new List<Bar>(capacity: request.Bars);
        for (var index = pages.Count - 1; index >= 0; index--)
        {
            bars.AddRange(pages[index]);
        }

        return bars;
    }

    #endregion Candle Loading

    #region Replay

    /// <summary>Feed the loaded series into the host's input, pacing one candle
    /// at a time (T-264): each write is followed by an honest yield, so the pump
    /// consumes the candle and the signal reader applies its stamp while the
    /// feed still has bars left — the in-flight queues stay near-empty across
    /// long replays. The unbounded write itself completes synchronously per
    /// candle; the token still stops the walk.</summary>
    private static async Task FeedBarsAsync(
        StrategyHost host,
        IReadOnlyList<Bar> bars,
        BacktestRunRequest request,
        CancellationToken cancellationToken)
    {
        foreach (var bar in bars)
        {
            await host.CandleInput
                .WriteAsync(new ClosedCandle(request.Symbol, request.Interval, bar), cancellationToken)
                .ConfigureAwait(false);

            // The unbounded write never pends, so without this yield the feed
            // would run the whole series into the channel in one unbroken
            // stretch — the exact O(N) backlog the concurrency removes.
            await Task.Yield();
        }

        // The whole series is in: the pump drains, consumes and completes.
        host.CandleInput.TryComplete();
    }

    /// <summary>
    /// Apply one stamped signal to the ledger: a stance CHANGE triggers the
    /// transition (the paper lane never auto-retries a refused placement —
    /// the human re-places; the replay records the refusal once and moves
    /// on). Neutral is the honest hold: no opinion closes nothing.
    /// </summary>
    private void ApplySignal(
        BacktestLedger ledger,
        Random prng,
        StrategyStance previousStance,
        StrategySignal signal,
        BacktestRunRequest request,
        ref StrategyStance currentStance)
    {
        var stance = signal.Stance;
        var bar = signal.Candle;
        currentStance = stance;

        if (stance == previousStance || stance == StrategyStance.Neutral)
        {
            // No change, or the warmup's honest no-op: the mark only.
            ledger.Mark(bar.Time, bar.Close);
            return;
        }

        var fillTime = bar.Time;

        if (stance == StrategyStance.Long && previousStance != StrategyStance.Long)
        {
            EnterOrExtendLong(ledger, prng, request, bar, fillTime);
        }
        else if (stance == StrategyStance.Flat && previousStance == StrategyStance.Long)
        {
            ExitLong(ledger, prng, request, bar, fillTime);
        }

        ledger.Mark(bar.Time, bar.Close);
    }

    /// <summary>
    /// The long-side transition: the targeted quantity (equity fraction
    /// marked at this candle's close) minus the current exposure, executed
    /// as one market fill at the reference close skewed by the seeded
    /// adverse slippage. A refusal records the fact and leaves the book.
    /// </summary>
    private void EnterOrExtendLong(
        BacktestLedger ledger,
        Random prng,
        BacktestRunRequest request,
        Bar bar,
        DateTimeOffset fillTime)
    {
        var targetQuantity = ledger.LongTargetQuantity(bar.Close, request.LongNotionalFraction);
        var delta = targetQuantity - ledger.SignedQuantity;
        if (delta <= 0m)
        {
            // Already at or beyond target (a refused earlier entry, or the
            // net points the wrong way): the stance says hold; nothing
            // would default the position forward.
            return;
        }

        var fillPrice = SkewAdversely(prng, request.SlippageValue, OrderSide.Buy, bar.Close);
        if (ledger.FillSide(OrderSide.Buy, delta, fillPrice, fillTime))
        {
            _logger.LogDebug(
                "Backtest fill: buy {Quantity} at {Price} on candle {Time}",
                delta,
                fillPrice,
                fillTime);
        }
        else
        {
            _logger.LogInformation(
                "Backtest refusal: the long entry at {Price} was refused by the paper portfolio (recorded, book untouched)",
                fillPrice);
        }
    }

    /// <summary>
    /// The exit transition: the whole net position liquidates (the stance's
    /// stand-aside), one market fill at the reference close skewed by the
    /// seeded adverse slippage. Nothing to liquidate means a no-op (an
    /// earlier entry refused): honest absence, not a synthesized fill.
    /// </summary>
    private void ExitLong(
        BacktestLedger ledger,
        Random prng,
        BacktestRunRequest request,
        Bar bar,
        DateTimeOffset fillTime)
    {
        var liquidate = Math.Abs(ledger.SignedQuantity);
        if (liquidate <= 0m)
        {
            return;
        }

        var fillPrice = SkewAdversely(prng, request.SlippageValue, OrderSide.Sell, bar.Close);
        if (ledger.FillSide(OrderSide.Sell, liquidate, fillPrice, fillTime))
        {
            _logger.LogDebug(
                "Backtest fill: sell {Quantity} at {Price} on candle {Time}",
                liquidate,
                fillPrice,
                fillTime);
        }
        else
        {
            _logger.LogInformation(
                "Backtest refusal: the exit sell at {Price} was refused by the paper portfolio (recorded, book untouched)",
                fillPrice);
        }
    }

    /// <summary>
    /// Skew the reference close toward the adverse side by up to the
    /// configured magnitude: a buy pays up to |slippage| above it, a sell
    /// up to |slippage| below it — a fraction drawn from the run's seeded
    /// generator. A zero magnitude reproduces the exact reference close
    /// (the paper ledger's exact fill price) and consumes no draw, so a
    /// slippage-less replay is seed-independent by construction while the
    /// same seed always reproduces the same curve whatever the policy.
    /// </summary>
    private static decimal SkewAdversely(
        Random prng,
        decimal slippageValue,
        OrderSide side,
        decimal referenceClose)
    {
        if (slippageValue == 0m)
        {
            return referenceClose;
        }

        var fraction = (decimal)prng.NextDouble();
        var skew = slippageValue * fraction;
        var skewed = side == OrderSide.Buy ? referenceClose + skew : referenceClose - skew;

        // The skew must never invert the order book's honesty: an adverse
        // price stays positive for a positive reference.
        if (skewed <= 0m)
        {
            throw new InvalidOperationException(
                $"The seeded slippage moved the fill price to a non-positive value ({skewed}); the run is refused rather than degraded.");
        }

        return skewed;
    }

    #endregion Replay
}
