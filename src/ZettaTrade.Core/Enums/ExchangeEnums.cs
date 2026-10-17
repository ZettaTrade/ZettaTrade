namespace ZettaTrade.Core.Enums;

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
/// Capabilities of an exchange adapter.
/// Allows partial implementation (e.g., data-only without trading).
/// </summary>
[Flags]
public enum ExchangeCapabilities
{
    None = 0,
    MarketData = 1 << 0,
    Trading = 1 << 1,
    OrderBook = 1 << 2,
    HistoricalData = 1 << 3,
    AccountInfo = 1 << 4,
    All = MarketData | Trading | OrderBook | HistoricalData | AccountInfo,
}
