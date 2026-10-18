namespace ZettaTrade.Core.Enums;

/// <summary>
/// Supported asset classes.
/// </summary>
public enum AssetClass { Crypto = 0, Stock = 1, Forex = 2, Commodity = 3 }

/// <summary>
/// Capabilities of an exchange adapter.
/// Allows partial implementation (e.g., data-only without trading).
/// </summary>
[Flags]
public enum ExchangeCapabilities
{
    /// <summary>No capabilities.</summary>
    None = 0,
    /// <summary>Market data streaming.</summary>
    MarketData = 1 << 0,
    /// <summary>Order placement and management.</summary>
    Trading = 1 << 1,
    /// <summary>Order book data.</summary>
    OrderBook = 1 << 2,
    /// <summary>Historical data retrieval.</summary>
    HistoricalData = 1 << 3,
    /// <summary>Account information.</summary>
    AccountInfo = 1 << 4,
    /// <summary>All capabilities.</summary>
    All = MarketData | Trading | OrderBook | HistoricalData | AccountInfo,
}
