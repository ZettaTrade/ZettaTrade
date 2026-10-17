namespace ZettaTrade.Core.ValueObjects;

/// <summary>
/// Represents a trading symbol (e.g., "BTCUSDT", "AAPL").
/// Immutable value object.
/// </summary>
public sealed record Symbol
{
    /// <summary>Raw symbol string as used by the exchange (e.g., "BTCUSDT").</summary>
    public string Raw { get; }

    /// <summary>Base asset (e.g., "BTC").</summary>
    public string BaseAsset { get; }

    /// <summary>Quote asset (e.g., "USDT"). Null for stocks.</summary>
    public string? QuoteAsset { get; }

    public Symbol(string raw, string baseAsset, string? quoteAsset = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raw);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseAsset);

        Raw = raw;
        BaseAsset = baseAsset;
        QuoteAsset = quoteAsset;
    }

    /// <summary>
    /// Parses a symbol string. Assumes format "BASEQUOTE" for crypto.
    /// </summary>
    public static Symbol Parse(string raw)
    {
        // Simple heuristic: split at common quote assets
        var quoteAssets = new[] { "USDT", "BUSD", "USDC", "BTC", "ETH", "USD" };

        foreach (var quote in quoteAssets)
        {
            if (raw.EndsWith(quote, StringComparison.OrdinalIgnoreCase) && raw.Length > quote.Length)
            {
                var baseAsset = raw[..^quote.Length];
                return new Symbol(raw, baseAsset, quote);
            }
        }

        // Fallback: treat as stock or unknown
        return new Symbol(raw, raw);
    }

    public override string ToString() => Raw;
}
