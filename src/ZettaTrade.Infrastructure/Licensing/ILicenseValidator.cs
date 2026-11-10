namespace ZettaTrade.Infrastructure.Licensing;

/// <summary>
/// The local, offline license-key validator (T-132): a signature check
/// against the operator-configured public key plus the binding and expiry
/// checks. No network, no phone-home — validation reads settings and
/// computes, everything else is refusal.
/// </summary>
public interface ILicenseValidator
{
    /// <summary>
    /// Validates one pasted license key for one plugin id. Never throws on
    /// a bad key — every failure shape maps to a
    /// <see cref="LicenseValidationStatus"/> and only
    /// <see cref="LicenseValidationStatus.Valid"/> means entitled.
    /// </summary>
    /// <param name="pluginId">The plugin id the key must bind to.</param>
    /// <param name="licenseKey">The pasted key text (surrounding whitespace tolerated).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task<LicenseValidationStatus> ValidateAsync(
        string pluginId,
        string licenseKey,
        CancellationToken cancellationToken = default);
}
