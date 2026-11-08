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
}
