namespace Livery.Themes;

/// <summary>
/// Theme management service.
/// </summary>
public interface IThemeManager
{
    /// <summary>Name of the currently active theme.</summary>
    string CurrentTheme { get; }

    /// <summary>List of all available themes (built-in + custom).</summary>
    IReadOnlyList<ThemeInfo> AvailableThemes { get; }

    /// <summary>Initialize theme manager by loading custom themes from disk.</summary>
    Task InitializeAsync(CancellationToken ct = default);

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
public sealed record ThemeInfo(
    string Name,
    string DisplayName,
    bool IsBuiltIn,
    string[] PreviewColors,
    IReadOnlyDictionary<string, string>? Colors = null);
