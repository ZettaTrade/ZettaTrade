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

        // Deterministic seed: the replay's only stochastic input (the slippage
        // draw) derives from the explicit seed — xoshiro256** (the explicit-
        // seed Random ctor), stable across runs of one runtime.
        var prng = new Random(request.Seed);

        var processed = 0;
        var pumpTask = Task.CompletedTask;
        BacktestRunResult result;

        try
        {
            var bars = await LoadCandlesAsync(request, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Backtest '{Strategy}' on {Symbol} {Interval} (adapter {AdapterId}, seed {Seed}): {Bars} cached bars loaded",
                request.StrategyId,
                request.Symbol.Normalized,
                request.Interval,
                request.AdapterId,
                request.Seed,
                bars.Count);

            // The T-111 canon, consumed exactly as registered: a real host runs
            // the candle-close evaluation loop over its own retained window; the
            // replay feeds it the loaded series and reads the stamped signals.
            var host = new StrategyHost([strategy], _hostLogger);

            var ledger = new BacktestLedger(request.AdapterId, request.Symbol, _feeSchedules, request.InitialQuoteAmount);
            Publish(new BacktestRunEvent(BacktestRunEventKind.Started, request.Bars, BarsProcessed: 0, Report: null));

            // The pump runs with the SAME token: cancellation is the stop (the
            // host completes its output; the reader sees a completed channel,
            // not an exception). The orchestrating loops below inspect the token
            // cooperatively per step and per signal.
            pumpTask = host.RunAsync(cancellationToken);

            var previousStance = StrategyStance.Neutral;

            await FeedBarsAsync(host, bars, request, cancellationToken).ConfigureAwait(false);

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

            // The final mark rides the last candle's close (or the empty
            // honest zero-activity baseline when the cache served nothing).
            var finalClose = bars.Count > 0 ? bars[^1].Close : 0m;
            var report = new BacktestReport(
                request,
                ledger.EquityCurve,
                ledger.Fills,
                ledger.Refusals,
                ledger.ComputeStatistics(finalClose));

            result = new BacktestRunResult(BacktestRunOutcome.Completed, report);
            Publish(new BacktestRunEvent(
                BacktestRunEventKind.Completed,
                request.Bars,
                bars.Count,
                report));

            _logger.LogInformation(
                "Backtest '{Strategy}' completed: {Fills} fill(s), {Refusals} refusal(s), {Trades} closed trade(s), final equity {FinalEquity}",
                request.StrategyId,
                report.Statistics.Fills,
                report.Refusals.Count,
                report.Statistics.Trades,
                report.Statistics.FinalEquity);
        }
        catch (OperationCanceledException)
        {
            // The stop is the outcome: the cancelled run publishes the
            // cancelled event and produces NO report and NO artifacts.
            result = BacktestRunResult.Cancelled;
            Publish(new BacktestRunEvent(BacktestRunEventKind.Cancelled, request.Bars, processed, Report: null));
            _logger.LogInformation(
                "Backtest '{Strategy}' cancelled after {Processed} candle(s); nothing was produced",
                request.StrategyId,
                processed);
        }
        finally
        {
            // The pump ends on its own (input completed, or its token fired)
            // and cannot throw — its RunAsync swallow-the-stop canon.
            await pumpTask.ConfigureAwait(false);
        }

        return result;
    }

    #endregion Run

    #region Candle Loading

    /// <summary>
    /// Load the requested window from the cache: the newest
    /// <see cref="BacktestRunRequest.Bars"/> bars, oldest first. The cache's
    /// own paging shape (the T-085/T-086 canon) serves at most its own page
    /// cap per call, so the load walks back by open time until the target is
    /// reached or the cache runs dry — the honest shorter window.
    /// </summary>
    private async Task<List<Bar>> LoadCandlesAsync(
        BacktestRunRequest request,
        CancellationToken cancellationToken)
    {
        var bars = new List<Bar>(capacity: request.Bars);

        var newest = await _candles.GetLatestAsync(
            request.AdapterId,
            request.Symbol,
            request.Interval,
            request.Bars,
            cancellationToken).ConfigureAwait(false);
        bars.AddRange(newest);

        // An empty newest answer ends the walk immediately: the cache has
        // nothing — the honest zero-activity baseline, never a crash.
        while (bars.Count > 0 && bars.Count < request.Bars)
        {
            // The cooperative stop: checked between pages.
            cancellationToken.ThrowIfCancellationRequested();

            var page = await _candles.GetOlderThanAsync(
                request.AdapterId,
                request.Symbol,
                request.Interval,
                bars[0].Time,
                request.Bars - bars.Count,
                cancellationToken).ConfigureAwait(false);
            if (page.Count == 0)
            {
                break;
            }

            // Each page lands entirely older than the current front, so
            // always prepending keeps the series ascending.
            bars.InsertRange(0, page);
        }

        return bars;
    }

    #endregion Candle Loading

    #region Replay

    /// <summary>Feed the loaded series into the host's input (unbounded channel: the
    /// writer completes synchronously per candle; the token still stops the walk).</summary>
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
