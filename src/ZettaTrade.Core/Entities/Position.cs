namespace ZettaTrade.Core.Entities;

using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;

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
