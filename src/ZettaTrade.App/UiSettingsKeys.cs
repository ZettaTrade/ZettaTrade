namespace ZettaTrade.App;

/// <summary>
/// Settings keys of the user interface preferences section (theme, language).
/// Single source of truth for App-level UI settings access.
/// </summary>
public static class UiSettingsKeys
{
    /// <summary>Settings section holding user interface preferences.</summary>
    public const string Section = "UI";

    /// <summary>Settings key of the selected theme.</summary>
    public const string Theme = "Theme";

    /// <summary>Settings key of the selected language.</summary>
    public const string Language = "Language";

    /// <summary>Canonical Name of the theme applied until a stored setting is loaded.</summary>
    public const string DefaultTheme = "dark";

    /// <summary>Language applied until a stored setting is loaded, empty string means "follow the OS culture".</summary>
    public const string DefaultLanguage = "en";
}
