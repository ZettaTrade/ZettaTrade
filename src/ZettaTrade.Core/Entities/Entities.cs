namespace ZettaTrade.Core.Entities;

using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Represents a trading order.
/// </summary>
public sealed record Order
{
    public required string Id { get; init; }
    public required string ExchangeId { get; init; }
    public required Symbol Symbol { get; init; }
    public required OrderSide Side { get; init; }
    public required OrderType Type { get; init; }
    public required Quantity Quantity { get; init; }
    public required Price Price { get; init; }
    public required OrderStatus Status { get; init; }
    public required TimeInForce TimeInForce { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public Quantity FilledQuantity { get; init; } = Quantity.Zero;
    public string? ClientOrderId { get; init; }
    public Price? StopPrice { get; init; }
    public string? ExchangeOrderId { get; init; }
    public string? RejectReason { get; init; }
}

/// <summary>
/// Represents an open position.
/// </summary>
public sealed record Position
{
    public required string Id { get; init; }
    public required string ExchangeId { get; init; }
    public required Symbol Symbol { get; init; }
    public required OrderSide Side { get; init; }
    public required Quantity Quantity { get; init; }
    public required Price EntryPrice { get; init; }
    public required Price CurrentPrice { get; init; }
    public required DateTimeOffset OpenedAt { get; init; }
    public Price? StopLoss { get; init; }
    public Price? TakeProfit { get; init; }

    public Money UnrealizedPnl(string quoteCurrency)
    {
        var pnlPerUnit = Side == OrderSide.Buy
            ? CurrentPrice.Value - EntryPrice.Value
            : EntryPrice.Value - CurrentPrice.Value;
        return new Money(pnlPerUnit * Quantity.Value, quoteCurrency);
    }
}

/// <summary>
/// Represents an executed trade.
/// </summary>
public sealed record Trade
{
    public required string Id { get; init; }
    public required string OrderId { get; init; }
    public required Symbol Symbol { get; init; }
    public required OrderSide Side { get; init; }
    public required Price Price { get; init; }
    public required Quantity Quantity { get; init; }
    public required Money Fee { get; init; }
    public required DateTimeOffset ExecutedAt { get; init; }
}

/// <summary>
/// OHLCV candlestick bar.
/// </summary>
public readonly record struct Bar(
    DateTimeOffset Time,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);

/// <summary>
/// Top-of-book price level.
/// </summary>
public readonly record struct PriceLevel(decimal Price, decimal Quantity);

/// <summary>
/// Order book snapshot.
/// </summary>
public sealed record OrderBook
{
    public required Symbol Symbol { get; init; }
    public required IReadOnlyList<PriceLevel> Bids { get; init; }
    public required IReadOnlyList<PriceLevel> Asks { get; init; }
    public required DateTimeOffset Timestamp { get; init; }

    public PriceLevel? BestBid => Bids.Count > 0 ? Bids[0] : null;
    public PriceLevel? BestAsk => Asks.Count > 0 ? Asks[0] : null;

    public decimal? MidPrice =>
        BestBid is { } bid && BestAsk is { } ask
            ? (bid.Price + ask.Price) / 2m
            : null;

    public decimal? Spread =>
        BestBid is { } bid && BestAsk is { } ask
            ? ask.Price - bid.Price
            : null;
}

/// <summary>
/// Real-time ticker update.
/// </summary>
public readonly record struct Ticker(
    Symbol Symbol,
    decimal LastPrice,
    decimal Bid,
    decimal Ask,
    decimal Volume24h,
    decimal PriceChange24hPercent,
    DateTimeOffset Timestamp);
