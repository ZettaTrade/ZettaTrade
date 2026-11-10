namespace ZettaTrade.Infrastructure.Licensing;

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The RSA-SHA256 validator (T-132): the in-box crypto surface — the
/// verification key is an SPKI PEM the operator configures through
/// settings (<see cref="LicensingSettingsKeys.VerificationKey"/>), the
/// private key never exists on customer machines. Validation order: the
/// configured key first (<see cref="LicenseValidationStatus.Unconfigured"/>
/// refuses everything without one), the token shape, the signature, the
/// claims binding, the expiry.
/// </summary>
/// <param name="settings">The settings store the verification key reads from.</param>
/// <param name="logger">The logger (file-only).</param>
public sealed class LicenseValidator(
    ISettingsService settings,
    ILogger<LicenseValidator> logger) : ILicenseValidator
{
    /// <inheritdoc/>
    public async Task<LicenseValidationStatus> ValidateAsync(
        string pluginId,
        string licenseKey,
        CancellationToken cancellationToken = default)
    {
        var verificationKey = (await settings.GetAsync(
            LicensingSettingsKeys.Section,
            LicensingSettingsKeys.VerificationKey,
            string.Empty)).Trim();

        if (verificationKey.Length == 0)
        {
            logger.LogWarning(
                "The license verification key is not configured; the entitlement check for '{PluginId}' refuses.",
                pluginId);
            return LicenseValidationStatus.Unconfigured;
        }

        if (!LicenseToken.TryParse(licenseKey, out var payload, out var signature))
        {
            return LicenseValidationStatus.Malformed;
        }

        using var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(verificationKey);
        }
        catch (Exception fault) when (
            fault is ArgumentException or FormatException or CryptographicException)
        {
            logger.LogError(
                fault,
                "The configured license verification key is not a parsable public PEM; the entitlement check for '{PluginId}' refuses.",
                pluginId);
            return LicenseValidationStatus.Unconfigured;
        }

        if (!rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
        {
            return LicenseValidationStatus.Tampered;
        }

        LicenseKeyPayload? claims;
        try
        {
            claims = JsonSerializer.Deserialize<LicenseKeyPayload>(payload, LicenseJson.Options);
        }
        catch (JsonException)
        {
            return LicenseValidationStatus.Malformed;
        }

        if (claims is null
            || !string.Equals(claims.Product, LicenseContract.Product, StringComparison.Ordinal)
            || !string.Equals(claims.PluginId, pluginId, StringComparison.Ordinal))
        {
            return LicenseValidationStatus.WrongProduct;
        }

        if (claims.ExpiresAtUtc < DateTimeOffset.UtcNow)
        {
            return LicenseValidationStatus.Expired;
        }

        return LicenseValidationStatus.Valid;
    }
}
