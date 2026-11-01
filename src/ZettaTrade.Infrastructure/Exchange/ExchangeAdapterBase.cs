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
        return SubscribeWithStreamStart(symbol, StartSymbolStreamsAsync, subject.AsObservable());
    }

    /// <inheritdoc/>
    public virtual IObservable<OrderBook> SubscribeOrderBook(Symbol symbol, int depth = 20)
    {
        var subject = OrderBookSubjects.GetOrAdd(symbol.Normalized, _ => new Subject<OrderBook>());
        return SubscribeWithStreamStart(symbol, StartSymbolStreamsAsync, subject.AsObservable());
    }

    /// <inheritdoc/>
    public virtual IObservable<MarketTrade> SubscribeTrades(Symbol symbol)
    {
        var subject = TradeSubjects.GetOrAdd(symbol.Normalized, _ => new Subject<MarketTrade>());
        return SubscribeWithStreamStart(symbol, StartSymbolStreamsAsync, subject.AsObservable());
    }

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
    /// sessions) must run on adapter-owned lifetimes, not on this token.
    /// </summary>
    protected virtual Task StartSymbolStreamsAsync(
        Symbol symbol,
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
