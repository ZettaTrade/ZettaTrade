namespace ZettaTrade.Infrastructure.Plugins;

using ZettaTrade.Plugins.Abstractions.Extensions;

/// <summary>
/// The mutable shell behind <see cref="IPluginExtensionRegistry"/> (T-131):
/// the <see cref="PluginSdkHost"/> writes one snapshot during the startup
/// walk, readers see atomic swaps afterwards. Nothing mutates a snapshot —
/// a change always builds a new one.
/// </summary>
public sealed class PluginExtensionRegistry : IPluginExtensionRegistry
{
    private volatile PluginExtensionSnapshot _snapshot = PluginExtensionSnapshot.Empty;

    /// <inheritdoc/>
    public PluginExtensionSnapshot Snapshot => _snapshot;

    /// <summary>Swaps the snapshot in atomically (the loader's single write; the volatile field orders it against the readers).</summary>
    internal void Replace(PluginExtensionSnapshot snapshot) =>
        _snapshot = snapshot;
}

/// <summary>
/// The loader's loud-failure channel (T-131): what loaded, what was
/// refused and why. Every list is sorted — the report is stable text, the
/// goldens can pin it. The errors also land in the log at the Error
/// level; the report makes the loudness inspectable.
/// </summary>
/// <param name="LoadedPluginIds">The manifest ids that made it through every gate.</param>
/// <param name="RefusedPluginIds">The manifest ids the gates refused at least once.</param>
/// <param name="Errors">The refusal reasons, human-sorted, one per verdict.</param>
public sealed record PluginLoadReport(
    IReadOnlyList<string> LoadedPluginIds,
    IReadOnlyList<string> RefusedPluginIds,
    IReadOnlyList<string> Errors)
{
    /// <summary>The empty state: no scan has run (or none produced anything).</summary>
    public static readonly PluginLoadReport Empty = new([], [], []);
}
