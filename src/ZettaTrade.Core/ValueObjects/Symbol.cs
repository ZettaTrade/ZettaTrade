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

    /// <summary>The perpetual's settlement marker ("BTC/USDT:USDT", T-197).</summary>
    private const char SettleSeparator = ':';

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

    /// <summary>
    /// The perpetual's settlement asset (T-197: "USDT" for a Bybit linear
    /// USDT-perp). Null for every spot, stock and index symbol — the settled
    /// category's single honest marker, never a guessed flag.
    /// </summary>
    public string? SettleAsset { get; }

    /// <summary>
    /// Whether the symbol names a perpetual contract (T-197): the
    /// settlement asset carries the category, so an adapter routes a
    /// perpetual's requests to its derivatives wire, never the spot one.
    /// </summary>
    public bool IsPerpetual => SettleAsset is not null;

    /// <summary>Asset class (Crypto, Stock, etc.).</summary>
    public Enums.AssetClass AssetClass { get; }

    /// <summary>
    /// Create a new Symbol instance. A perpetual carries its settlement
    /// asset (T-197): a <c>settleAsset</c> without a quote asset is a
    /// malformed request and throws — the category is never defaulted.
    /// </summary>
    public Symbol(string baseAsset, string? quoteAsset, Enums.AssetClass assetClass, string? settleAsset = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseAsset);

        if (settleAsset != null && quoteAsset == null)
        {
            throw new ArgumentException(
                "A perpetual symbol requires a quote asset alongside its settlement asset",
                nameof(settleAsset));
        }

        BaseAsset = baseAsset.ToUpperInvariant();
        QuoteAsset = quoteAsset?.ToUpperInvariant();
        SettleAsset = settleAsset?.ToUpperInvariant();
        AssetClass = assetClass;
        Normalized = quoteAsset != null
            ? $"{baseAsset.ToUpperInvariant()}/{quoteAsset.ToUpperInvariant()}{(SettleAsset != null ? $":{SettleAsset}" : string.Empty)}"
            : baseAsset.ToUpperInvariant();
    }

    /// <summary>
    /// Get exchange-specific symbol format. A perpetual's wire text is the
    /// same concatenated pair as its spot twin (the v5 category rides the
    /// request, not the symbol text — T-197); every other exchange keeps
    /// the settled normalized form.
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
    /// Parse a normalized symbol string (e.g., "BTC/USDT"; a perpetual
    /// carries its settlement asset as the ccxt-style "BTC/USDT:USDT"
    /// suffix, T-197). A settle-marked single asset ("BTC:") or an empty
    /// settle is a malformed symbol and throws — never a defaulted parse.
    /// </summary>
    public static Symbol Parse(string normalized)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalized);

        var parts = normalized.Split(NormalizedSeparator);
        if (parts.Length == 2)
        {
            var quotePart = parts[1];
            var settleSeparator = quotePart.IndexOf(SettleSeparator);
            if (settleSeparator >= 0)
            {
                var quote = quotePart[..settleSeparator];
                var settle = quotePart[(settleSeparator + 1)..];
                if (quote.Length == 0 || settle.Length == 0 || settle.Contains(SettleSeparator))
                {
                    throw new ArgumentException(
                        $"A perpetual symbol needs exactly one quote and one settlement asset: '{normalized}'",
                        nameof(normalized));
                }

                return new Symbol(parts[0], quote, Enums.AssetClass.Crypto, settle);
            }

            return new Symbol(parts[0], parts[1], Enums.AssetClass.Crypto);
        }

        if (parts.Length > 2)
        {
            throw new ArgumentException(
                $"A normalized symbol carries at most one '/' separator: '{normalized}'",
                nameof(normalized));
        }

        if (parts[0].Contains(SettleSeparator))
        {
            throw new ArgumentException(
                $"A settle-marked asset without a quote is a malformed symbol: '{normalized}'",
                nameof(normalized));
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
