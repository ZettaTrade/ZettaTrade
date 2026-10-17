namespace ZettaTrade.AI.Mcp;

using System.Text.Json;

/// <summary>
/// Helper methods for creating JSON schemas from anonymous objects.
/// </summary>
public static class JsonSchemaHelper
{
    /// <summary>
    /// Convert an anonymous object to JsonElement for use in MCP tool schemas.
    /// </summary>
    public static JsonElement ToJsonElement(this object obj)
    {
        var json = JsonSerializer.Serialize(obj);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }
}
