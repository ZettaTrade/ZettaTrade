namespace ZettaTrade.Core.Interfaces;

/// <summary>
/// Unified settings service.
/// Handles general settings (JSON files) and secrets (platform-native encryption).
/// </summary>
public interface ISettingsService
{
    /// <summary>Get a typed setting value.</summary>
    Task<T> GetAsync<T>(string section, string key, T defaultValue = default!);

    /// <summary>Set a typed setting value.</summary>
    Task SetAsync<T>(string section, string key, T value);

    /// <summary>Get a secret value (encrypted storage).</summary>
    Task<string?> GetSecretAsync(string key, CancellationToken ct = default);

    /// <summary>Set a secret value (encrypted storage).</summary>
    Task SetSecretAsync(string key, string value, CancellationToken ct = default);

    /// <summary>Remove a secret.</summary>
    Task RemoveSecretAsync(string key, CancellationToken ct = default);

    /// <summary>List secret keys with a given prefix.</summary>
    Task<IReadOnlyList<string>> ListSecretKeysAsync(string prefix = "", CancellationToken ct = default);

    /// <summary>Observable stream of settings changes.</summary>
    IObservable<SettingsChange> Changes { get; }

    /// <summary>Persist all pending changes to disk.</summary>
    Task SaveAsync(CancellationToken ct = default);

    /// <summary>Reload settings from disk (hot-reload support).</summary>
    Task ReloadAsync(CancellationToken ct = default);
}

/// <summary>
/// Represents a single settings change event.
/// </summary>
public sealed record SettingsChange(string Section, string Key, object? OldValue, object? NewValue);
