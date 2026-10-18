namespace ZettaTrade.Infrastructure.Exchange;

using System.Collections.Concurrent;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;

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
    public IObservable<Ticker> SubscribeTicker(Symbol symbol)
    {
        var key = symbol.Normalized;
        var subject = TickerSubjects.GetOrAdd(key, _ => new Subject<Ticker>());
        return subject.AsObservable();
    }

    /// <inheritdoc/>
    public IObservable<OrderBook> SubscribeOrderBook(Symbol symbol, int depth = 20)
    {
        var key = symbol.Normalized;
        var subject = OrderBookSubjects.GetOrAdd(key, _ => new Subject<OrderBook>());
        return subject.AsObservable();
    }

    /// <inheritdoc/>
    public IObservable<MarketTrade> SubscribeTrades(Symbol symbol)
    {
        var key = symbol.Normalized;
        var subject = TradeSubjects.GetOrAdd(key, _ => new Subject<MarketTrade>());
        return subject.AsObservable();
    }

    #endregion

    #region Protected Methods

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

    #region IDisposable

    public virtual async ValueTask DisposeAsync()
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

        await Task.CompletedTask;
    }

    #endregion
}
