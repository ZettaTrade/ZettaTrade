using System.Text.RegularExpressions;

namespace ZettaTrade.Infrastructure.Plugins;

/// <summary>
/// The SemVer gate for plugin manifests (T-131): a <see cref="ZettaTrade.Plugins.Abstractions.ZettaTradePluginAttribute.Version"/>
/// is valid only in the SemVer 2 shape — `major.minor.patch`, an optional
/// `-pre` release part and an optional `+build` metadata part. Anything
/// else refuses the plugin loudly.
/// </summary>
public static partial class PluginSemVer
{
    [GeneratedRegex(@"^(?<core>\d+\.\d+\.\d+)(?<pre>-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?(?<build>\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$")]
    private static partial Regex Pattern();

    /// <summary>
    /// Whether the text parses as SemVer 2. Assume the caller has already
    /// trimmed; whitespace inside never belongs to a version.
    /// </summary>
    /// <param name="version">The candidate version text.</param>
    /// <param name="canonicalCore">The numeric core (`major.minor.patch`) when the text parses.</param>
    public static bool TryParse(
        string? version,
        out string canonicalCore)
    {
        canonicalCore = string.Empty;
        var match = Pattern().Match(version ?? string.Empty);
        if (!match.Success)
        {
            return false;
        }

        canonicalCore = match.Groups["core"].Value;
        return true;
    }
}
