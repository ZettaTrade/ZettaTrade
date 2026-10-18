namespace ZettaTrade.Core.Interfaces;

using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Market data adapter interface.
/// Provides read-only access to market data (tickers, order books, historical data).
/// Does not include trading operations.
/// </summary>
public interface IMarketDataAdapter : IAsyncDisposable
{
    /// <summary>Unique identifier for this adapter instance.</summary>
    string Id { get; }
    
    /// <summary>Display name of the exchange (e.g., "Binance", "Bybit").</summary>
    string Name { get; }

    /// <summary>Whether the adapter is currently connected.</summary>
    bool IsConnected { get; }

    /// <summary>Capabilities of this adapter.</summary>
    ExchangeCapabilities Capabilities { get; }

    /// <summary>Connect to the exchange.</summary>
    Task ConnectAsync(CancellationToken ct = default);

    /// <summary>Disconnect from the exchange.</summary>
    Task DisconnectAsync(CancellationToken ct = default);

    /// <summary>Subscribe to real-time ticker updates for multiple symbols.</summary>
    IAsyncEnumerable<Ticker> SubscribeTickersAsync(
        IEnumerable<Symbol> symbols,
        CancellationToken ct = default);

    /// <summary>Subscribe to order book updates for a symbol.</summary>
    IAsyncEnumerable<OrderBook> SubscribeOrderBookAsync(
        Symbol symbol,
        int depth = 20,
        CancellationToken ct = default);

    /// <summary>Subscribe to public trade stream for a symbol (market trades, not user trades).</summary>
    IAsyncEnumerable<MarketTrade> SubscribeMarketTradesAsync(
        Symbol symbol,
        CancellationToken ct = default);

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
/// Trading adapter interface.
/// Provides trading operations (orders, positions, balances).
/// Requires authentication and extends IMarketDataAdapter.
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

/// <summary>
/// Request to place an order.
/// </summary>
public sealed record OrderRequest
{
    /// <summary>Trading symbol.</summary>
    public required Symbol Symbol { get; init; }
    
    /// <summary>Order side (Buy or Sell).</summary>
    public required OrderSide Side { get; init; }
    
    /// <summary>Order type (Market, Limit, etc.).</summary>
    public required OrderType Type { get; init; }
    
    /// <summary>Order quantity.</summary>
    public required Quantity Quantity { get; init; }
    
    /// <summary>
    /// Limit price. Required for Limit and StopLimit orders.
    /// Null for Market orders.
    /// </summary>
    public Price? Price { get; init; }
    
    /// <summary>
    /// Stop trigger price. Required for StopMarket and StopLimit orders.
    /// </summary>
    public Price? StopPrice { get; init; }
    
    /// <summary>Time-in-force policy.</summary>
    public TimeInForce TimeInForce { get; init; } = TimeInForce.GTC;
    
    /// <summary>
    /// Expiry time for GTD (Good-Till-Date) orders.
    /// Required when TimeInForce is GTD.
    /// </summary>
    public DateTimeOffset? Expiry { get; init; }
    
    /// <summary>Client-side order identifier.</summary>
    public string? ClientOrderId { get; init; }
    
    /// <summary>Whether this is a reduce-only order.</summary>
    public bool ReduceOnly { get; init; }
    
    /// <summary>
    /// Validate the order request.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if validation fails.</exception>
    public void Validate()
    {
        if (TimeInForce == TimeInForce.GTD && Expiry == null)
            throw new InvalidOperationException("Expiry is required for GTD orders.");
        
        if (Expiry != null && Expiry <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Expiry must be in the future.");
        
        if ((Type == OrderType.Limit || Type == OrderType.StopLimit) && Price == null)
            throw new InvalidOperationException($"Price is required for {Type} orders.");
        
        if ((Type == OrderType.StopMarket || Type == OrderType.StopLimit) && StopPrice == null)
            throw new InvalidOperationException($"StopPrice is required for {Type} orders.");
        
        if (Quantity.Value <= 0)
            throw new InvalidOperationException("Quantity must be positive.");
    }
}
