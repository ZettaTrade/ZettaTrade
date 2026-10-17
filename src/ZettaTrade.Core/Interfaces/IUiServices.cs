namespace ZettaTrade.Core.Interfaces;

/// <summary>
/// Theme management service.
/// </summary>
public interface IThemeManager
{
    /// <summary>Name of the currently active theme.</summary>
    string CurrentTheme { get; }

    /// <summary>List of all available themes (built-in + custom).</summary>
    IReadOnlyList<ThemeInfo> AvailableThemes { get; }

    /// <summary>Apply a theme by name.</summary>
    Task ApplyThemeAsync(string themeName, CancellationToken ct = default);

    /// <summary>Load a custom theme from a JSON file.</summary>
    Task LoadCustomThemeAsync(string filePath, CancellationToken ct = default);

    /// <summary>Observable stream of theme changes.</summary>
    IObservable<string> ThemeChanged { get; }
}

/// <summary>
/// Metadata about an available theme.
/// </summary>
public sealed record ThemeInfo(string Name, string DisplayName, bool IsBuiltIn, string[] PreviewColors);

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
}

public sealed record LanguageInfo(string Code, string DisplayName, string NativeName, bool IsPrimary);
