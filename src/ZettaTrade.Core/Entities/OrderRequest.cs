namespace ZettaTrade.Core.Entities;

using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;

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
        // Enum definedness: a record programmatically built with an out-of-set
        // value (e.g. a numeric ride-through) must fail loudly here — never
        // flow on as valid toward execution (BUG-007).
        if (!Enum.IsDefined(Side))
            throw new InvalidOperationException($"Undefined order side: {Side}.");

        if (!Enum.IsDefined(Type))
            throw new InvalidOperationException($"Undefined order type: {Type}.");

        if (!Enum.IsDefined(TimeInForce))
            throw new InvalidOperationException($"Undefined time-in-force: {TimeInForce}.");

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
