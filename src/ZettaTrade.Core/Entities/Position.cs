namespace ZettaTrade.Core.Entities;

using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Represents an open trading position on an exchange.
/// </summary>
public sealed record Position
{
    /// <summary>Unique identifier for this position.</summary>
    public required string Id { get; init; }
    
    /// <summary>Exchange identifier.</summary>
    public required string ExchangeId { get; init; }
    
    /// <summary>Trading symbol.</summary>
    public required Symbol Symbol { get; init; }
    
    /// <summary>Position side (Buy for long, Sell for short).</summary>
    public required OrderSide Side { get; init; }
    
    /// <summary>Position quantity.</summary>
    public required Quantity Quantity { get; init; }
    
    /// <summary>Average entry price.</summary>
    public required Price EntryPrice { get; init; }
    
    /// <summary>Current market price.</summary>
    public required Price CurrentPrice { get; init; }
    
    /// <summary>Position opening timestamp.</summary>
    public required DateTimeOffset OpenedAt { get; init; }
    
    /// <summary>Stop-loss price (if set).</summary>
    public Price? StopLoss { get; init; }
    
    /// <summary>Take-profit price (if set).</summary>
    public Price? TakeProfit { get; init; }

    /// <summary>
    /// Calculate unrealized profit/loss.
    /// </summary>
    /// <param name="quoteCurrency">Quote currency for PnL calculation.</param>
    /// <returns>Unrealized PnL as Money value.</returns>
    public Money UnrealizedPnl(string quoteCurrency)
    {
        var pnlPerUnit = Side == OrderSide.Buy
            ? CurrentPrice.Value - EntryPrice.Value
            : EntryPrice.Value - CurrentPrice.Value;
        return new Money(pnlPerUnit * Quantity.Value, quoteCurrency);
    }
}
