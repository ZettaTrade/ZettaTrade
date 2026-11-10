namespace ZettaTrade.Infrastructure.Licensing;

using System.Text.Json;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The entitlement gate (T-132): reads the protected-plugins list (a JSON
/// string array under <see cref="LicensingSettingsKeys.ProtectedPlugins"/>;
/// a malformed document logs loudly and protects EVERYTHING through the
/// <see cref="MalformedListSentinel"/> — an unparsable list must open no
/// gate, the refused-not-defaulted rule), then a protected plugin
/// needs a stored key validated through the validator.
/// </summary>
/// <param name="settings">The settings store the protected list reads from.</param>
/// <param name="validator">The local key validator.</param>
/// <param name="store">The settings-backed key store.</param>
/// <param name="logger">The logger (file-only).</param>
public sealed class LicenseGate(
    ISettingsService settings,
    ILicenseValidator validator,
    ILicenseStore store,
    ILogger<LicenseGate> logger) : ILicenseGate
{
    /// <summary>The sentinel the malformed-list posture reads as "everything protected".</summary>
    private const string MalformedListSentinel = "*";

    /// <inheritdoc/>
    public async Task<LicenseVerdict> EvaluateAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        var protectedPlugins = await ReadProtectedPluginsAsync(cancellationToken);
        if (!protectedPlugins.Contains(pluginId)
            && !protectedPlugins.Contains(MalformedListSentinel))
        {
            return new LicenseVerdict(true, LicenseValidationStatus.Valid);
        }

        var key = await store.GetKeyAsync(pluginId, cancellationToken);
        if (string.IsNullOrWhiteSpace(key))
        {
            return new LicenseVerdict(false, LicenseValidationStatus.Missing);
        }

        var status = await validator.ValidateAsync(pluginId, key, cancellationToken);
        return new LicenseVerdict(status == LicenseValidationStatus.Valid, status);
    }

    private async Task<HashSet<string>> ReadProtectedPluginsAsync(CancellationToken cancellationToken)
    {
        var json = await settings.GetAsync(
            LicensingSettingsKeys.Section,
            LicensingSettingsKeys.ProtectedPlugins,
            LicensingSettingsKeys.DefaultProtectedPlugins);

        try
        {
            var pluginIds = JsonSerializer.Deserialize<string[]>(json, LicenseJson.Options);
            return new HashSet<string>(pluginIds ?? [], StringComparer.Ordinal);
        }
        catch (JsonException fault)
        {
            logger.LogError(
                fault,
                "The protected-plugins setting is not a parsable JSON string array; protecting EVERY plugin until it is fixed — an unparsable list opens no gate.");
            return [MalformedListSentinel];
        }
    }
}

