namespace ZettaTrade.Core.Interfaces;

/// <summary>
/// Platform-agnostic secret storage.
/// Implementations use native OS secure storage (DPAPI, Keychain, libsecret).
/// </summary>
public interface ISecretStore
{
    /// <summary>Retrieve a secret by key. Returns null if not found.</summary>
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    /// <summary>Store a secret. Overwrites if key already exists.</summary>
    Task SetAsync(string key, string value, CancellationToken ct = default);

    /// <summary>Remove a secret. No-op if key doesn't exist.</summary>
    Task RemoveAsync(string key, CancellationToken ct = default);

    /// <summary>List all keys matching a prefix.</summary>
    Task<IReadOnlyList<string>> ListKeysAsync(string prefix = "", CancellationToken ct = default);

    /// <summary>Check if the underlying storage is available on this platform.</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
}
