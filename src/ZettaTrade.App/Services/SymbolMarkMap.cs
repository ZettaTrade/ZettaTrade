namespace ZettaTrade.App.Services;

using System.Text.Json;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The persisted per-exchange map of symbol marks (the favorites and the
/// hidden pairs live in two settings keys of the same shape): a JSON object
/// mapping an exchange id to the array of the marked symbols' normalized
/// names, e.g. <c>{"demo":["BTC/USDT"],"bybit":["ETH/USDT"]}</c>.
/// </summary>
/// <remarks>
/// Parsing is fail-safe by contract (the trading-safety rule: never crash
/// the picker over a stored preference): a blank or malformed document, a
/// non-object root, a non-array exchange entry, a non-string array entry
/// and an unparseable symbol name all drop — the usable remainder survives.
/// Serialization is the exact inverse and omits empty exchanges, so a
/// cleared mark never leaves an empty shell behind.
/// </remarks>
public static class SymbolMarkMap
{
    /// <summary>
    /// Parse a stored marks map. Every unusable piece drops (see the class
    /// remarks); the method never throws for stored garbage.
    /// </summary>
    /// <param name="json">The stored JSON document, or null when the key is
    /// absent.</param>
    /// <returns>The exchange-id to marked-names map; empty when nothing
    /// usable was stored.</returns>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
            }

            Dictionary<string, IReadOnlySet<string>> marks = new(StringComparer.Ordinal);
            foreach (var exchange in document.RootElement.EnumerateObject())
            {
                if (exchange.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                HashSet<string> names = new(StringComparer.Ordinal);
                foreach (var entry in exchange.Value.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.String ||
                        entry.GetString() is not { } stored ||
                        string.IsNullOrWhiteSpace(stored))
                    {
                        continue;
                    }

                    try
                    {
                        names.Add(Symbol.Parse(stored.Trim()).Normalized);
                    }
                    catch (ArgumentException)
                    {
                        // A malformed mark drops; the rest of the list survives.
                    }
                }

                if (names.Count > 0)
                {
                    marks[exchange.Name] = names;
                }
            }

            return marks;
        }
        catch (JsonException)
        {
            return new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Serialize a marks map back into the stored shape. Exchanges without
    /// marks are omitted, so the file never carries empty shells, and each
    /// exchange's names are written in their normalized (ordinal) order — a
    /// stable document for the same marks (no save-order noise).
    /// </summary>
    /// <param name="marks">The exchange-id to marked-names map.</param>
    /// <returns>The JSON document (an empty map serializes to <c>{}</c>).</returns>
    public static string Serialize(IReadOnlyDictionary<string, IReadOnlySet<string>> marks)
    {
        Dictionary<string, List<string>> payload = new(StringComparer.Ordinal);
        foreach (var (exchangeId, names) in marks)
        {
            if (names.Count > 0)
            {
                payload[exchangeId] = names
                    .OrderBy(static name => name, StringComparer.Ordinal)
                    .ToList();
            }
        }

        return JsonSerializer.Serialize(payload);
    }

    /// <summary>
    /// Replace one exchange's whole mark entry and return the updated map:
    /// the entry becomes exactly the given names (the caller decides add and
    /// drop), an empty set removes the entry (the same no-empty-shells
    /// contract the serializer keeps), and the other exchanges' entries pass
    /// through untouched.
    /// </summary>
    /// <param name="marks">The current map.</param>
    /// <param name="exchangeId">The exchange whose entry replaces.</param>
    /// <param name="symbolNames">The entry's new normalized names.</param>
    /// <returns>The updated map (the input is never mutated).</returns>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> WithSet(
        IReadOnlyDictionary<string, IReadOnlySet<string>> marks,
        string exchangeId,
        IEnumerable<string> symbolNames)
    {
        HashSet<string> updated = new(symbolNames, StringComparer.Ordinal);

        Dictionary<string, IReadOnlySet<string>> updatedMap = new(StringComparer.Ordinal);
        foreach (var (exchange, names) in marks)
        {
            if (!string.Equals(exchange, exchangeId, StringComparison.Ordinal) && names.Count > 0)
            {
                updatedMap[exchange] = names;
            }
        }

        if (updated.Count > 0)
        {
            updatedMap[exchangeId] = updated;
        }

        return updatedMap;
    }
}
