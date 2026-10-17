namespace ZettaTrade.Core.Interfaces;

/// <summary>
/// Settings service for application configuration.
/// Does NOT handle secrets — use ISecretStore directly for sensitive data.
/// </summary>
public interface ISettingsService
{
    /// <summary>Get a typed setting value.</summary>
    Task<T> GetAsync<T>(string section, string key, T defaultValue = default!);

    /// <summary>Set a typed setting value.</summary>
    Task SetAsync<T>(string section, string key, T value);

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
