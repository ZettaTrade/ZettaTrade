namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// The cross-venue spread sampler (T-253): one on-demand pass that asks
/// every REGISTERED adapter (the facade's fleet — Binance, Bybit, OKX —
/// the active choice is just one of them) for the chosen symbol's
/// ticker and reads the perp-spot basis series from the local candle
/// cache (T-085). Market-data only: a venue that never answers in the
/// wait budget takes its table slot as the honest dash row (the
/// acceptance — a missing venue is a dash, never an error); the pass
/// never routes, never sizes, never touches the trading surface.
/// <para>
/// The ticker sample rides the adapters' own lazy dial (the first
/// subscriber connects the venue's socket), bounded by
/// <see cref="TickerWait"/>; the basis reads share a bounded two-slot
/// budget (the correlation pass's discipline). Unparsable symbol texts
/// throw (the one-symbol pass has nothing to answer with — the panel
/// catches and localizes), a parsable text never does: a plain word is
/// a stock ticker and answers the honest dashes.
/// </para>
/// </summary>
public sealed class VenueSpreadService
{
    #region Constants

    /// <summary>The cache-read budget: at most this many series reads in flight (the bounded-concurrency discipline).</summary>
    internal const int ReadBudget = 2;

    /// <summary>
    /// The ticker wait: how long one venue's sample may take (a lazy
    /// dial plus the wire's first tick). A venue that stays silent past
    /// it owns a dash row — the pass never blocks the panel on a dead
    /// socket. Mutable only so the tests shrink it (the production
    /// default stands).
    /// </summary>
    internal static TimeSpan TickerWait { get; set; } = TimeSpan.FromSeconds(2.5);

    #endregion Constants

    #region Private Fields

    /// <summary>The facade: <c>Registered</c> is the fleet, <c>Current</c> the basis pass's snapshot adapter.</summary>
    private readonly ActiveExchangeService _activeExchanges;

    /// <summary>The local candle cache (T-085) every basis read rides.</summary>
    private readonly ICandleCache _cache;

    /// <summary>The read budget gate: at most <see cref="ReadBudget"/> cache reads in flight.</summary>
    private readonly SemaphoreSlim _readBudget = new(ReadBudget, ReadBudget);

    /// <summary>The sampler's own logging lane.</summary>
    private readonly ILogger<VenueSpreadService> _logger;

    #endregion Private Fields

    #region Constructor

    /// <summary>
    /// Create the sampler over the facade and the cache. Nothing arms
    /// here: the pass is on-demand, the window's sample command calls
    /// <see cref="SampleAsync"/> directly.
    /// </summary>
    public VenueSpreadService(
        ActiveExchangeService activeExchanges,
        ICandleCache cache,
        ILogger<VenueSpreadService> logger)
    {
        _activeExchanges = activeExchanges ?? throw new ArgumentNullException(nameof(activeExchanges));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #endregion Constructor

    #region Sampling

    /// <summary>
    /// Run one pass: every registered venue's ticker sampled within the
    /// wait budget (off the UI thread, the fleet's registration order),
    /// every venue's perp-spot basis series read from the cache under
    /// the read budget. The basis keys off the facade's <c>Current</c>
    /// adapter snapshot for the sibling resolution but reads each
    /// venue's OWN cache — the same-venue spot-perp pairing is the
    /// honest basis (a cross-venue pairing would fold two venues' fees
    /// and conventions into one number and dress the mixture up as
    /// carry).
    /// </summary>
    /// <param name="symbolText">The sampled symbol's text: the spot form or the settled perp form.</param>
    /// <param name="frame">The time frame the basis series rides.</param>
    /// <param name="window">The basis series' bar window (the reads fetch this many bars per category).</param>
    /// <param name="ct">The pass's cancellation token.</param>
    /// <returns>The pass's snapshot.</returns>
    /// <exception cref="ArgumentNullException">The symbol text is null or white-space.</exception>
    /// <exception cref="ArgumentException">The symbol text does not parse (the malformed shapes: two separators, a settle mark without a quote).</exception>
    public async Task<VenueSpreadSnapshot> SampleAsync(
        string symbolText,
        TimeFrame frame,
        int window,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(symbolText))
        {
            throw new ArgumentNullException(nameof(symbolText));
        }

        Symbol symbol;
        try
        {
            symbol = Symbol.Parse(symbolText);
        }
        catch (ArgumentException)
        {
            _logger.LogWarning(
                "Venue spread: the text '{SymbolText}' does not parse; the pass refuses it",
                symbolText);
            throw;
        }

        if (window < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(window), window, "The basis window must be at least 2.");
        }

        // The fleet snapshot once: a mid-pass registration change cannot
        // tear the table (the facade's own list is fixed at composition,
        // but the discipline costs nothing and reads the same).
        var adapters = _activeExchanges.Registered;
        var quotes = await Task.WhenAll(adapters
            .Select(adapter => SampleVenueAsync(adapter, symbol, ct))
            .ToArray());

        var bases = await Task.WhenAll(adapters
            .Select(adapter => SampleBasisAsync(adapter, symbol, frame, window, ct))
            .ToArray());

        // The deltas ride the ANSWERING set's median (the pure math
        // dashes the single-venue table honestly).
        var deltas = VenueSpreadMath.DeltasVsMedian([.. quotes.Select(static quote => quote.Last)]);

        return new VenueSpreadSnapshot(
            symbol.Normalized,
            [.. quotes.Zip(deltas, static (quote, delta) => quote with { LastDeltaBps = delta })],
            [.. bases]);
    }

    /// <summary>
    /// One venue's ticker sample: the first tick within the wait budget
    /// or null (the dash) — a silent socket, a faulted stream and a
    /// plain timeout all answer the same honest absence. The
    /// subscription disposes before the pass moves on.
    /// </summary>
    private async Task<VenueQuote> SampleVenueAsync(
        IMarketDataAdapter adapter,
        Symbol symbol,
        CancellationToken ct)
    {
        var ticker = await SampleFirstTickerAsync(adapter, symbol, ct);
        if (ticker is not { } answered)
        {
            return new VenueQuote(adapter.Name, null, null, null, null);
        }

        // A non-positive wire price is not a price (the refusal
        // discipline): the cell renders the dash, never a fabricated
        // level.
        return new VenueQuote(
            adapter.Name,
            answered.LastPrice > 0m ? answered.LastPrice : null,
            answered.Bid > 0m ? answered.Bid : null,
            answered.Ask > 0m ? answered.Ask : null,
            null);
    }

    private async Task<Ticker?> SampleFirstTickerAsync(
        IMarketDataAdapter adapter,
        Symbol symbol,
        CancellationToken ct)
    {
        var first = new TaskCompletionSource<Ticker>(TaskCreationOptions.RunContinuationsAsynchronously);
        IDisposable subscription = adapter.SubscribeTicker(symbol).Subscribe(
            ticker => first.TrySetResult(ticker),
            error => first.TrySetException(error));
        try
        {
            var completed = await Task.WhenAny(first.Task, Task.Delay(TickerWait, ct));
            return completed == first.Task ? await first.Task : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            subscription.Dispose();
        }
    }

    /// <summary>
    /// One venue's basis series: the spot and the perp sibling read from
    /// THAT venue's own cache under the read budget, the pure pairing
    /// over the shared bar times. A stock symbol (no quote asset) owns
    /// no perp sibling — the honest empty series. The sibling derives
    /// from the parsed symbol: the perp form strips the settle marker,
    /// the spot form appends the quote asset as the linear settle (the
    /// T-197 ccxt-style convention).
    /// </summary>
    private async Task<VenueBasis> SampleBasisAsync(
        IMarketDataAdapter adapter,
        Symbol symbol,
        TimeFrame frame,
        int window,
        CancellationToken ct)
    {
        if (symbol.QuoteAsset is null)
        {
            return new VenueBasis(adapter.Name, []);
        }

        var spot = symbol.IsPerpetual
            ? new Symbol(symbol.BaseAsset, symbol.QuoteAsset, symbol.AssetClass)
            : symbol;
        var perp = symbol.IsPerpetual
            ? symbol
            : new Symbol(symbol.BaseAsset, symbol.QuoteAsset, symbol.AssetClass, symbol.QuoteAsset);

        IReadOnlyList<Bar> spotBars;
        IReadOnlyList<Bar> perpBars;
        await _readBudget.WaitAsync(ct);
        try
        {
            spotBars = await _cache.GetLatestAsync(adapter.Id, spot, frame, window, ct);
            perpBars = await _cache.GetLatestAsync(adapter.Id, perp, frame, window, ct);
        }
        finally
        {
            _readBudget.Release();
        }

        return new VenueBasis(adapter.Name, VenueSpreadMath.BasisSeries(spotBars, perpBars));
    }

    #endregion Sampling
}
