namespace ZettaTrade.Core.ValueObjects;

using System.Globalization;

/// <summary>
/// Represents a trading symbol with normalized and exchange-specific formats.
/// Each exchange has its own symbol format (e.g., BTCUSDT vs BTC-USD).
/// </summary>
public sealed record Symbol
{
    private const int MinAssetLength = 2;
    private const int MaxAssetLength = 10;

    #region Exchange Identifiers

    private const string BinanceId = "binance";
    private const string BybitId = "bybit";
    private const string OkxId = "okx";
    private const string AlpacaId = "alpaca";
    private const string InteractiveBrokersId = "ib";
    private const string KrakenId = "kraken";

    /// <summary>Normalized pair separator (e.g., "BTC/USDT").</summary>
    private const char NormalizedSeparator = '/';

    /// <summary>
    /// Common quote currencies used to split concatenated exchange pairs ("BTCUSDT").
    /// Order matters: tokens sharing prefixes (BUSD/USD) are matched suffix-first,
    /// so longer tokens must stay before their shorter prefixes.
    /// </summary>
    private static readonly string[] KnownCryptoQuoteAssets =
    [
        "USDT", "USDC", "BUSD", "BTC", "ETH", "USD", "EUR",
    ];

    #endregion Exchange Identifiers

    /// <summary>Normalized symbol (e.g., "BTC/USDT" for display).</summary>
    public string Normalized { get; }

    /// <summary>Base asset (e.g., "BTC").</summary>
    public string BaseAsset { get; }

    /// <summary>Quote asset (e.g., "USDT"). Null for stocks.</summary>
    public string? QuoteAsset { get; }

    /// <summary>Asset class (Crypto, Stock, etc.).</summary>
    public Enums.AssetClass AssetClass { get; }

    /// <summary>
    /// Create a new Symbol instance.
    /// </summary>
    public Symbol(string baseAsset, string? quoteAsset, Enums.AssetClass assetClass)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseAsset);

        BaseAsset = baseAsset.ToUpperInvariant();
        QuoteAsset = quoteAsset?.ToUpperInvariant();
        AssetClass = assetClass;
        Normalized = quoteAsset != null ? $"{baseAsset.ToUpperInvariant()}/{quoteAsset.ToUpperInvariant()}" : baseAsset.ToUpperInvariant();
    }

    /// <summary>
    /// Get exchange-specific symbol format.
    /// </summary>
    public string GetExchangeFormat(string exchangeId)
    {
        return exchangeId.ToLowerInvariant() switch
        {
            BinanceId or BybitId or OkxId => QuoteAsset != null ? $"{BaseAsset}{QuoteAsset}" : BaseAsset,
            AlpacaId or InteractiveBrokersId => BaseAsset,
            KrakenId => QuoteAsset != null ? $"{BaseAsset}{NormalizedSeparator}{QuoteAsset}" : BaseAsset,
            _ => Normalized,
        };
    }

    /// <summary>
    /// Parse a normalized symbol string (e.g., "BTC/USDT").
    /// </summary>
    public static Symbol Parse(string normalized)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalized);

        var parts = normalized.Split(NormalizedSeparator);
        if (parts.Length == 2)
        {
            return new Symbol(parts[0], parts[1], Enums.AssetClass.Crypto);
        }

        return new Symbol(parts[0], null, Enums.AssetClass.Stock);
    }

    /// <summary>
    /// Try to parse an exchange-specific symbol format.
    /// </summary>
    public static bool TryParseExchangeFormat(string exchangeSymbol, string exchangeId, out Symbol? symbol)
    {
        symbol = null;

        if (string.IsNullOrWhiteSpace(exchangeSymbol))
            return false;

        var (baseAsset, quoteAsset, assetClass) = exchangeId.ToLowerInvariant() switch
        {
            BinanceId or BybitId or OkxId => ParseCryptoConcatenated(exchangeSymbol),
            AlpacaId or InteractiveBrokersId => (exchangeSymbol, null, Enums.AssetClass.Stock),
            KrakenId => ParseCryptoSeparated(exchangeSymbol, NormalizedSeparator),
            _ => (exchangeSymbol, null, Enums.AssetClass.Stock),
        };

        if (baseAsset == null)
            return false;

        symbol = new Symbol(baseAsset, quoteAsset, assetClass);
        return true;
    }

    private static (string? baseAsset, string? quoteAsset, Enums.AssetClass assetClass) ParseCryptoConcatenated(string symbol)
    {
        foreach (var quote in KnownCryptoQuoteAssets)
        {
            if (symbol.EndsWith(quote, StringComparison.OrdinalIgnoreCase) && symbol.Length > quote.Length)
            {
                var baseAsset = symbol[..^quote.Length];
                if (baseAsset.Length >= MinAssetLength && baseAsset.Length <= MaxAssetLength)
                {
                    return (baseAsset, quote, Enums.AssetClass.Crypto);
                }
            }
        }

        return (null, null, Enums.AssetClass.Crypto);
    }

    private static (string? baseAsset, string? quoteAsset, Enums.AssetClass assetClass) ParseCryptoSeparated(string symbol, char separator)
    {
        var parts = symbol.Split(separator);
        if (parts.Length == 2 && parts[0].Length >= MinAssetLength && parts[1].Length >= MinAssetLength)
        {
            return (parts[0], parts[1], Enums.AssetClass.Crypto);
        }

        return (null, null, Enums.AssetClass.Crypto);
    }

    public override string ToString() => Normalized;
}
