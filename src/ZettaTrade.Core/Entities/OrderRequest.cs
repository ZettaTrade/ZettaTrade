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
    /// For a TrailingStop order it seeds the initial trigger level: the first
    /// price the simulated stop arms at before the trailing distance drags it
    /// along the favorable direction. Also the value anchor the risk manager
    /// estimates the order's notional from at trigger.
    /// </summary>
    public Price? StopPrice { get; init; }

    /// <summary>
    /// Trailing distance for TrailingStop orders: how far the market is
    /// allowed to retrace from the favorable extreme before the stop
    /// triggers. The trigger itself only ever moves in the favorable
    /// direction (a sell order's trigger ratchets up, a buy order's down).
    /// Required and strictly positive for TrailingStop orders.
    /// </summary>
    public Price? TrailDistance { get; init; }

    /// <summary>
    /// OCO (one-cancels-the-other) group identifier. Orders sharing a
    /// non-empty id form an either-or group: the first leg that fills or is
    /// cancelled takes the remaining legs down with it, so no half pair ever
    /// keeps working. Null for standalone orders. An adapter that cannot
    /// hold the contract must reject the leg instead of degrading.
    /// </summary>
    public string? OcoGroupId { get; init; }

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
    /// Post-only intent (T-191): the limit order must rest on the book as a
    /// maker and never cross it at placement. If the limit price would
    /// immediately match the opposite best bid/ask, the adapter refuses the
    /// request instead of degrading it to a taker fill. Defined for Limit
    /// orders only; anything else is refused at validation.
    /// </summary>
    public bool PostOnly { get; init; }

    /// <summary>
    /// The bracket payload (T-186): the attached protective exits the entry
    /// carries to the adapter as one validated request. Null for every
    /// standalone order — only the ENTRY of a bracket carries the legs; a
    /// leg itself never does. An adapter that cannot hold the shape must
    /// reject the request instead of degrading.
    /// </summary>
    public BracketLegs? BracketLegs { get; init; }

    /// <summary>
    /// Validate the order request. A bracket entry validates WITH its
    /// attached exits: one validated request, never a half protection.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if validation fails.</exception>
    public void Validate()
    {
        // Enum definedness: a record programmatically built with an out-of-set
        // value (e.g. a numeric ride-through) must fail loudly here — never
        // flow on as valid toward execution (BUG-007). The scope is Buy/Sell
        // (T-198): the tape-only neutral side (OrderSide.None) states an
        // unserved aggressor fact on a MARKET TRADE; on an ORDER it would
        // silently take the sell branch of every side-aware execution path —
        // so it is refused here like any other unparsable side.
        if (Side is not (OrderSide.Buy or OrderSide.Sell))
            throw new InvalidOperationException($"Order side must be Buy or Sell; {Side} is refused.");

        if (!Enum.IsDefined(Type))
            throw new InvalidOperationException($"Undefined order type: {Type}.");

        if (!Enum.IsDefined(TimeInForce))
            throw new InvalidOperationException($"Undefined time-in-force: {TimeInForce}.");

        if (TimeInForce == TimeInForce.GTD && Expiry == null)
            throw new InvalidOperationException("Expiry is required for GTD orders.");

        if (Expiry != null && Expiry <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Expiry must be in the future.");

        // Post-only (T-191): the maker-only contract is defined for limit
        // orders only. A non-limit request that claims post-only is
        // unparsable, never silently degraded to a plain order.
        if (PostOnly && Type != OrderType.Limit)
            throw new InvalidOperationException(
                $"PostOnly is only supported for Limit orders; {Type} is refused.");

        if ((Type == OrderType.Limit || Type == OrderType.StopLimit) && Price == null)
            throw new InvalidOperationException($"Price is required for {Type} orders.");

        if ((Type == OrderType.StopMarket || Type == OrderType.StopLimit) && StopPrice == null)
            throw new InvalidOperationException($"StopPrice is required for {Type} orders.");

        if (Quantity.Value <= 0)
            throw new InvalidOperationException("Quantity must be positive.");

        // Trailing-stop shape: the request always carries its trailing distance
        // and the seed trigger the order arms at. A trailing request without
        // either is unparsable, never defaulted toward execution.
        if (Type == OrderType.TrailingStop)
        {
            if (TrailDistance == null)
                throw new InvalidOperationException("TrailDistance is required for TrailingStop orders.");

            if (TrailDistance.Value.Value <= 0)
                throw new InvalidOperationException(
                    $"TrailDistance must be positive for TrailingStop orders ({TrailDistance.Value.Value}).");

            if (StopPrice == null)
                throw new InvalidOperationException(
                    "StopPrice (the initial trigger seed) is required for TrailingStop orders.");
        }

        // Stop-limit trigger-vs-limit matrix: the armed trigger sits inside the
        // limit protection. A Buy stop-limit limits its pursuit from above
        // (the limit price is at or above the trigger), a Sell stop-limit
        // below it. An inverted pair is a validation error, never a silent
        // reinterpretation.
        if (Type == OrderType.StopLimit && Price is { } limitPrice && StopPrice is { } triggerPrice)
        {
            var wrongSide = Side == OrderSide.Buy
                ? triggerPrice.Value > limitPrice.Value
                : triggerPrice.Value < limitPrice.Value;

            if (wrongSide)
            {
                throw new InvalidOperationException(
                    $"For a {Side} stop-limit the limit price must be " +
                    (Side == OrderSide.Buy
                        ? $"at or above the stop trigger (limit {limitPrice.Value}, trigger {triggerPrice.Value})."
                        : $"at or below the stop trigger (limit {limitPrice.Value}, trigger {triggerPrice.Value})."));
            }
        }

        if (BracketLegs is { } bracket)
        {
            ValidateBracket(bracket);
        }
    }

    /// <summary>
    /// The bracket shape matrix (T-186): the entry is a market or limit
    /// order that belongs to no OCO group itself, the exits are a limit
    /// (the take-profit) and a stop-limit (the stop-loss) on the OPPOSITE
    /// side with the SAME quantity, sharing ONE non-empty either-or group
    /// id. The exits validate as whole requests too, so an unparsable leg
    /// is refused with its own reason; a bracket inside a bracket is
    /// unparsable by shape.
    /// </summary>
    private void ValidateBracket(BracketLegs bracket)
    {
        if (Type is not (OrderType.Market or OrderType.Limit))
        {
            throw new InvalidOperationException(
                $"A bracket entry must be a market or limit order; {Type} cannot carry attached exits.");
        }

        if (OcoGroupId is not null)
        {
            throw new InvalidOperationException(
                "A bracket entry cannot itself join an OCO group; its exits form the pair.");
        }

        ValidateBracketLeg(bracket.TakeProfit, "take-profit", OrderType.Limit);
        ValidateBracketLeg(bracket.StopLoss, "stop-loss", OrderType.StopLimit);

        var exitSide = Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
        if (bracket.TakeProfit.Side != exitSide || bracket.StopLoss.Side != exitSide)
        {
            throw new InvalidOperationException(
                $"The bracket exits must be {exitSide} orders (they close a {Side} entry); " +
                "same-side exits would extend the position instead of protecting it.");
        }

        var quantity = Quantity.Value;
        if (bracket.TakeProfit.Quantity.Value != quantity || bracket.StopLoss.Quantity.Value != quantity)
        {
            throw new InvalidOperationException(
                "The bracket exits must carry the entry's quantity so the position closes whole.");
        }

        var groupId = bracket.TakeProfit.OcoGroupId;
        if (string.IsNullOrWhiteSpace(groupId) || bracket.StopLoss.OcoGroupId != groupId)
        {
            throw new InvalidOperationException(
                "The bracket exits must share one non-empty OCO group id: " +
                "the first exit that ends takes the other down.");
        }
    }

    /// <summary>One bracket exit's shape: the expected type, no nested
    /// bracket, and the leg's own full request validation.</summary>
    private void ValidateBracketLeg(OrderRequest leg, string legName, OrderType expectedType)
    {
        ArgumentNullException.ThrowIfNull(leg);

        if (leg.BracketLegs is not null)
        {
            throw new InvalidOperationException(
                "A bracket leg cannot itself carry a bracket; the exits are plain orders.");
        }

        if (leg.Type != expectedType)
        {
            throw new InvalidOperationException(
                $"The bracket's {legName} leg must be a {expectedType} order; {leg.Type} is refused.");
        }

        leg.Validate();
    }
}
