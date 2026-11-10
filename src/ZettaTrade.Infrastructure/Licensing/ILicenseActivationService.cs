namespace ZettaTrade.Infrastructure.Licensing;

/// <summary>
/// The customer-side activation flow (T-132): validate one pasted key and
/// store it only on a valid verdict. Every other status refuses with
/// nothing written — refused-not-defaulted is the module's absolute rule.
/// </summary>
public interface ILicenseActivationService
{
    /// <summary>
    /// Validates the pasted key for the plugin id and, only on
    /// <see cref="LicenseValidationStatus.Valid"/>, stores it for the
    /// loader gate. Any other status means nothing was stored.
    /// </summary>
    /// <param name="pluginId">The plugin id the key must bind to.</param>
    /// <param name="licenseKey">The pasted key text (surrounding whitespace tolerated).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The validation status; <see cref="LicenseValidationStatus.Valid"/> means the key was stored.</returns>
    Task<LicenseValidationStatus> ActivateAsync(
        string pluginId,
        string licenseKey,
        CancellationToken cancellationToken = default);
}
