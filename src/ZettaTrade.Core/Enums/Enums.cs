namespace ZettaTrade.Core.Enums;

/// <summary>
/// Side of an order or trade.
/// </summary>
public enum OrderSide
{
    Buy = 0,
    Sell = 1,
}

/// <summary>
/// Type of order execution.
/// </summary>
public enum OrderType
{
    Market = 0,
    Limit = 1,
    StopMarket = 2,
    StopLimit = 3,
    TrailingStop = 4,
    OCO = 5,
}

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
public enum OrderStatus
{
    Pending = 0,
    Submitted = 1,
    PartiallyFilled = 2,
    Filled = 3,
    Cancelled = 4,
    Rejected = 5,
    Expired = 6,
}

/// <summary>
/// Supported asset classes.
/// </summary>
public enum AssetClass
{
    Crypto = 0,
    Stock = 1,
    Forex = 2,
    Commodity = 3,
}

/// <summary>
/// Candlestick timeframe.
/// </summary>
public enum TimeFrame
{
    M1 = 0,
    M5 = 1,
    M15 = 2,
    M30 = 3,
    H1 = 4,
    H4 = 5,
    D1 = 6,
    W1 = 7,
    MN1 = 8,
}
