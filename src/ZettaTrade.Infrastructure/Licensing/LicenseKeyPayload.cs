namespace ZettaTrade.Infrastructure.Licensing;

/// <summary>
/// The claims a signed license key carries (T-132): the product binding,
/// the entitled plugin id, the licensee subject and the validity window.
/// The payload travels inside the token's middle segment and is signed
/// exactly as serialized — a byte-flip breaks the signature.
/// </summary>
/// <param name="Product">The product the key was issued for (must equal <see cref="LicenseContract.Product"/>).</param>
/// <param name="PluginId">The plugin id the key entitles (the loader gate matches it against the manifest id).</param>
/// <param name="Subject">The licensee identity the issuer wrote (an email, an order reference — opaque to the app).</param>
/// <param name="IssuedAtUtc">The issuance moment.</param>
/// <param name="ExpiresAtUtc">The expiry moment; validation past it refuses with <see cref="LicenseValidationStatus.Expired"/>.</param>
public sealed record LicenseKeyPayload(
    string Product,
    string PluginId,
    string Subject,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc);
