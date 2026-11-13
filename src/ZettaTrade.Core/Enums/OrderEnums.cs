namespace ZettaTrade.Core.Enums;

/// <summary>
/// Side of an order or trade.
/// </summary>
public enum OrderSide
{
    /// <summary>Buy side.</summary>
    Buy = 0,
    /// <summary>Sell side.</summary>
    Sell = 1,

    /// <summary>
    /// The tape-only neutral side (T-198): the wire served NO aggressor side —
    /// a stock feed's trade event is a matched buy and a matched sell, and
    /// venues such as Alpaca's v2 IEX feed publish exactly that fact. A MARKET
    /// TRADE may carry it (the tape states price, size and time only); an
    /// ORDER never may — <see cref="Entities.OrderRequest"/> validates the
    /// side scope to Buy/Sell before any order flows.
    /// </summary>
    None = 2,
}

/// <summary>
/// Type of order execution.
/// Note: OCO is not a type — it's a pair of orders (one cancels the other).
/// </summary>
public enum OrderType { Market = 0, Limit = 1, StopMarket = 2, StopLimit = 3, TrailingStop = 4 }

/// <summary>
/// Time-in-force policy for orders.
/// </summary>
public enum TimeInForce
{
    /// <summary>Good-Till-Cancelled.</summary>
    GTC = 0,
    /// <summary>Immediate-Or-Cancel.</summary>
    IOC = 1,
    /// <summary>Fill-Or-Kill.</summary>
    FOK = 2,
    /// <summary>Good-Till-Date (requires Expiry).</summary>
    GTD = 3,
}

/// <summary>
/// Current lifecycle state of an order.
/// </summary>
public enum OrderStatus { Pending = 0, Submitted = 1, PartiallyFilled = 2, Filled = 3, Cancelled = 4, Rejected = 5, Expired = 6 }
