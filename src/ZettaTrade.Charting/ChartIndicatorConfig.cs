namespace ZettaTrade.Charting;

using System.Text.Json;

/// <summary>
/// One row of the chart indicator overlay configuration: the indicator
/// name, whether the overlay is enabled and the indicator's period. The
/// rows persist as a JSON array in the UI settings section under the
/// "ChartIndicators" key. The read is tolerant - numbers may arrive as
/// numbers or strings, booleans as real booleans or "true"/"false"
/// strings, unknown keys are ignored - and every unusable row is skipped
/// with a reported problem. Nothing is ever clamped or defaulted.
/// </summary>
public sealed record ChartIndicatorConfig(string Name, bool Enabled, int Period)
{
    /// <summary>The literal JSON row name the write emits ("name").</summary>
    private const string NameKey = "name";

    /// <summary>The literal JSON row key the write emits ("enabled").</summary>
    private const string EnabledKey = "enabled";

    /// <summary>The literal JSON row key the write emits ("period").</summary>
    private const string PeriodKey = "period";

    #region Parse

    /// <summary>
    /// Parse the persisted indicator settings value: the valid rows and the
    /// skip reasons of every unusable one (a malformed payload, a
    /// non-array root or a row missing a usable piece). Pure - the caller
    /// logs the problems.
    /// </summary>
    public static (IReadOnlyList<ChartIndicatorConfig> Configs, IReadOnlyList<string> Problems) ParseList(
        string? settingsJson)
    {
        List<ChartIndicatorConfig> configs = [];
        List<string> problems = [];

        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return (configs, problems);
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(settingsJson);
        }
        catch (JsonException)
        {
            problems.Add("The value is not readable JSON; all indicator rows are skipped.");
            return (configs, problems);
        }

        using (document)
        {
            if (document.RootElement.ValueKind is not JsonValueKind.Array)
            {
                problems.Add("The value does not hold an indicator row array; all rows are skipped.");
                return (configs, problems);
            }

            var rowNumber = 1;
            foreach (var row in document.RootElement.EnumerateArray())
            {
                ReadRow(row, rowNumber, configs, problems);
                rowNumber++;
            }
        }

        return (configs, problems);
    }

    /// <summary>
    /// Read one row: a usable name (a non-empty string), a usable enabled
    /// flag and a usable period (an integer). A row missing any of them, or
    /// not even an object, reports exactly ONE problem and is skipped.
    /// </summary>
    private static void ReadRow(
        JsonElement row,
        int rowNumber,
        List<ChartIndicatorConfig> configs,
        List<string> problems)
    {
        if (row.ValueKind is not JsonValueKind.Object)
        {
            problems.Add($"Row {rowNumber} skipped: the row is not a JSON object.");
            return;
        }

        List<string> issues = [];
        var name = ReadName(row, issues);
        var enabled = ReadEnabled(row, issues);
        var period = ReadPeriod(row, issues);

        if (issues.Count > 0)
        {
            problems.Add($"Row {rowNumber} skipped: {string.Join("; ", issues)}.");
            return;
        }

        configs.Add(new ChartIndicatorConfig(name!, enabled!.Value, period!.Value));
    }

    private static string? ReadName(JsonElement row, List<string> issues)
    {
        if (!row.TryGetProperty(NameKey, out var element) || element.ValueKind is not JsonValueKind.String)
        {
            issues.Add("the row carries no usable name");
            return null;
        }

        var value = element.GetString()?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            issues.Add("the row name is empty");
            return null;
        }

        return value;
    }

    private static bool? ReadEnabled(JsonElement row, List<string> issues)
    {
        if (!row.TryGetProperty(EnabledKey, out var element))
        {
            issues.Add("the row carries no enabled flag");
            return null;
        }

        if (element.ValueKind is JsonValueKind.True)
        {
            return true;
        }

        if (element.ValueKind is JsonValueKind.False)
        {
            return false;
        }

        if (element.ValueKind is JsonValueKind.String &&
            bool.TryParse(element.GetString(), out var parsed))
        {
            return parsed;
        }

        issues.Add("the enabled flag is not a readable boolean");
        return null;
    }

    private static int? ReadPeriod(JsonElement row, List<string> issues)
    {
        if (!row.TryGetProperty(PeriodKey, out var element))
        {
            issues.Add("the row carries no period");
            return null;
        }

        if (element.ValueKind is JsonValueKind.Number)
        {
            if (element.TryGetInt32(out var parsedNumber))
            {
                return parsedNumber;
            }

            issues.Add("the period is not a readable integer");
            return null;
        }

        if (element.ValueKind is JsonValueKind.String &&
            int.TryParse(element.GetString(), out var parsedText))
        {
            return parsedText;
        }

        issues.Add("the period is not a readable integer");
        return null;
    }

    #endregion Parse

    #region Write

    /// <summary>
    /// Serialize the rows back into the compact persisted form (the same
    /// shape the default configuration carries) - the settings use it as
    /// the single key value.
    /// </summary>
    public static string Write(IReadOnlyList<ChartIndicatorConfig> configs)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        return JsonSerializer.Serialize(configs, options);
    }

    #endregion Write
}
