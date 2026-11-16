namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// The correlation matrix computer (T-252): an on-demand pass over a
/// chosen symbol set — every symbol's latest window+1 bars read from the
/// local candle cache (T-085) OFF the UI thread, the N×N Pearson matrix
/// assembled by the pure <see cref="CorrelationMath"/> on the pool. The
/// reads share a bounded two-slot budget (the MtfTrend discipline): a
/// 20-symbol matrix walks in politely, never one cache storm and never a
/// frozen panel — the window's command awaits the returned task while
/// the UI thread stays free.
/// <para>
/// App-lifetime singleton registered beside the screener's panels; the
/// pass is stateless, so every compute answers fresh (no pipeline, no
/// cadence — the correlation matrix reads on demand, not on a pulse).
/// Unparsable symbol texts are refused with a warning (the screener's
/// refuse-not-default precedent): a text the active exchange's parser
/// never accepts owns no cache key, and a fabricated row would lie.
/// </para>
/// </summary>
public sealed class CorrelationService
{
    #region Constants

    /// <summary>The cache-read budget: at most this many series reads in flight (the bounded-concurrency discipline).</summary>
    internal const int ReadBudget = 2;

    /// <summary>
    /// The roster cap: the matrix answers at most this many symbols (the
    /// list order's first ones, ordinal-sorted) — a 100-symbol watchlist
    /// would compute, but the panel's grid would be unreadable noise; the
    /// snapshot's truncation flag carries the honesty.
    /// </summary>
    public const int MaxSymbols = 32;

    #endregion Constants

    #region Private Fields

    /// <summary>The facade: its <c>Current</c> adapter is the pass's snapshot for every cache key.</summary>
    private readonly ActiveExchangeService _activeExchanges;

    /// <summary>The local candle cache (T-085) every series read rides.</summary>
    private readonly ICandleCache _cache;

    /// <summary>The read budget gate: at most <see cref="ReadBudget"/> series reads in flight.</summary>
    private readonly SemaphoreSlim _readBudget = new(ReadBudget, ReadBudget);

    /// <summary>The computer's own logging lane.</summary>
    private readonly ILogger<CorrelationService> _logger;

    #endregion Private Fields

    #region Constructor

    /// <summary>
    /// Create the computer over the facade and the cache. Nothing arms
    /// here: the pass is on-demand, the window's compute command calls
    /// <see cref="ComputeAsync"/> directly.
    /// </summary>
    public CorrelationService(
        ActiveExchangeService activeExchanges,
        ICandleCache cache,
        ILogger<CorrelationService> logger)
    {
        _activeExchanges = activeExchanges ?? throw new ArgumentNullException(nameof(activeExchanges));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #endregion Constructor

    #region Compute

    /// <summary>
    /// Compute one pass: every symbol's series read under the read budget
    /// (off the UI thread), the matrix assembled on the pool. The pass
    /// captures the facade's <c>Current</c> adapter once — every cache key
    /// rides the same snapshot. A symbol whose cache series is short or
    /// absent still takes its matrix slot: its cells answer the honest
    /// dash, the symbol's presence in the set is real.
    /// </summary>
    /// <param name="symbolTexts">The chosen set's symbol texts (the watchlist's own order; duplicates collapse).</param>
    /// <param name="frame">The time frame every series rides.</param>
    /// <param name="window">The requested returns window in bars (the reads fetch window+1 closes).</param>
    /// <param name="ct">The pass's cancellation token.</param>
    /// <returns>The pass's snapshot.</returns>
    /// <exception cref="ArgumentNullException">The symbol texts are null.</exception>
    public async Task<CorrelationSnapshot> ComputeAsync(
        IReadOnlyList<string> symbolTexts,
        TimeFrame frame,
        int window,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(symbolTexts);
        if (window < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(window), window, "The returns window must be at least 2.");
        }

        // The pass's symbol order: distinct texts, ordinal-sorted (the
        // MtfTrend set discipline) — the matrix order is deterministic
        // whatever the watchlist document's row order did.
        var parsed = new List<(string Text, Symbol Symbol)>();
        foreach (var text in symbolTexts
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static text => text, StringComparer.Ordinal))
        {
            if (parsed.Count >= MaxSymbols)
            {
                break;
            }

            try
            {
                parsed.Add((text, Symbol.Parse(text)));
            }
            catch (ArgumentException)
            {
                _logger.LogWarning(
                    "Correlation: the text '{SymbolText}' does not parse on the active exchange; the pass refuses it",
                    text);
            }
        }

        var adapter = _activeExchanges.Current;
        var returns = await Task.WhenAll(parsed
            .Select(entry => Task.Run(() => ReadReturnsAsync(entry.Symbol, adapter, frame, window, ct), ct))
            .ToArray());

        // The matrix assembly rides the pool too — the N×N pairing over
        // the shared-key scans never touches the UI thread.
        var minOverlap = CorrelationMath.MinOverlapReturns(window);
        var matrix = await Task.Run(
            () => CorrelationMath.Matrix(returns, minOverlap),
            ct);

        return new CorrelationSnapshot(
            frame,
            window,
            [.. parsed.Select(static entry => entry.Symbol.Normalized)],
            matrix,
            Truncated: parsed.Count >= MaxSymbols && symbolTexts
                .Where(static text => !string.IsNullOrWhiteSpace(text))
                .Distinct(StringComparer.Ordinal)
                .Count() > MaxSymbols);
    }

    /// <summary>
    /// One symbol's cache read under the read budget: the latest
    /// window+1 bars (the +1 buys the returns' prior closes), the pure
    /// log-return map over them.
    /// </summary>
    private async Task<Dictionary<DateTimeOffset, double>> ReadReturnsAsync(
        Symbol symbol,
        IMarketDataAdapter adapter,
        TimeFrame frame,
        int window,
        CancellationToken ct)
    {
        await _readBudget.WaitAsync(ct);
        try
        {
            var bars = await _cache.GetLatestAsync(adapter.Id, symbol, frame, window + 1, ct);
            return CorrelationMath.LogReturns(bars);
        }
        finally
        {
            _readBudget.Release();
        }
    }

    #endregion Compute
}
