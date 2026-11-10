namespace ZettaTrade.Infrastructure.Licensing;

/// <summary>
/// The entitlement gate the plugin loader consults before starting any
/// plugin (T-132): a plugin listed in the protected-plugins setting needs
/// a stored, valid license key; anything else (including the whole open
/// surface with the default empty list) passes without a check.
/// </summary>
public interface ILicenseGate
{
    /// <summary>
    /// Evaluates one plugin's entitlement. Never throws on a bad key — the
    /// verdict carries the refusal status and the loader records it.
    /// </summary>
    /// <param name="pluginId">The plugin id from the manifest.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task<LicenseVerdict> EvaluateAsync(
        string pluginId,
        CancellationToken cancellationToken = default);
}
