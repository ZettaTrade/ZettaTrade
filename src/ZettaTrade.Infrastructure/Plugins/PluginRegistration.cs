namespace ZettaTrade.Infrastructure.Plugins;

using ZettaTrade.Plugins.Abstractions.Extensions;

/// <summary>The plugin-owned registration of a panel descriptor (T-131).</summary>
/// <param name="PluginId">The owning plugin's manifest id.</param>
/// <param name="Panel">The plugin's descriptor.</param>
public sealed record PluginPanelRegistration(
    string PluginId,
    PluginPanelDescriptor Panel);

/// <summary>The plugin-owned registration of a toolbar item descriptor (T-131).</summary>
/// <param name="PluginId">The owning plugin's manifest id.</param>
/// <param name="Item">The plugin's descriptor.</param>
public sealed record PluginToolbarRegistration(
    string PluginId,
    PluginToolbarItemDescriptor Item);

/// <summary>The plugin-owned registration of a settings section descriptor (T-131).</summary>
/// <param name="PluginId">The owning plugin's manifest id.</param>
/// <param name="Section">The plugin's descriptor.</param>
public sealed record PluginSettingsSectionRegistration(
    string PluginId,
    PluginSettingsSectionDescriptor Section);

/// <summary>
/// One atomic snapshot of the loaded plugins' registrations: panels,
/// toolbar items and settings sections, each stamped with its ownership.
/// Immutable by construction — a host that wants a change builds a new
/// snapshot and swaps it in.
/// </summary>
/// <param name="Panels">Every loaded plugin's panel descriptors.</param>
/// <param name="ToolbarItems">Every loaded plugin's toolbar item descriptors.</param>
/// <param name="SettingsSections">Every loaded plugin's settings section descriptors.</param>
public sealed record PluginExtensionSnapshot(
    IReadOnlyList<PluginPanelRegistration> Panels,
    IReadOnlyList<PluginToolbarRegistration> ToolbarItems,
    IReadOnlyList<PluginSettingsSectionRegistration> SettingsSections)
{
    /// <summary>The empty state the registry answers before the loader runs.</summary>
    public static readonly PluginExtensionSnapshot Empty =
        new([], [], []);
}
