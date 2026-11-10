using System;

namespace ZettaTrade.Plugins.Abstractions;

/// <summary>
/// The versioned identity of an out-of-tree plugin (T-131): the manifest
/// the loader reads. It rides the plugin class itself — one attribute per
/// plugin class, the class is the plugin. The identifier is the stable
/// key other plugins may name as a dependency; it must be unique across
/// every loaded plugin. The version is a SemVer 2 string (`major.minor
/// .patch` with an optional `-pre` suffix); the loader refuses anything
/// else loudly.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class ZettaTradePluginAttribute(
    string pluginId,
    string displayName,
    string version,
    params string[] dependencies) : Attribute
{
    /// <summary>The stable identity: unique across all loaded plugins, referenced by <see cref="Dependencies"/>.</summary>
    public string PluginId { get; } = pluginId;

    /// <summary>A human-facing display name (an English literal — end-user naming rides resx keys).</summary>
    public string DisplayName { get; } = displayName;

    /// <summary>The SemVer 2 version (`major.minor.patch` plus an optional `-pre` suffix).</summary>
    public string Version { get; } = version;

    /// <summary>The plugin ids this plugin requires — declared-but-missing means the plugin loads never, loudly.</summary>
    public IReadOnlyList<string> Dependencies { get; } = dependencies;
}
