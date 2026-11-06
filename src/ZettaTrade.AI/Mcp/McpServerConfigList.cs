namespace ZettaTrade.AI.Mcp;

using System.Globalization;
using System.Text.Json;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The persisted external-server list: the rows parse back into
/// <see cref="McpServerConfig"/> entries (property-name matching is case
/// insensitive, transports ride both the enum names and their numbers) and
/// every unusable row is SKIPPED with a reported problem, never defaulted —
/// an unparsable transport or a missing name never silently becomes an
/// enabled stdio entry. Pure: the caller logs the problems.
/// </summary>
public static class McpServerConfigList
{
    #region Parse

    /// <summary>The literal JSON row keys (reads match case-insensitively).</summary>
    private const string NameKey = "name";
    private const string TransportKey = "transport";
    private const string CommandKey = "command";
    private const string ArgsKey = "args";
    private const string UrlKey = "url";
    private const string EnabledKey = "enabled";
    private const string AuthSecretRefKey = "authSecretRef";
    private const string TimeoutKey = "httpTimeoutSeconds";

    /// <summary>
    /// Parse the persisted server list value: the valid rows and the skip
    /// reasons of every unusable one.
    /// </summary>
    public static (IReadOnlyList<McpServerConfig> Servers, IReadOnlyList<string> Problems) Parse(
        string? settingsJson)
    {
        List<McpServerConfig> servers = [];
        List<string> problems = [];

        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return (servers, problems);
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(settingsJson);
        }
        catch (JsonException)
        {
            problems.Add("The value is not readable JSON; all external-server rows are skipped.");
            return (servers, problems);
        }

        using (document)
        {
            if (document.RootElement.ValueKind is not JsonValueKind.Array)
            {
                problems.Add("The value does not hold an external-server row array; all rows are skipped.");
                return (servers, problems);
            }

            var rowNumber = 1;
            foreach (var row in document.RootElement.EnumerateArray())
            {
                ReadRow(row, rowNumber, servers, problems);
                rowNumber++;
            }
        }

        return (servers, problems);
    }

    /// <summary>One row: a usable name and transport decide the entry, the optional pieces ride when usable.</summary>
    private static void ReadRow(
        JsonElement row,
        int rowNumber,
        List<McpServerConfig> servers,
        List<string> problems)
    {
        if (row.ValueKind is not JsonValueKind.Object)
        {
            problems.Add($"Row {rowNumber} skipped: the row is not a JSON object.");
            return;
        }

        List<string> issues = [];
        var name = ReadString(row, NameKey);
        var transport = ReadTransport(row);
        var timeoutOption = ReadOptionalInt(row, TimeoutKey);
        var timeoutUnreadable = row.TryGetProperty(TimeoutKey, out _) && timeoutOption is null;

        if (string.IsNullOrWhiteSpace(name))
        {
            issues.Add("the row carries no name");
        }

        if (transport is null)
        {
            issues.Add("the transport is not a readable Stdio/StreamableHttp value");
        }

        if (timeoutUnreadable)
        {
            issues.Add("the row's httpTimeoutSeconds option is not a readable integer");
        }

        if (issues.Count > 0)
        {
            problems.Add($"Row {rowNumber} skipped: {string.Join("; ", issues)}.");
            return;
        }

        servers.Add(new McpServerConfig
        {
            Name = name!,
            Transport = transport!.Value,
            Command = ReadString(row, CommandKey),
            Args = ReadStringArray(row, ArgsKey),
            Url = ReadString(row, UrlKey),
            AuthSecretRef = ReadString(row, AuthSecretRefKey),
            Enabled = ReadEnabled(row),
            HttpTimeoutSeconds = timeoutOption,
        });
    }

    /// <summary>The enabled flag rides a real boolean or a "true"/"false" string; absent is disabled.</summary>
    private static bool ReadEnabled(JsonElement row)
    {
        if (!row.TryGetProperty(EnabledKey, out var element))
        {
            return false;
        }

        if (element.ValueKind is JsonValueKind.True)
        {
            return true;
        }

        if (element.ValueKind is JsonValueKind.String &&
            bool.TryParse(element.GetString(), out var parsed))
        {
            return parsed;
        }

        return false;
    }

    /// <summary>The optional integer option; a present but unreadable one reads as null (the row keeps no option).</summary>
    /// <remarks>The caller's strictness lives in <see cref="ReadRow"/>'s presence check above.</remarks>
    private static int? ReadOptionalInt(JsonElement row, string key)
    {
        if (!TryGetPropertyIgnoreCase(row, key, out var element))
        {
            return null;
        }

        if (element.ValueKind is JsonValueKind.Number && element.TryGetInt32(out var parsedNumber))
        {
            return parsedNumber;
        }

        if (element.ValueKind is JsonValueKind.String &&
            int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedText))
        {
            return parsedText;
        }

        return null;
    }

    /// <summary>The transport rides both the enum names (case-insensitive) and their numbers; anything else is unusable.</summary>
    private static McpTransport? ReadTransport(JsonElement row)
    {
        if (!TryGetPropertyIgnoreCase(row, TransportKey, out var element))
        {
            return null;
        }

        if (element.ValueKind is JsonValueKind.Number)
        {
            var numeric = element.GetInt32();
            return Enum.IsDefined(typeof(McpTransport), numeric)
                ? (McpTransport)numeric
                : null;
        }

        if (element.ValueKind is JsonValueKind.String)
        {
            var text = element.GetString();
            foreach (McpTransport candidate in Enum.GetValues(typeof(McpTransport)))
            {
                if (string.Equals(candidate.ToString(), text, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>Case-insensitive string property; absent or non-string reads null.</summary>
    private static string? ReadString(JsonElement row, string key)
    {
        if (!TryGetPropertyIgnoreCase(row, key, out var element) ||
            element.ValueKind is not JsonValueKind.String)
        {
            return null;
        }

        return element.GetString();
    }

    /// <summary>The string array option; absent, non-array or non-string members read as absent.</summary>
    private static string[]? ReadStringArray(JsonElement row, string key)
    {
        if (!TryGetPropertyIgnoreCase(row, key, out var element) ||
            element.ValueKind is not JsonValueKind.Array)
        {
            return null;
        }

        return element
            .EnumerateArray()
            .Where(item => item.ValueKind is JsonValueKind.String)
            .Select(item => item.GetString()!)
            .ToArray();
    }

    /// <summary>Case-insensitive property lookup for the row objects (the parse contract's word).</summary>
    private static bool TryGetPropertyIgnoreCase(
        JsonElement row,
        string key,
        out JsonElement property)
    {
        foreach (var propertyInRow in row.EnumerateObject())
        {
            if (string.Equals(propertyInRow.Name, key, StringComparison.OrdinalIgnoreCase))
            {
                property = propertyInRow.Value;
                return true;
            }
        }

        property = default;
        return false;
    }

    #endregion Parse

    #region Write

    /// <summary>
    /// Serialize the entries back into the compact persisted form: camelCase
    /// keys, transport names, args arrays; the same shape the parse reads.
    /// Absent options stay absent — the wire carries no null noise.
    /// </summary>
    public static string Write(IReadOnlyList<McpServerConfig> servers)
    {
        var array = new System.Text.Json.Nodes.JsonArray();

        foreach (var server in servers)
        {
            var row = new System.Text.Json.Nodes.JsonObject
            {
                [NameKey] = server.Name,
                [TransportKey] = ToTransportName(server.Transport),
                [EnabledKey] = server.Enabled,
            };

            if (server.Command is { } command)
            {
                row[CommandKey] = command;
            }

            if (server.Args is { } args)
            {
                var values = new System.Text.Json.Nodes.JsonArray();
                foreach (var arg in args)
                {
                    values.Add(arg);
                }

                row[ArgsKey] = values;
            }

            if (server.Url is { } url)
            {
                row[UrlKey] = url;
            }

            if (server.AuthSecretRef is { } authSecretRef)
            {
                row[AuthSecretRefKey] = authSecretRef;
            }

            if (server.HttpTimeoutSeconds is { } timeoutSeconds)
            {
                row[TimeoutKey] = timeoutSeconds;
            }

            array.Add(row);
        }

        return array.ToJsonString(
            new JsonSerializerOptions { WriteIndented = false });
    }

    /// <summary>The transport's canonical persisted name.</summary>
    private static string ToTransportName(McpTransport transport) =>
        transport == McpTransport.Stdio ? "stdio" : "streamableHttp";

    #endregion Write
}
