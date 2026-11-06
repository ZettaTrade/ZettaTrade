namespace ZettaTrade.Infrastructure.PnL;

using ZettaTrade.Core.Enums;

/// <summary>
/// The pure average-price arithmetic of the PnL engine: how one fill moves
/// a position book and what it realizes. No persistence, no observables, no
/// time — the exact numbers come out of these functions, deterministically,
/// from (state, fill) pairs, which is what makes the engine reconstructible
/// from its persisted realized history after a restart.
/// </summary>
/// <remarks>
/// Realized PnL per closing fill is the average-price method: a fill that
/// reduces a position realizes <c>(price − averageEntry) × closedQty</c>,
/// signed by the held side. A closing trade larger than the position
/// outright closes it and re-opens the remainder (<see cref="PnlFillEffect.Flip"/>)
/// at the same fill price. Commission is NOT part of these functions: it is
/// a realized cost the engine applies outside the position book (a
/// flip's commission charges the closed part's realized line; the
/// re-opened remainder books at the raw fill price).
/// </remarks>
internal static class PnlPositionMath
{
    #region Public Methods

    /// <summary>
    /// Apply one fill to a position book.
    /// </summary>
    /// <param name="state">The pre-fill book state (default = no position).</param>
    /// <param name="side">The fill side (Buy opens/extends a long, Sell a short).</param>
    /// <param name="quantity">The executed quantity; strictly positive.</param>
    /// <param name="price">The execution price; strictly positive. A
    /// zero/negative price is rejected rather than defaulted (trading
    /// safety: never substitute defaults for invalid inputs).</param>
    /// <returns>The post-fill book state, the fill's effect, the closed
    /// quantity (zero for pure opens) and the gross realized PnL of the
    /// closed part (zero for pure opens) — both in the symbol's quote
    /// currency.</returns>
    public static (PnlPositionState State, PnlFillEffect Effect, decimal ClosedQuantity, decimal GrossRealized)
        ApplyFill(
            PnlPositionState state,
            OrderSide side,
            decimal quantity,
            decimal price)
    {
        if (side is not (OrderSide.Buy or OrderSide.Sell))
        {
            throw new ArgumentOutOfRangeException(nameof(side), side, "The fill side must be Buy or Sell.");
        }

        if (quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "The fill quantity must be positive.");
        }

        if (price <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(price), price, "The fill price must be positive.");
        }

        var signedDelta = side == OrderSide.Buy ? quantity : -quantity;
        var position = state.SignedQuantity;

        // Opening or extending in the same direction: the average entry
        // price blends quantity-weighted with the fill price.
        if (position == 0m || (position > 0m && signedDelta > 0m) || (position < 0m && signedDelta < 0m))
        {
            var newAbsolute = Math.Abs(position) + quantity;
            var newAverage = (Math.Abs(position) * state.AverageEntryPrice + quantity * price) / newAbsolute;
            var newSigned = position > 0m || (position == 0m && signedDelta > 0m) ? newAbsolute : -newAbsolute;

            return (new PnlPositionState(newSigned, newAverage, state.CumulativeRealized), PnlFillEffect.Open, 0m, 0m);
        }

        // Closing into (or beyond) the position: what closes realizes by the
        // average-price method; what remains (a flip) re-opens on the other
        // side at the fill price.
        var heldSign = position > 0m ? 1 : -1;
        var held = position * heldSign;
        var closed = Math.Min(quantity, held);
        var gross = (price - state.AverageEntryPrice) * closed * heldSign;
        var remainder = quantity - closed;
        var newPosition = position + signedDelta;
        var effect = remainder > 0m ? PnlFillEffect.Flip : PnlFillEffect.Reduce;
        var average = newPosition == 0m ? 0m : effect == PnlFillEffect.Flip ? price : state.AverageEntryPrice;

        return (new PnlPositionState(newPosition, average, state.CumulativeRealized), effect, closed, gross);
    }

    #endregion Public Methods
}
