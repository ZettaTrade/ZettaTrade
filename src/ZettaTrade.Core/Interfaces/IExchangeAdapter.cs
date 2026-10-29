namespace ZettaTrade.Core.Interfaces;

using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Base interface for all exchange adapters.
/// </summary>
public interface IExchangeAdapter : IAsyncDisposable
{
    /// <summary>Exchange identifier (e.g., "binance", "bybit").</summary>
    string Id { get; }

    /// <summary>Exchange display name.</summary>
    string Name { get; }

    /// <summary>Whether the adapter is currently connected.</summary>
    bool IsConnected { get; }

    /// <summary>Capabilities of this adapter.</summary>
    ExchangeCapabilities Capabilities { get; }

    /// <summary>Connect to the exchange.</summary>
    Task ConnectAsync(CancellationToken ct = default);

    /// <summary>Disconnect from the exchange.</summary>
    Task DisconnectAsync(CancellationToken ct = default);
}

/// <summary>
/// Interface for market data operations.
/// </summary>
public interface IMarketDataAdapter : IExchangeAdapter
{
    /// <summary>Get list of available trading pairs.</summary>
    Task<IReadOnlyList<Symbol>> GetSymbolsAsync(CancellationToken ct = default);

    /// <summary>Subscribe to ticker updates for a symbol.</summary>
    IObservable<Ticker> SubscribeTicker(Symbol symbol);

    /// <summary>Subscribe to order book updates for a symbol.</summary>
    IObservable<OrderBook> SubscribeOrderBook(Symbol symbol, int depth = 20);

    /// <summary>Subscribe to trade stream for a symbol.</summary>
    IObservable<MarketTrade> SubscribeTrades(Symbol symbol);

    /// <summary>Get historical candlestick data.</summary>
    Task<IReadOnlyList<Bar>> GetBarsAsync(
        Symbol symbol,
        TimeFrame timeFrame,
        DateTimeOffset from,
        DateTimeOffset? to = null,
        int limit = 500,
        CancellationToken ct = default);
}

/// <summary>
/// Interface for trading operations.
/// </summary>
public interface ITradingAdapter : IMarketDataAdapter
{
    /// <summary>Place a new order.</summary>
    Task<Order> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default);

    /// <summary>Cancel an existing order. Symbol is required by some exchanges (e.g., Binance).</summary>
    Task CancelOrderAsync(Symbol symbol, string orderId, CancellationToken ct = default);

    /// <summary>Get all open orders.</summary>
    Task<IReadOnlyList<Order>> GetOpenOrdersAsync(CancellationToken ct = default);

    /// <summary>Get all open positions.</summary>
    Task<IReadOnlyList<Position>> GetPositionsAsync(CancellationToken ct = default);

    /// <summary>Get account balance.</summary>
    Task<IReadOnlyDictionary<string, Money>> GetBalancesAsync(CancellationToken ct = default);
}
