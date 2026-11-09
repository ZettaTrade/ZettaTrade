namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;

/// <summary>
/// The persisted hotkey-override map (T-102): a JSON object mapping a
/// registry action id to its canonical gesture string, e.g.
/// <c>{"Order.New":"Ctrl+B"}</c>. An action absent from the map rides its
/// registry default.
/// </summary>
/// <remarks>
/// Parsing is fail-safe by contract (the trading-safety rule: never crash
/// the window over a stored preference): a blank or malformed document, a
/// non-object root, a non-string entry value, an unknown action id and an
/// unparseable gesture all drop — the usable remainder survives. The
/// modifier-less verdict lives with <see cref="HotkeyConflicts.IsPlain"/>
/// (the editor's check); the map itself only guarantees parseability, so a
/// plain stored gesture is diagnosed once and kept out of the routes by
/// the service.
/// </remarks>
public static class HotkeyOverrideMap
{
    /// <summary>
    /// Parse a stored override map. Every unusable piece drops (see the
    /// class remarks); the method never throws for stored garbage.
    /// </summary>
    /// <param name="json">The stored JSON document, or null when the key is absent.</param>
    /// <returns>The action-id to canonical-gesture map; empty when nothing usable was stored.</returns>
    public static IReadOnlyDictionary<string, string> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            Dictionary<string, string> map = new(StringComparer.Ordinal);
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.String ||
                    entry.Value.GetString() is not { } storedGesture ||
                    HotkeyRegistry.ActionFor(entry.Name) is not { } action ||
                    HotkeyConflicts.Normalize(storedGesture) is not { } canonical)
                {
                    continue;
                }

                map[action.Id] = canonical;
            }

            return map;
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (ArgumentException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// The map with one entry set or cleared: a null-or-blank gesture
    /// clears the action's override (the action falls back to its
    /// registry default), a gesture replaces it. Pure — the input map is
    /// never mutated.
    /// </summary>
    /// <param name="map">The current override map.</param>
    /// <param name="actionId">The registry action id to address.</param>
    /// <param name="canonicalGesture">The override's canonical gesture, or null to clear.</param>
    public static IReadOnlyDictionary<string, string> WithEntry(
        IReadOnlyDictionary<string, string> map,
        string actionId,
        string? canonicalGesture)
    {
        Dictionary<string, string> updated = new(map, StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(canonicalGesture))
        {
            updated.Remove(actionId);
        }
        else
        {
            updated[actionId] = canonicalGesture;
        }

        return updated;
    }

    /// <summary>
    /// The serializer options: the relaxed encoder keeps the stored document
    /// human-legible — a gesture's <c>+</c> prints as itself (the default
    /// encoder would spell it <c>\u002B</c>). The map's values are only ever
    /// registry ids and canonical gestures, so no relaxed-encoder hazard
    /// applies; the document lives in a plain JSON settings file.
    /// </summary>
    private static readonly JsonSerializerOptions StoredOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Serialize an override map back into the stored shape. Empty maps
    /// serialize to <c>{}</c>, and each action id is written in its
    /// ordinal order — a stable document for the same map (no save-order
    /// noise).
    /// </summary>
    /// <param name="map">The action-id to canonical-gesture map.</param>
    /// <returns>The JSON document (an empty map serializes to <c>{}</c>).</returns>
    public static string Serialize(IReadOnlyDictionary<string, string> map)
    {
        Dictionary<string, string> payload = new(StringComparer.Ordinal);
        foreach (var (id, gesture) in map.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
        {
            payload[key: id] = gesture;
        }

        return JsonSerializer.Serialize(payload, StoredOptions);
    }
}
