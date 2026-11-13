namespace ZettaTrade.App.Services;

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

/// <summary>
/// One named chart template (T-202): a snapshot of the chart face the
/// manager captured at save time — the rendering type, the Y-axis scale
/// mode, the indicator configuration's verbatim JSON and the two overlay
/// toggles. The template is pure data: applying it writes exactly these
/// facets (the per-symbol type/scale under the addressed symbol, the
/// indicators and the overlays as the workspace-level keys), and nothing
/// else.
/// </summary>
/// <param name="Name">The template's name, unique within the store (a
/// save under an existing name replaces it; a rename onto an existing
/// name is refused).</param>
/// <param name="ChartType">The captured <see cref="ZettaTrade.Charting.ChartSeriesType"/>.</param>
/// <param name="ScaleMode">The captured <see cref="ZettaTrade.UI.Charting.PriceScaleMode"/>.</param>
/// <param name="IndicatorsJson">The captured <c>UI.ChartIndicators</c>
/// document, kept verbatim (the rows' own consumer re-parses it on
/// apply — the template never re-shapes the rows it carries).</param>
/// <param name="ShowVwap">The captured VWAP overlay visibility.</param>
/// <param name="ShowVolumeProfile">The captured volume-profile overlay visibility.</param>
public sealed record ChartTemplate(
    string Name,
    ZettaTrade.Charting.ChartSeriesType ChartType,
    ZettaTrade.UI.Charting.PriceScaleMode ScaleMode,
    string IndicatorsJson,
    bool ShowVwap,
    bool ShowVolumeProfile);

/// <summary>
/// One parsed read of the stored templates envelope: the schema version
/// the file carries plus the usable templates. A version that is not the
/// current one answers no templates — a NEWER version's file must never
/// be clobbered by this build's next save (the manager refuses every
/// mutation and reports), and an older-or-absent version is unreadable
/// garbage by the same rule, never a silent substitution.
/// </summary>
/// <param name="Version">The envelope's stored schema version.</param>
/// <param name="Templates">The usable templates (empty unless the version is the current one).</param>
public sealed record ChartTemplatesDocument(int Version, IReadOnlyList<ChartTemplate> Templates);

/// <summary>
/// The stored JSON contract of the named chart templates (T-202): a
/// versioned envelope under <c>UI.ChartTemplates</c> —
/// <c>{"version":1,"templates":[{"name":"Scalp","chartType":"HeikinAshi",
/// "scaleMode":"Logarithmic","indicators":"[...]","showVwap":true,
/// "showVolumeProfile":false}]}</c>, camelCase keys (the
/// <c>DetachedPanels</c> precedent). Parsing is fail-safe by contract
/// (the trading-safety rule: never crash the chart over a stored
/// preference): a malformed document answers an unreadable version with
/// no templates, an unusable entry drops and the remainder survives.
/// Serialization is the exact inverse: the current version, the
/// templates in their name-ordinal order (a stable document for the same
/// store — no save-order noise), nothing filtered (the parse already
/// folded every stored entry into its usable shape).
/// </summary>
public static class ChartTemplatesStore
{
    /// <summary>The schema version this build reads and writes. Bump on a shape change; every other version refuses mutations.</summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// Parse a stored templates envelope. Every unusable piece drops (see
    /// the class remarks); the method never throws for stored garbage.
    /// Within one document a duplicated name keeps the LAST entry (the
    /// later record is the fresher one — the same rule the detached-panel
    /// record keeps).
    /// </summary>
    /// <param name="json">The stored JSON document, or null when the key is absent.</param>
    /// <returns>The parsed document: the stored version and, only for the
    /// current version, the usable templates.</returns>
    public static ChartTemplatesDocument Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new ChartTemplatesDocument(CurrentVersion, []);
        }

        int version;
        List<ChartTemplate> parsed = [];
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("version", out var versionElement) ||
                versionElement.ValueKind != JsonValueKind.Number ||
                !versionElement.TryGetInt32(out version))
            {
                return new ChartTemplatesDocument(0, []);
            }

            if (version != CurrentVersion)
            {
                return new ChartTemplatesDocument(version, []);
            }

            if (!root.TryGetProperty("templates", out var templatesElement) ||
                templatesElement.ValueKind != JsonValueKind.Array)
            {
                return new ChartTemplatesDocument(version, []);
            }

            foreach (var entry in templatesElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object ||
                    entry.TryGetProperty("name", out var nameElement) is false ||
                    nameElement.ValueKind != JsonValueKind.String ||
                    nameElement.GetString() is not { } rawName ||
                    string.IsNullOrWhiteSpace(rawName) ||
                    entry.TryGetProperty("chartType", out var typeElement) is false ||
                    typeElement.ValueKind != JsonValueKind.String ||
                    ParseStoredEnum<ZettaTrade.Charting.ChartSeriesType>(typeElement.GetString()) is not { } chartType ||
                    entry.TryGetProperty("scaleMode", out var scaleElement) is false ||
                    scaleElement.ValueKind != JsonValueKind.String ||
                    ParseStoredEnum<ZettaTrade.UI.Charting.PriceScaleMode>(scaleElement.GetString()) is not { } scaleMode ||
                    entry.TryGetProperty("indicators", out var indicatorsElement) is false ||
                    indicatorsElement.ValueKind != JsonValueKind.String ||
                    indicatorsElement.GetString() is not { } indicators ||
                    string.IsNullOrWhiteSpace(indicators) ||
                    entry.TryGetProperty("showVwap", out var vwapElement) is false ||
                    vwapElement.ValueKind != JsonValueKind.True && vwapElement.ValueKind != JsonValueKind.False ||
                    entry.TryGetProperty("showVolumeProfile", out var profileElement) is false ||
                    profileElement.ValueKind != JsonValueKind.True && profileElement.ValueKind != JsonValueKind.False)
                {
                    // One unusable entry drops; the rest survives.
                    continue;
                }

                parsed.Add(new ChartTemplate(
                    rawName.Trim(),
                    chartType,
                    scaleMode,
                    indicators,
                    vwapElement.GetBoolean(),
                    profileElement.GetBoolean()));
            }
        }
        catch (JsonException)
        {
            // Malformed JSON answers an unreadable version: the manager
            // refuses every mutation, the file stands as it was stored.
            return new ChartTemplatesDocument(0, []);
        }

        return new ChartTemplatesDocument(
            version,
            parsed
                .GroupBy(template => template.Name, StringComparer.Ordinal)
                .Select(group => group.Last())
                .OrderBy(template => template.Name, StringComparer.Ordinal)
                .ToList());
    }

    /// <summary>
    /// Serialize the template list into the stored shape: the current
    /// version's envelope, each template in its name-ordinal order — a
    /// stable document for the same store (no save-order noise). The
    /// shape is written key by key so it exactly mirrors the parse
    /// contract: the enum NAMES (a numeric artifact is not a user
    /// choice), the <c>indicators</c> key (the document rides verbatim —
    /// the writer escapes its inner quotes per the JSON spec), the
    /// camelCase keys.
    /// </summary>
    /// <param name="templates">The template list.</param>
    /// <returns>The JSON envelope.</returns>
    public static string Serialize(IReadOnlyList<ChartTemplate> templates)
    {
        var ordered = templates
            .Where(template => !string.IsNullOrWhiteSpace(template.Name))
            .OrderBy(template => template.Name, StringComparer.Ordinal)
            .ToList();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", CurrentVersion);
            writer.WriteStartArray("templates");
            foreach (var template in ordered)
            {
                writer.WriteStartObject();
                writer.WriteString("name", template.Name);
                writer.WriteString("chartType", template.ChartType.ToString());
                writer.WriteString("scaleMode", template.ScaleMode.ToString());
                writer.WriteString("indicators", template.IndicatorsJson);
                writer.WriteBoolean("showVwap", template.ShowVwap);
                writer.WriteBoolean("showVolumeProfile", template.ShowVolumeProfile);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The stored enum parse in its pure form: the value must name a DEFINED enum member (case-insensitive, trimmed); anything else yields null — the entry drops, never a default substitution. The strict-name contract never rides <see cref="Enum.TryParse"/>'s numeric path: a stored "1" is a numeric artifact, not a user choice, and must not silently become its enumerated value.</summary>
    private static TEnum? ParseStoredEnum<TEnum>(string? stored)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        var trimmed = stored.Trim();
        return Enum.GetNames<TEnum>().Contains(trimmed, StringComparer.OrdinalIgnoreCase)
            ? Enum.Parse<TEnum>(trimmed, ignoreCase: true)
            : null;
    }
}
