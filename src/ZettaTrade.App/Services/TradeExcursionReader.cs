namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// The MAE/MFE derivation lane of the trade-statistics window (T-251): it
/// reads the trade windows' candles from the local candle cache (T-085,
/// read-only, never the network) and derives each trade's excursions
/// through <see cref="TradeExcursionMath"/>.
/// <para>
/// The cache keys its series by (adapter id, symbol, interval). A trade's
/// own venue key is tried first — a live trade's exchange id IS the cache
/// key the chart wrote; a paper trade's "paper" key usually holds nothing
/// (the paper lane prices its fills off the venue the market-data facade
/// stands on), so the candidates fall through to the app's market-data
/// lanes. The first candidate whose series holds ANY bar of the symbol's
/// whole union window wins for the symbol — no stitching across candidates
/// (two venues' candles never mix into one series), and a symbol no
/// candidate covers contributes no excursions: the honest dash.
/// </para>
/// <para>
/// The window read walks the cache BACKWARD from the window's end through
/// <see cref="ICandleCache.GetOlderThanAsync"/> pages (the T-085 walk-back
/// contract, the shape the backtest loader rides) until the window's start
/// is covered or the series runs dry; a page cap bounds the walk so an
/// oversized eviction cap can never pin an unbounded read. The series is
/// one H1 page set per symbol — a six-figure trade history over a year of
/// bars costs a couple dozen bounded queries per symbol, all off the
/// trading hot path.
/// </para>
/// </summary>
public sealed class TradeExcursionReader(ICandleCache cache)
{
    /// <summary>Interval of the excursion bars: the app's reference frame (the market reference close rides the same H1 bars).</summary>
    private const TimeFrame ExcursionTimeFrame = TimeFrame.H1;

    /// <summary>Bars per walk-back page: the store clamps to the same cap, so one page is one bounded query.</summary>
    private const int WalkPageSize = 1_000;

    /// <summary>
    /// The walk-back page cap: 300k bars is above the shipped eviction cap
    /// (100k per series), so a normal cache walks dry long before the cap;
    /// a settings-sized monster series still reads bounded.
    /// </summary>
    private const int MaxWalkPages = 300;

    private readonly ICandleCache _cache = cache ?? throw new ArgumentNullException(nameof(cache));

    /// <summary>
    /// Derive the excursions for a batch of trade facts. The facts group by
    /// symbol; each group reads its candles once and derives every trade's
    /// window from the same series. Trades without candle coverage for
    /// their window produce no fact.
    /// </summary>
    /// <param name="trades">The accumulator's per-trade facts (any order).</param>
    /// <param name="candidateAdapterIds">
    /// The adapter ids the reads fall through, in priority order — the
    /// trades' own venue ids first, then the app's market-data lanes.
    /// </param>
    /// <param name="cancellationToken">Cancellation rides the scan's token.</param>
    public async Task<IReadOnlyList<TradeExcursionFact>> DeriveAsync(
        IReadOnlyList<TradeStatsTradeFact> trades,
        IReadOnlyList<string> candidateAdapterIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trades);
        ArgumentNullException.ThrowIfNull(candidateAdapterIds);

        if (trades.Count == 0)
        {
            return [];
        }

        var candidates = candidateAdapterIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length == 0)
        {
            return [];
        }

        var results = new List<TradeExcursionFact>();
        foreach (var symbolGroup in trades.GroupBy(trade => trade.Symbol, StringComparer.Ordinal))
        {
            var facts = symbolGroup.ToArray();
            var window = (From: facts.Min(trade => trade.OpenedAt), To: facts.Max(trade => trade.ClosedAt));
            if (window.To <= window.From)
            {
                continue;
            }

            foreach (var adapterId in candidates)
            {
                var bars = await ReadWindowAsync(adapterId, symbolGroup.Key, window.From, window.To, cancellationToken);
                if (bars.Count == 0)
                {
                    continue;
                }

                foreach (var trade in facts)
                {
                    var slice = TradeExcursionMath.SliceWindow(bars, trade.OpenedAt, trade.ClosedAt);
                    var derived = TradeExcursionMath.Derive(trade.EntryPrice, trade.IsLong, slice);
                    if (derived is { } excursion)
                    {
                        results.Add(new TradeExcursionFact(
                            trade.ExchangeId,
                            trade.ClosingOrderId,
                            excursion.Mae,
                            excursion.Mfe));
                    }
                }

                // The first candidate with any bars wins for the whole
                // symbol: no stitching across adapters, never a mixed series.
                break;
            }
        }

        return results;
    }

    /// <summary>
    /// One adapter's bars over [<paramref name="from"/>, <paramref name="to"/>):
    /// the walk-back pages gathered oldest-first, trimmed to the window's
    /// start. Empty when the series holds nothing inside the window.
    /// </summary>
    private async Task<IReadOnlyList<Bar>> ReadWindowAsync(
        string adapterId,
        string symbol,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var parsed = Symbol.Parse(symbol);
        List<IReadOnlyList<Bar>>? pages = null;
        var before = to;
        for (var page = 0; page < MaxWalkPages; page++)
        {
            var bars = await _cache.GetOlderThanAsync(
                adapterId,
                parsed,
                ExcursionTimeFrame,
                before,
                WalkPageSize,
                cancellationToken);
            if (bars.Count == 0)
            {
                break;
            }

            pages ??= [];
            pages.Add(bars);

            // Each page is oldest-first and disjoint from the last (the
            // strict `before` bound); the walk stops once the window's
            // start sits inside the gathered coverage.
            var oldest = bars[0].Time;
            if (oldest <= from)
            {
                break;
            }

            before = oldest;
        }

        if (pages is null)
        {
            return [];
        }

        // The pages descend in time while each ascends inside itself:
        // reversing the page order yields one globally ascending series.
        var ascending = pages
            .AsEnumerable()
            .Reverse()
            .SelectMany(static page => page)
            .Where(bar => bar.Time >= from)
            .ToArray();
        return ascending;
    }
}
