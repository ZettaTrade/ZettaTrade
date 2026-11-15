namespace ZettaTrade.Backtest;

using System.Reactive.Linq;

/// <summary>
/// The offline deterministic replay harness of the strategy seam (T-113).
/// A run loads its candle window from the local cache, replays the closed
/// candles through the exact T-111 host/contract shapes, converts stance
/// changes into fills that ride the paper ledger's T-051-corrected
/// bookkeeping, and produces the equity report. Entirely offline: no
/// adapters, no rate limiter, no trading surface — the backtest can never
/// touch a trading adapter's order lane.
/// </summary>
public interface IBacktestService
{
    /// <summary>
    /// Run one deterministic backtest. The same request (strategy, series,
    /// seed, policy) over the same cache produces a byte-identical report —
    /// the run consults no wall clock and no unseeded randomness. A
    /// cancelled run ends cooperatively and returns the cancelled
    /// <see cref="BacktestRunResult"/> (no report, no artifacts).
    /// </summary>
    /// <param name="request">The deterministic input (validated first, invalid shapes are refused).</param>
    /// <param name="cancellationToken">The cooperative stop token of the replay.</param>
    /// <returns>The outcome plus the report when completed.</returns>
    /// <exception cref="ArgumentNullException">The request is missing.</exception>
    /// <exception cref="InvalidOperationException">The strategy id is unknown, or another run is still in flight.</exception>
    /// <exception cref="ArgumentException">A required identity is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A run bound is violated.</exception>
    Task<BacktestRunResult> RunAsync(
        BacktestRunRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The per-run state stream: Started once, Progress at a fixed stride,
    /// the terminal Completed (report attached) or Cancelled. A UI listener
    /// subscribes behind ObserveOnUiThread and may subscribe any time; a
    /// run refuses a second concurrent start, so runs never interleave on
    /// the stream.
    /// </summary>
    IObservable<BacktestRunEvent> Events { get; }

    /// <summary>
    /// Run one deterministic parameter sweep over a two-knob grid (T-212):
    /// the candle window loads ONCE from the template's series, then every
    /// grid parameterization replays it — a fresh host, ledger and seeded
    /// draw per parameterization, the existing per-run events flowing for
    /// each (no sweep-level events exist; the caller knows its own grid).
    /// The sweep holds the same single-flight gate for its whole life (runs
    /// never interleave on the stream, and a run refuses a start while a
    /// sweep is in flight and vice versa). The cancellation is a boundary
    /// stop: each parameterization rides no token and replays to
    /// completion, the token is checked BETWEEN parameterizations, and a
    /// stop carries every completed row — nothing half-run ever exists, and
    /// a replay is never interrupted mid-bars.
    /// </summary>
    /// <param name="request">The deterministic sweep input (validated first, invalid shapes are refused).</param>
    /// <param name="cancellationToken">The cooperative stop token of the sweep (honored between parameterizations).</param>
    /// <returns>The outcome, the sweep's request and the ranked rows (the completed rows on a stop).</returns>
    /// <exception cref="ArgumentNullException">The request is missing.</exception>
    /// <exception cref="InvalidOperationException">The strategy id is unknown, or another run or sweep is still in flight.</exception>
    /// <exception cref="ArgumentException">A required identity is blank, or both axes name the same knob.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A run bound or an axis bound is violated.</exception>
    Task<BacktestSweepResult> SweepAsync(
        BacktestSweepRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resample one completed run's trade sequence (T-254): the report's
    /// closed trades extract from its own fills (the ledger's exact
    /// average-price bookkeeping) and every path bootstrap-redraws the whole
    /// sequence with replacement through one seeded generator, folding the
    /// equity-path distribution — the final-equity and max-drawdown
    /// percentiles plus how many paths end below the starting balance. The
    /// compute is pure and rides the worker lane (off-UI): it consults no
    /// cache, no event stream and no single-flight gate — the report is
    /// immutable and a sampling may overlap a replay honestly. A cancelled
    /// sampling answers the cancelled result (no distribution, no numbers).
    /// </summary>
    /// <param name="request">The deterministic sampling input (validated first, invalid shapes are refused).</param>
    /// <param name="cancellationToken">The cooperative stop token of the fold (honored between paths).</param>
    /// <returns>The outcome plus the folded distribution when completed.</returns>
    /// <exception cref="ArgumentNullException">The request is missing.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A sampling bound or the draw budget is violated.</exception>
    /// <exception cref="InvalidOperationException">The report closed no trades, or its statistics disagree with its own fills.</exception>
    Task<BacktestMonteCarloResult> MonteCarloAsync(
        BacktestMonteCarloRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Walk-forward the grid over the cached window (T-254): the window
    /// loads ONCE from the template's series, carves into
    /// <see cref="BacktestWalkForwardRequest.Folds"/> consecutive segments
    /// (the honest even carve, the remainder bars unused), every segment
    /// splits 50/50 into its in-sample head and out-of-sample tail, every
    /// grid parameterization replays the in-sample half and the best IS net
    /// (ties by enumeration order) replays the out-of-sample half — the
    /// chosen pair's honest unseen window. The single-flight gate covers the
    /// whole study (runs never interleave on the stream; a run or sweep
    /// started while a walk-forward is in flight refuses exactly as a second
    /// run would). The event discipline is the T-263 shape's own: the
    /// in-sample optimization replays are SILENT (they are the optimization's
    /// internals — P×F event cycles would re-count no honest surface), and
    /// each fold's out-of-sample replay publishes its per-run event shape —
    /// one event cycle per fold row, exactly a sweep row's own. The
    /// cancellation is a boundary
    /// stop: replays never interrupt mid-bars and a stop keeps every fold
    /// whose out-of-sample replay completed — nothing half-run ever exists.
    /// A loaded window that cannot host every fold's two honest halves is
    /// refused with its own named counts before any replay starts.
    /// </summary>
    /// <param name="request">The deterministic study input (validated first, invalid shapes are refused).</param>
    /// <param name="cancellationToken">The cooperative stop token of the study (honored between replays).</param>
    /// <returns>The outcome, the study's request and the chronological fold rows (the completed folds on a stop).</returns>
    /// <exception cref="ArgumentNullException">The request is missing.</exception>
    /// <exception cref="InvalidOperationException">The strategy id is unknown, another run or sweep is still in flight, or the loaded window cannot host the folds.</exception>
    /// <exception cref="ArgumentException">A required identity is blank, or both axes name the same knob.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A run bound, an axis bound or the fold bound is violated.</exception>
    Task<BacktestWalkForwardResult> WalkForwardAsync(
        BacktestWalkForwardRequest request,
        CancellationToken cancellationToken = default);
}
