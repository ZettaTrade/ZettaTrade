namespace Livery.Localization;

/// <summary>
/// Localization manager for runtime language switching.
/// </summary>
public interface ILocalizationManager
{
    /// <summary>Current UI culture code (e.g., "en", "ru").</summary>
    string CurrentLanguage { get; }

    /// <summary>Available languages.</summary>
    IReadOnlyList<LanguageInfo> AvailableLanguages { get; }

    /// <summary>Switch UI language at runtime.</summary>
    Task SetLanguageAsync(string cultureCode, CancellationToken ct = default);

    /// <summary>Get a localized string by key.</summary>
    string GetString(string key);

    /// <summary>Get a localized string with format arguments.</summary>
    string GetString(string key, params object[] args);

    /// <summary>Observable stream of language changes.</summary>
    IObservable<string> LanguageChanged { get; }

    /// <summary>
    /// Get reactive observable for a specific localization key.
    /// Emits current value immediately, then on every language change.
    /// Use with ToProperty() in ReactiveUI ViewModels for automatic binding.
    /// </summary>
    IObservable<string> GetObservable(string key);
}

/// <summary>
/// Metadata about an available language.
/// </summary>
public sealed record LanguageInfo(string Code, string DisplayName, string NativeName, bool IsPrimary);
