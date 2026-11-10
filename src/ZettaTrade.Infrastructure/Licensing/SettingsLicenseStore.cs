namespace ZettaTrade.Infrastructure.Licensing;

using ZettaTrade.Core.Interfaces;

/// <summary>
/// The settings-backed license store (T-132): each plugin's activated key
/// rides the settings document under <see cref="LicensingSettingsKeys.Section"/>
/// with the per-plugin key shape <see cref="LicensingSettingsKeys.PluginKey"/> —
/// one local file, no extra database, the same surface the rest of the
/// app's configuration lives on.
/// </summary>
/// <param name="settings">The settings store the keys persist through.</param>
public sealed class SettingsLicenseStore(ISettingsService settings) : ILicenseStore
{
    /// <inheritdoc/>
    public Task<string> GetKeyAsync(
        string pluginId,
        CancellationToken cancellationToken = default) =>
        settings.GetAsync(
            LicensingSettingsKeys.Section,
            LicensingSettingsKeys.PluginKey(pluginId),
            string.Empty);

    /// <inheritdoc/>
    public Task SetKeyAsync(
        string pluginId,
        string key,
        CancellationToken cancellationToken = default) =>
        settings.SetAsync(
            LicensingSettingsKeys.Section,
            LicensingSettingsKeys.PluginKey(pluginId),
            key);
}
