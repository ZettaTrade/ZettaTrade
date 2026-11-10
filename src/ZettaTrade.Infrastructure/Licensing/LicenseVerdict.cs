namespace ZettaTrade.Infrastructure.Licensing;

/// <summary>
/// The gate verdict the plugin loader's license stage consumes (T-132):
/// whether the plugin may start and which status backed the decision. A
/// plugin outside the protected list is entitled without a check and the
/// status is then informational.
/// </summary>
/// <param name="Entitled">Whether the plugin may load.</param>
/// <param name="Status">The status that backed the verdict.</param>
public sealed record LicenseVerdict(
    bool Entitled,
    LicenseValidationStatus Status);
