namespace ZettaTrade.Charting;

using System.Text.Json;

/// <summary>
/// The oscillator panes' persisted geometry (T-199): the settled splitter
/// heights keyed by indicator name under the UI section's own settings
/// key. The read is tolerant - numbers may arrive as JSON numbers or
/// numeric strings, unusable rows are skipped (the pane then rides the
/// layout default) - and the write is the compact camel-keyed map the
/// chart's settle pipeline stores. The map lives BESIDE the indicator
/// rows (not inside them): the settings window's row writer owns the rows
/// wholesale, so a pane height written there would be clobbered by the
/// next settings save - a sibling key keeps the two writers disjoint.
/// </summary>
public static class ChartIndicatorPaneHeights
{
    /// <summary>Parse the stored map: every usable entry lands keyed by its indicator name; garbage entries (unparsable values, non-object roots) are skipped, never thrown.</summary>
    public static IReadOnlyDictionary<string, double> Parse(string? settingsJson)
    {
        var heights = new Dictionary<string, double>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return heights;
        }

        try
        {
            using var json = JsonDocument.Parse(settingsJson);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
            {
                return heights;
            }

            foreach (var row in json.RootElement.EnumerateObject())
            {
                if (TryReadHeight(row.Value, out var height) && height > 0)
                {
                    heights[row.Name] = height;
                }
            }
        }
        catch (JsonException)
        {
            // A garbage stored value serves the empty map: every pane rides
            // its default height instead of failing the chart.
        }

        return heights;
    }

    /// <summary>Serialize the map back into the compact persisted form: whole-pixel heights keyed by indicator name, in the map's own order.</summary>
    public static string Write(IReadOnlyDictionary<string, double> heights)
    {
        var rows = heights
            .OrderBy(row => row.Key, StringComparer.Ordinal)
            .Select(row => $"{JsonEncoder.Escape(row.Key)}:{Math.Round(row.Value, MidpointRounding.AwayFromZero):0}");
        return "{" + string.Join(",", rows) + "}";
    }

    private static bool TryReadHeight(JsonElement value, out double height)
    {
        height = 0;
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                if (value.TryGetDouble(out var number))
                {
                    height = number;
                    return true;
                }

                return false;
            case JsonValueKind.String:
                return double.TryParse(value.GetString(), out height) && !double.IsNaN(height);
            default:
                return false;
        }
    }

    /// <summary>JSON string escaping for a persisted key (the codec carries no serializer dependency for one map shape).</summary>
    private static class JsonEncoder
    {
        public static string Escape(string value) => JsonSerializer.Serialize(value);
    }
}
