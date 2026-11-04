namespace ZettaTrade.AI.Mcp;

using System;

/// <summary>
/// Strict enum parsing for the MCP tool arguments: values parse only when
/// they name a member DEFINED by the enum (case-insensitive), and numeric or
/// undefined strings never ride through — trading-safety rule: never
/// substitute defaults for unparsable enums, return an error instead.
/// </summary>
internal static class ToolEnumParser
{
    /// <summary>
    /// Parse <paramref name="text"/> into an enum value only when it names a
    /// defined member (case-insensitive); numeric strings (e.g. "999") and
    /// undefined names (e.g. "IntoTheVoid") refuse.
    /// </summary>
    /// <typeparam name="TEnum">The target enum type.</typeparam>
    /// <param name="text">The raw argument text.</param>
    /// <param name="value">The parsed value when the parse succeeds.</param>
    /// <returns>True only for a defined member; false never mutates the value.</returns>
    public static bool TryParseDefined<TEnum>(string? text, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // The loose TryParse also accepts numeric strings ("999" joins as the
        // value 999 whatever the member set) and groups; the definedness
        // check is what refuses them.
        return Enum.TryParse(text.Trim(), ignoreCase: true, out value)
            && Enum.IsDefined<TEnum>(value);
    }

    /// <summary>
    /// Format the enum's allowed member names for an error message
    /// (case-insensitive parsing accepts any casing, but the canonical names
    /// are what the tool schema pins).
    /// </summary>
    public static string AllowedNames<TEnum>()
        where TEnum : struct, Enum =>
        string.Join(", ", Enum.GetNames<TEnum>());
}
