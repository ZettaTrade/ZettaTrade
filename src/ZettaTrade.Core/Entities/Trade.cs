namespace ZettaTrade.Core.Entities;

using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Represents an executed trade (user's own trade).
/// </summary>
public sealed record Trade
{
    /// <summary>Unique trade identifier.</summary>
    public required string Id { get; init; }
    
    /// <summary>Associated order identifier.</summary>
    public required string OrderId { get; init; }
    
    /// <summary>Trading symbol.</summary>
    public required Symbol Symbol { get; init; }
    
    /// <summary>Trade side.</summary>
    public required OrderSide Side { get; init; }
    
    /// <summary>Execution price.</summary>
    public required Price Price { get; init; }
    
    /// <summary>Trade quantity.</summary>
    public required Quantity Quantity { get; init; }
    
    /// <summary>Trading fee.</summary>
    public required Money Fee { get; init; }
    
    /// <summary>Execution timestamp.</summary>
    public required DateTimeOffset ExecutedAt { get; init; }
}
