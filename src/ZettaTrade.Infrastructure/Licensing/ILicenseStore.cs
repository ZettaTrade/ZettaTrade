namespace ZettaTrade.Infrastructure.Licensing;

/// <summary>
/// Where the activated license keys live (T-132): one stored key per
/// plugin id, readable by the loader gate and writable only through the
/// activation service (a key lands here only after a valid validation).
/// </summary>
public interface ILicenseStore
{
    /// <summary>
    /// Reads the stored key for one plugin id; an empty string means none
    /// stored (<see cref="LicenseValidationStatus.Missing"/> at the gate).
    /// </summary>
    /// <param name="pluginId">The plugin id the key belongs to.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task<string> GetKeyAsync(
        string pluginId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the key for one plugin id. Only the activation service
    /// (valid verdict in hand) may call this — a refusal never writes.
    /// </summary>
    /// <param name="pluginId">The plugin id the key belongs to.</param>
    /// <param name="key">The full token text.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task SetKeyAsync(
        string pluginId,
        string key,
        CancellationToken cancellationToken = default);
}
