namespace ZettaTrade.Infrastructure.PnL;

/// <summary>
/// The mutable bookkeeping state of one position account, keyed by
/// (exchange, symbol). The sign of <see cref="SignedQuantity"/> holds the
/// side (positive = long, negative = short, zero = flat);
/// <see cref="AverageEntryPrice"/> is the quantity-weighted average of the
/// opening fills; <see cref="CumulativeRealized"/> is the running realized
/// PnL net of every accounted commission, expressed in the symbol's quote
/// currency.
/// </summary>
internal readonly record struct PnlPositionState(
    decimal SignedQuantity,
    decimal AverageEntryPrice,
    decimal CumulativeRealized);
