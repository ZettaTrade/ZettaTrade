namespace ZettaTrade.Infrastructure.Licensing;

/// <summary>
/// The settings keys the licensing module owns (T-132): one section, the
/// operator's verification key, the protected-plugins list and the
/// per-plugin stored keys.
/// </summary>
public static class LicensingSettingsKeys
{
    /// <summary>The settings section every licensing key lives in.</summary>
    public const string Section = "Licensing";

    /// <summary>
    /// The public verification key (PEM text) the validator checks every
    /// signature against. Empty or unparsable means
    /// <see cref="LicenseValidationStatus.Unconfigured"/> — the module never
    /// defaults to trusting an unverified key.
    /// </summary>
    public const string VerificationKey = "VerificationKey";

    /// <summary>
    /// The protected-plugins setting: a JSON array of plugin ids whose load
    /// requires a valid license key (an empty array — the default —
    /// protects nothing, matching the open-core posture until the first
    /// paid module ships).
    /// </summary>
    public const string ProtectedPlugins = "ProtectedPlugins";

    /// <summary>The default protected-plugins document: nothing is protected.</summary>
    public const string DefaultProtectedPlugins = "[]";

    /// <summary>
    /// The settings key one plugin's stored license key lives under (the
    /// activation dialog writes it, the loader gate reads it).
    /// </summary>
    public static string PluginKey(string pluginId) => $"Key.{pluginId}";

    /// <summary>
    /// The settings key a plugin's EULA once-acceptance fact lives under
    /// (the activation dialog writes it after the customer accepts, and
    /// reads it — acceptance persists, so the text surfaces only at the
    /// first activation).
    /// </summary>
    public static string EulaAcceptedKey(string pluginId) => $"EulaAccepted.{pluginId}";
}
