namespace ZettaTrade.App.Services;

using System.Text.Json;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.UI.Charting;

/// <summary>
/// The persisted per-symbol Y-axis scale mode map (T-090): a JSON object
/// mapping a normalized symbol (e.g. <c>BTC/USDT</c>) to its chosen
/// <see cref="PriceScaleMode"/> enum name, e.g.
/// <c>{"BTC/USDT":"Logarithmic"}</c>. The chart's workspace symbol carries
/// the choice the way the per-symbol chart types carry theirs (the
/// T-029/T-055/T-089 per-context restore discipline).
/// </summary>
/// <remarks>
/// Parsing is fail-safe by contract (the trading-safety rule: never crash
/// the chart over a stored preference): a blank or malformed document, a
/// non-object root, a non-string entry value and an unparseable symbol or
/// mode name all drop — the usable remainder survives. Serialization is the
/// exact inverse and omits nothing but empty shells, so a document with no
/// preferences serializes to <c>{}</c>.
/// </remarks>
public static class SymbolScaleModeMap
{
    /// <summary>
    /// Parse a stored scale-mode map. Every unusable piece drops (see the
    /// class remarks); the method never throws for stored garbage.
    /// </summary>
    /// <param name="json">The stored JSON document, or null when the key is
    /// absent.</param>
    /// <returns>The normalized-symbol to scale-mode map; empty when nothing
    /// usable was stored.</returns>
    public static IReadOnlyDictionary<string, PriceScaleMode> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, PriceScaleMode>(StringComparer.Ordinal);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, PriceScaleMode>(StringComparer.Ordinal);
            }

            Dictionary<string, PriceScaleMode> map = new(StringComparer.Ordinal);
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.String ||
                    entry.Value.GetString() is not { } storedMode)
                {
                    continue;
                }

                try
                {
                    var normalized = Symbol.Parse(entry.Name).Normalized;
                    if (ParseStoredMode(storedMode) is { } mode)
                    {
                        map[normalized] = mode;
                    }
                }
                catch (ArgumentException)
                {
                    // A malformed symbol drops its entry; the rest survives.
                }
            }

            return map;
        }
        catch (JsonException)
        {
            return new Dictionary<string, PriceScaleMode>(StringComparer.Ordinal);
        }
    }

    /// <summary>The stored mode parse in its pure form: the value must name
    /// a DEFINED <see cref="PriceScaleMode"/> (case-insensitive, trimmed);
    /// anything else yields null — the entry drops, never a default
    /// substitution. The strict-name contract never rides
    /// <see cref="Enum.TryParse"/>'s numeric path: a stored "1" is a numeric
    /// artifact, not a user choice, and must not silently become its
    /// enumerated value.</summary>
    public static PriceScaleMode? ParseStoredMode(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        return Enum.GetNames<PriceScaleMode>().Contains(stored.Trim(), StringComparer.OrdinalIgnoreCase)
            ? Enum.Parse<PriceScaleMode>(stored.Trim(), ignoreCase: true)
            : null;
    }

    /// <summary>
    /// Serialize a scale-mode map back into the stored shape. Empty maps
    /// serialize to <c>{}</c>, and each symbol is written in its normalized
    /// (ordinal) order — a stable document for the same map (no save-order
    /// noise): the same map serializes identically regardless of the order
    /// its entries reached the dictionary in.
    /// </summary>
    /// <param name="map">The normalized-symbol to scale-mode map.</param>
    /// <returns>The JSON document (an empty map serializes to <c>{}</c>).</returns>
    public static string Serialize(IReadOnlyDictionary<string, PriceScaleMode> map)
    {
        Dictionary<string, string> payload = new(StringComparer.Ordinal);
        foreach (var (symbol, mode) in map.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(symbol))
            {
                payload[key: symbol] = mode.ToString();
            }
        }

        return JsonSerializer.Serialize(payload);
    }

    /// <summary>
    /// Replace one symbol's scale mode and return the updated map: the
    /// symbol's entry becomes exactly the given mode (a pick persists even
    /// when it equals the linear default — the user chose it) and the other
    /// symbols' entries pass through untouched.
    /// </summary>
    /// <param name="map">The current map.</param>
    /// <param name="symbol">The normalized symbol whose entry is written.</param>
    /// <param name="mode">The chosen Y-axis scale mode.</param>
    /// <returns>The updated map (the input is never mutated).</returns>
    public static IReadOnlyDictionary<string, PriceScaleMode> WithEntry(
        IReadOnlyDictionary<string, PriceScaleMode> map,
        string symbol,
        PriceScaleMode mode)
    {
        Dictionary<string, PriceScaleMode> updatedMap = new(StringComparer.Ordinal);
        foreach (var (existing, existingMode) in map)
        {
            if (!string.Equals(existing, symbol, StringComparison.Ordinal))
            {
                updatedMap[existing] = existingMode;
            }
        }

        updatedMap[key: symbol] = mode;

        return updatedMap;
    }
}
