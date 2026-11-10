namespace ZettaTrade.Infrastructure.Licensing;

using Microsoft.Extensions.Logging;

/// <summary>
/// The activation service (T-132): a thin refused-not-defaulted leg over
/// the validator and the store — validate first, store only on valid, log
/// the refusals (files only). The blank-input shapes refuse without
/// touching the validator at all.
/// </summary>
/// <param name="validator">The local key validator.</param>
/// <param name="store">The settings-backed key store.</param>
/// <param name="logger">The logger (file-only).</param>
public sealed class LicenseActivationService(
    ILicenseValidator validator,
    ILicenseStore store,
    ILogger<LicenseActivationService> logger) : ILicenseActivationService
{
    /// <inheritdoc/>
    public async Task<LicenseValidationStatus> ActivateAsync(
        string pluginId,
        string licenseKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || string.IsNullOrWhiteSpace(licenseKey))
        {
            return LicenseValidationStatus.Malformed;
        }

        var status = await validator.ValidateAsync(
            pluginId.Trim(),
            licenseKey.Trim(),
            cancellationToken);

        if (status != LicenseValidationStatus.Valid)
        {
            logger.LogWarning(
                "The license activation for '{PluginId}' refused with {Status}; nothing stored.",
                pluginId,
                status);
            return status;
        }

        await store.SetKeyAsync(pluginId.Trim(), licenseKey.Trim(), cancellationToken);
        logger.LogInformation(
            "The license for '{PluginId}' activated and stored.",
            pluginId);
        return LicenseValidationStatus.Valid;
    }
}
