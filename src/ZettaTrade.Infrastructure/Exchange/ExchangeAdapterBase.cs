namespace ZettaTrade.Infrastructure.Exchange;

using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Base class for exchange adapters with common functionality.
/// </summary>
public abstract class ExchangeAdapterBase : IMarketDataAdapter
{
    #region Properties

    /// <inheritdoc/>
    public abstract string Id { get; }

    /// <inheritdoc/>
    public abstract string Name { get; }

    /// <inheritdoc/>
    public bool IsConnected { get; protected set; }

    /// <inheritdoc/>
    public abstract ExchangeCapabilities Capabilities { get; }

    /// <summary>Logger instance.</summary>
    protected readonly ILogger Logger;

    /// <summary>Subject for ticker updates.</summary>
    protected readonly ConcurrentDictionary<string, Subject<Ticker>> TickerSubjects = new();

    /// <summary>Subject for order book updates.</summary>
    protected readonly ConcurrentDictionary<string, Subject<OrderBook>> OrderBookSubjects = new();

    /// <summary>Subject for trade updates.</summary>
    protected readonly ConcurrentDictionary<string, Subject<MarketTrade>> TradeSubjects = new();

    /// <summary>
    /// The order book depths this adapter serves — the public face of the
    /// wire-level <see cref="SupportedBookDepths"/> declaration, so a caller
    /// can resolve its request against the real constraint with
    /// <see cref="ResolveBookDepth"/> instead of receiving the rejection.
    /// </summary>
    public IReadOnlySet<int> ServedBookDepths => SupportedBookDepths;

    #endregion

    #region Constructor

    /// <summary>
    /// Create a new ExchangeAdapterBase instance.
    /// </summary>
    protected ExchangeAdapterBase(ILogger logger)
    {
        Logger = logger;
    }

    #endregion

    #region Public Methods

    /// <inheritdoc/>
    public abstract Task ConnectAsync(CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task DisconnectAsync(CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task<IReadOnlyList<Symbol>> GetSymbolsAsync(CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task<IReadOnlyList<Bar>> GetBarsAsync(
        Symbol symbol,
        TimeFrame timeFrame,
        DateTimeOffset from,
        DateTimeOffset? to = null,
        int limit = 500,
        CancellationToken ct = default);

    /// <inheritdoc/>
    public virtual IObservable<Ticker> SubscribeTicker(Symbol symbol)
    {
        var subject = TickerSubjects.GetOrAdd(symbol.Normalized, _ => new Subject<Ticker>());
        return SubscribeWithStreamStart(symbol, (sym, ct) => StartSymbolStreamsAsync(sym, null, ct), subject.AsObservable());
    }

    /// <inheritdoc/>
    public virtual IObservable<OrderBook> SubscribeOrderBook(Symbol symbol, int depth = 20)
    {
        if (!SupportedBookDepths.Contains(depth))
        {
            // The depth contract is explicit: an out-of-set request is an
            // error, never a silently served different level count (BUG-032).
            // The error rides the subscription instead of a synchronous throw
            // so a blind caller's switch (which guards its stream failures)
            // survives the rejection.
            return Observable.Throw<OrderBook>(
                new ArgumentOutOfRangeException(
                    nameof(depth),
                    depth,
                    $"The {Id} adapter serves only order book depths {SupportedBookDepthsText} " +
                    $"(requested {depth}); the request is rejected, not defaulted."));
        }

        var subject = OrderBookSubjects.GetOrAdd(symbol.Normalized, _ => new Subject<OrderBook>());
        return SubscribeWithStreamStart(symbol, (sym, ct) => StartSymbolStreamsAsync(sym, depth, ct), subject.AsObservable());
    }

    /// <inheritdoc/>
    public virtual IObservable<MarketTrade> SubscribeTrades(Symbol symbol)
    {
        var subject = TradeSubjects.GetOrAdd(symbol.Normalized, _ => new Subject<MarketTrade>());
        return SubscribeWithStreamStart(symbol, (sym, ct) => StartSymbolStreamsAsync(sym, null, ct), subject.AsObservable());
    }

    /// <summary>
    /// Resolve a requested order book depth against the depths an adapter
    /// serves: the smallest served depth at least as deep as the request
    /// wins, and — when nothing is deep enough — the deepest served depth.
    /// No declaration (null/empty, serve-anything adapters) passes the
    /// request through for the adapter to own: it is served or rejected by
    /// <see cref="SubscribeOrderBook"/>'s explicit contract (BUG-032),
    /// never silently reshaped there.
    /// </summary>
    public static int ResolveBookDepth(IReadOnlySet<int>? servedDepths, int requestedDepth)
    {
        if (servedDepths is not { Count: > 0 })
        {
            return requestedDepth;
        }

        return servedDepths
            .Where(depth => depth >= requestedDepth)
            .DefaultIfEmpty(servedDepths.Max())
            .Min();
    }

    #endregion

    #region Protected Properties

    /// <summary>
    /// The order book depths this adapter honors on the wire (Binance serves
    /// <c>@depth5/@depth10/@depth20</c>, Bybit serves <c>orderbook.1</c> and
    /// <c>orderbook.50</c>). <see cref="SubscribeOrderBook"/> rejects any other
    /// depth with a clear error instead of silently serving a different level
    /// count than the one requested (BUG-032).
    /// </summary>
    protected abstract IReadOnlySet<int> SupportedBookDepths { get; }

    #endregion

    #region Protected Methods

    /// <summary>
    /// Start the adapter-specific exchange streams for one symbol. Invoked by
    /// the first subscriber of each feed (SubscribeTicker, SubscribeOrderBook,
    /// SubscribeTrades) so data actually flows, and re-run by every new outer
    /// subscription (stream subscription is idempotent on exchanges). The
    /// in-flight start is cancelled together with the subscription. The
    /// default is a no-op for adapters that push into the subjects directly.
    /// The cancellation token covers the start only: it is reclaimed once the
    /// start completes, so resources pooled longer than one start (WebSocket
    /// sessions) must run on adapter-owned lifetimes, not on this token. In
    /// particular the START'S SENDS must never ride this token when they go
    /// through a transport shared by every feed: cancelling a mid-flight send
    /// aborts the shared socket for all of them.
    /// </summary>
    /// <param name="symbol">The symbol whose streams start.</param>
    /// <param name="bookDepth">
    /// The explicitly requested order book depth of a book-triggered start
    /// (validated against <see cref="SupportedBookDepths"/> before the call),
    /// or null when the start was triggered by the ticker/trade feeds — the
    /// adapter then serves its own pinned depth.
    /// </param>
    /// <param name="ct">The start's lifetime token (see the summary).</param>
    protected virtual Task StartSymbolStreamsAsync(
        Symbol symbol,
        int? bookDepth,
        CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Publish a ticker update to all subscribers.
    /// </summary>
    protected void PublishTicker(Ticker ticker)
    {
        var key = ticker.Symbol.Normalized;
        if (TickerSubjects.TryGetValue(key, out var subject))
        {
            subject.OnNext(ticker);
        }
    }

    /// <summary>
    /// Publish an order book update to all subscribers.
    /// </summary>
    protected void PublishOrderBook(OrderBook orderBook)
    {
        var key = orderBook.Symbol.Normalized;
        if (OrderBookSubjects.TryGetValue(key, out var subject))
        {
            subject.OnNext(orderBook);
        }
    }

    /// <summary>
    /// Publish a trade update to all subscribers.
    /// </summary>
    protected void PublishTrade(MarketTrade trade)
    {
        var key = trade.Symbol.Normalized;
        if (TradeSubjects.TryGetValue(key, out var subject))
        {
            subject.OnNext(trade);
        }
    }

    #endregion

    #region Private Methods

    /// <summary>Sorted, comma-separated view of the supported depths for error messages.</summary>
    private string SupportedBookDepthsText => string.Join(", ", SupportedBookDepths.OrderBy(depth => depth));

    /// <summary>
    /// Wrap a shared per-symbol feed subject so the FIRST subscription starts
    /// the adapter-side exchange streams (never silently dead data), the
    /// in-flight start is cancelled together with the subscription, and every
    /// re-subscription re-runs the idempotent start. The subject itself stays
    /// shared, so repeated subscribers receive the same published data.
    /// </summary>
    private static IObservable<T> SubscribeWithStreamStart<T>(
        Symbol symbol,
        Func<Symbol, CancellationToken, Task> startStreams,
        IObservable<T> feed)
    {
        return Observable
            .FromAsync(token => startStreams(symbol, token))
            .Select(_ => feed)
            .Switch();
    }

    #endregion

    #region IDisposable

    public virtual ValueTask DisposeAsync()
    {
        foreach (var subject in TickerSubjects.Values)
        {
            subject.Dispose();
        }
        TickerSubjects.Clear();

        foreach (var subject in OrderBookSubjects.Values)
        {
            subject.Dispose();
        }
        OrderBookSubjects.Clear();

        foreach (var subject in TradeSubjects.Values)
        {
            subject.Dispose();
        }
        TradeSubjects.Clear();

        return ValueTask.CompletedTask;
    }

    #endregion
}
