namespace ZettaTrade.Core.Interfaces;

using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Unified exchange adapter interface.
/// All exchange-specific implementations must implement this.
/// </summary>
public interface IExchangeAdapter : IAsyncDisposable
{
    /// <summary>Display name of the exchange (e.g., "Binance", "Bybit").</summary>
    string Name { get; }

    /// <summary>Whether the adapter is currently connected.</summary>
    bool IsConnected { get; }

    /// <summary>Connect to the exchange (REST + WebSocket).</summary>
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

    /// <summary>Subscribe to trade stream for a symbol.</summary>
    IAsyncEnumerable<Trade> SubscribeTradesAsync(
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

    /// <summary>Place a new order.</summary>
    Task<Order> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default);

    /// <summary>Cancel an existing order.</summary>
    Task CancelOrderAsync(string orderId, CancellationToken ct = default);

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
    public required Symbol Symbol { get; init; }
    public required OrderSide Side { get; init; }
    public required OrderType Type { get; init; }
    public required Quantity Quantity { get; init; }
    public Price? Price { get; init; }
    public Price? StopPrice { get; init; }
    public TimeInForce TimeInForce { get; init; } = TimeInForce.GTC;
    public string? ClientOrderId { get; init; }
    public bool ReduceOnly { get; init; }
}
