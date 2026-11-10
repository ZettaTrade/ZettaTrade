namespace ZettaTrade.Infrastructure.Licensing;

using System.IO;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The plugins-directory EULA reader (T-132): one settings-scoped scope, a
/// path-hardened file read (<c>{pluginId}.EULA.txt</c> — the id must
/// be a plain file name; any separator or traversal shape refuses the read)
/// and the per-plugin acceptance fact riding settings. Validation stays
/// with <see cref="LicenseValidator"/>; this surface only presents and
/// records the legal consent.
/// </summary>
/// <param name="settings">The settings store the acceptance facts ride.</param>
/// <param name="pluginsDirectory">The plugins directory the EULA files live next to the module DLLs.</param>
/// <param name="logger">The logger (file-only).</param>
public sealed class LicenseEulaService(
    ISettingsService settings,
    string pluginsDirectory,
    ILogger<LicenseEulaService> logger) : ILicenseEulaService
{
    /// <inheritdoc/>
    public async Task<string?> GetTextAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolvePath(pluginId, out var path))
        {
            return null;
        }

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (Exception fault) when (
            fault is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            logger.LogWarning(
                fault,
                "The EULA text for '{PluginId}' could not be read; presenting no acceptance step for it.",
                pluginId);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> IsAcceptedAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            return false;
        }

        return await settings.GetAsync(
            LicensingSettingsKeys.Section,
            LicensingSettingsKeys.EulaAcceptedKey(pluginId),
            false);
    }

    /// <inheritdoc/>
    public async Task MarkAcceptedAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            throw new ArgumentException(
                "The plugin id the EULA acceptance is recorded for must not be blank.",
                nameof(pluginId));
        }

        await settings.SetAsync(
            LicensingSettingsKeys.Section,
            LicensingSettingsKeys.EulaAcceptedKey(pluginId),
            true);
    }

    /// <summary>
    /// Resolves the EULA file path, refusing any plugin id that is not a
    /// plain file name (separators, traversal shapes, reserved characters)
    /// — the pasted id never scopes a read outside the plugins directory.
    /// </summary>
    private bool TryResolvePath(string pluginId, out string path)
    {
        path = string.Empty;

        if (string.IsNullOrWhiteSpace(pluginId))
        {
            return false;
        }

        var id = pluginId.Trim();
        if (Path.GetFileName(id) != id || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        path = Path.Combine(pluginsDirectory, $"{id}.EULA.txt");
        return true;
    }
}
