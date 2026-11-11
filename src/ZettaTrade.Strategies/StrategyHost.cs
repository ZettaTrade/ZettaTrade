namespace ZettaTrade.Strategies;

using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The candle-close host of the strategy seam (T-111): one evaluation
/// loop consuming CLOSED candles from an owned input channel and emitting
/// one stamped <see cref="StrategySignal"/> per (strategy, symbol,
/// interval) evaluation into an owned output channel. The loop is
/// sequential - candles are appended per series, every registered strategy
/// is evaluated over the series' retained window in catalog order, and a
/// strategy that throws is quarantined for the session (logged loudly,
/// the rest of the scan keeps working - the T-054 tolerance canon at
/// runtime). The output channel is inert output for later UI tasks: the
/// consumer reads it, never feeds it into orders - no order placement, no
/// risk bypass, the human decides.
/// </summary>
/// <remarks>
/// The host sits between the app wiring and the strategies: the upstream
/// candle owner (the hosting task, next on the board) writes final
/// candles into <see cref="CandleInput"/> and drains
/// <see cref="Signals"/>. The pump ends when the input completes or the
/// token fires, then the output completes - a drain-first test reads the
/// emission exactly once.
/// </remarks>
public sealed class StrategyHost
{
    #region Constants

    /// <summary>
    /// The retained bars per (symbol, interval) series the default
    /// construction applies: a covering window for the standard warmup
    /// needs (a strategy wanting more history answers
    /// <see cref="StrategyStance.Neutral"/> honestly rather than fabricate).
    /// </summary>
    public const int DefaultWindowBars = 256;

    #endregion

    #region Private Fields

    private readonly ILogger<StrategyHost> _logger;
    private readonly IReadOnlyList<IStrategy> _strategies;
    private readonly int _windowBars;

    /// <summary>The series windows keyed by (symbol, interval); the pump is the only reader and writer.</summary>
    private readonly Dictionary<SeriesKey, SeriesWindow> _series = [];

    /// <summary>The strategies that threw during evaluation, quarantined for the session after one warning.</summary>
    private readonly HashSet<string> _quarantined = new(StringComparer.Ordinal);

    /// <summary>The closed-candle input the composition feeds (final candles, ascending per series).</summary>
    private readonly Channel<ClosedCandle> _candles =
        Channel.CreateUnbounded<ClosedCandle>(new UnboundedChannelOptions { SingleWriter = false });

    /// <summary>The stamped signal output the consumer drains (one event per strategy per closed candle).</summary>
    private readonly Channel<StrategySignal> _signals =
        Channel.CreateUnbounded<StrategySignal>(new UnboundedChannelOptions { SingleWriter = true });

    #endregion

    #region Constructors

    /// <summary>Construct the host over one strategy set (the catalog's snapshot) and the retained window length.</summary>
    /// <param name="strategies">The strategies to run, in emission order (the catalog's load order).</param>
    /// <param name="logger">The host's logger.</param>
    /// <param name="windowBars">The retained closed bars per series.</param>
    public StrategyHost(
        IReadOnlyList<IStrategy> strategies,
        ILogger<StrategyHost> logger,
        int windowBars = DefaultWindowBars)
    {
        _strategies = strategies ?? throw new ArgumentNullException(nameof(strategies));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (windowBars < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(windowBars), windowBars, "The host retains at least one bar per series.");
        }

        _windowBars = windowBars;
    }

    #endregion

    #region Channels

    /// <summary>
    /// The candle input: the composition writes final (closed) candles here
    /// and completes the writer to end the pump. Candles for any number of
    /// (symbol, interval) series may interleave freely.
    /// </summary>
    public ChannelWriter<ClosedCandle> CandleInput => _candles.Writer;

    /// <summary>
    /// The signal output: one stamped event per registered strategy per
    /// accepted closed candle, in catalog order. Read only - the host is
    /// the writer; the consumer surfaces the stances, never trades on them
    /// (no order placement, no risk bypass: the human decides).
    /// </summary>
    public ChannelReader<StrategySignal> Signals => _signals.Reader;

    #endregion

    #region Pump

    /// <summary>
    /// Run the evaluation until the input writer completes or the token
    /// fires; then complete the output so a consumer drains exactly what
    /// the run emitted. A cancelled run is a completed run to the reader -
    /// cancellation is the stop, never an error surfaced downstream.
    /// </summary>
    /// <param name="ct">The cancellation token stopping the pump.</param>
    public async Task RunAsync(CancellationToken ct = default)
    {
        try
        {
            await foreach (var candle in _candles.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                EvaluateAndEmit(candle);
            }
        }
        catch (OperationCanceledException)
        {
            // The token is the stop: the downstream sees a completed
            // channel, not an exception.
        }
        finally
        {
            _signals.Writer.TryComplete();
        }
    }

    private void EvaluateAndEmit(ClosedCandle candle)
    {
        var key = new SeriesKey(candle.Symbol, candle.Interval);
        if (!_series.TryGetValue(key, out var window))
        {
            window = new SeriesWindow(_windowBars);
            _series[key] = window;
        }

        if (!window.IsAdvancing(candle.Bar))
        {
            _logger.LogDebug(
                "The closed candle {Time} for {Series} does not advance the series and is skipped",
                candle.Bar.Time,
                $"{candle.Symbol}@{candle.Interval}");
            return;
        }

        window.Append(candle.Bar);
        var bars = window.Snapshot();
        var context = new StrategyContext(
            candle.Symbol.Normalized,
            candle.Interval.ToString(),
            ToSdkBars(bars));

        foreach (var strategy in _strategies)
        {
            // The stamps come from the host's own facts, never from the
            // strategy: an implementation cannot fabricate an identity.
            var id = strategy.Id;
            if (_quarantined.Contains(id))
            {
                continue;
            }

            StrategyStance stance;
            try
            {
                stance = strategy.Evaluate(context);
            }
            catch (Exception ex)
            {
                _quarantined.Add(id);
                _logger.LogError(
                    ex,
                    "Strategy '{Id}' threw during evaluation and is quarantined for the session",
                    id);
                continue;
            }

            _signals.Writer.TryWrite(
                new StrategySignal(id, candle.Symbol, candle.Interval, candle.Bar, stance));
        }
    }

    #endregion

    #region SDK Context Adapter

    /// <summary>
    /// The app-to-SDK bar adaptation (T-168): the strategy contract rides
    /// the SDK-owned <see cref="ZettaTrade.Plugins.Abstractions.IndicatorBar"/>
    /// OHLC shape, so the host converts the retained window once per
    /// evaluation - the T-163 grammar (the Charting pipeline adapts once
    /// per compute) applied to the strategy seam. The allocation rides the
    /// closed-candle cadence, never the per-strategy loop: one array per
    /// series evaluation serves every registered strategy.
    /// </summary>
    private static ZettaTrade.Plugins.Abstractions.IndicatorBar[] ToSdkBars(IReadOnlyList<Core.Entities.Bar> bars)
    {
        var sdkBars = new ZettaTrade.Plugins.Abstractions.IndicatorBar[bars.Count];
        for (var i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            sdkBars[i] = new ZettaTrade.Plugins.Abstractions.IndicatorBar(bar.Open, bar.High, bar.Low, bar.Close);
        }

        return sdkBars;
    }

    #endregion

    #region Series Key

    /// <summary>The (symbol, interval) identity the per-series windows are keyed by.</summary>
    private readonly record struct SeriesKey(Symbol Symbol, TimeFrame Interval);

    #endregion
}
