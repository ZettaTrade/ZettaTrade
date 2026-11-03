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

    /// <summary>Settings key of the persisted chart time frame (a TimeFrame enum name, e.g. H1).</summary>
    public const string ChartTimeFrame = "ChartTimeFrame";

    /// <summary>
    /// Settings key of the persisted chart clock format: true selects the
    /// 12-hour clock, false (the default) the 24-hour one.
    /// </summary>
    public const string ChartUse12HourClock = "ChartUse12HourClock";

    /// <summary>The clock format applied until a stored setting is loaded: the 24-hour clock the axis always printed before the preference existed.</summary>
    public const bool DefaultUse12HourClock = false;

    /// <summary>
    /// Settings key of the persisted AI assistant panel visibility: written on
    /// every toggle, restored by the startup walk before any window opens.
    /// </summary>
    public const string ShowAiPanel = "ShowAiPanel";

    /// <summary>
    /// The panel visibility applied until a stored setting is loaded: visible,
    /// the layout every session saw before the toggle existed.
    /// </summary>
    public const bool DefaultShowAiPanel = true;

    /// <summary>
    /// Settings section holding persisted workspace context choices; the
    /// active-exchange facade keeps its ExchangeId in the same section.
    /// </summary>
    public const string WorkspaceSection = "Workspace";

    /// <summary>
    /// Settings key of the persisted workspace symbol (a normalized Symbol
    /// name, e.g. ETH/BTC): written on every confirmed user selection and
    /// applied as the symbols bootstrap's first-choice preference.
    /// </summary>
    public const string WorkspaceSymbolId = "SymbolId";
}
