namespace ZettaTrade.Infrastructure.Licensing;

/// <summary>
/// The verdict of one license-key validation (T-132). Every non-valid
/// status refuses: activation stores nothing and the loader gate keeps the
/// plugin out — refused-not-defaulted is the module's absolute rule.
/// </summary>
public enum LicenseValidationStatus
{
    /// <summary>The key carries a valid signature, the right product and plugin binding, and is not expired.</summary>
    Valid,

    /// <summary>The protected plugin carries no stored key at all.</summary>
    Missing,

    /// <summary>The key text does not parse into the token shape or its payload is not a parsable claims document.</summary>
    Malformed,

    /// <summary>The payload does not verify against the configured verification key — the key was altered or was not issued by the holder of the private key.</summary>
    Tampered,

    /// <summary>The key verifies but its expiry passed.</summary>
    Expired,

    /// <summary>The key verifies but binds a different product or a different plugin id.</summary>
    WrongProduct,

    /// <summary>No verification key is configured (or the configured text is not a parsable PEM) — the entitlement surface refuses every check until an operator configures one.</summary>
    Unconfigured,
}
