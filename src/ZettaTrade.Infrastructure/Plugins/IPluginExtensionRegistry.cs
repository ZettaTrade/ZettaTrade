namespace ZettaTrade.Infrastructure.Plugins;

/// <summary>
/// The read-side of the plugins' registrations (T-131): everything the
/// loaded plugins handed the shell. The <see cref="PluginSdkHost"/> fills
/// the registry once during the startup walk and swaps the snapshot
/// atomically; readers always see a complete, immutable picture.
/// </summary>
public interface IPluginExtensionRegistry
{
    /// <summary>The current immutable snapshot.</summary>
    PluginExtensionSnapshot Snapshot { get; }
}
