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
    /// Settings key of the persisted order book panel width (logical pixels):
    /// written by the workspace-layout ViewModel when the chart/book splitter
    /// drag settles, restored by the startup walk before the first frame.
    /// </summary>
    public const string BookPanelWidth = "BookPanelWidth";

    /// <summary>
    /// Settings key of the persisted AI assistant panel width (logical
    /// pixels): the same splitter-drag persistence discipline as
    /// <see cref="BookPanelWidth"/>, for the book/assistant splitter.
    /// </summary>
    public const string AiPanelWidth = "AiPanelWidth";

    /// <summary>
    /// The panel width applied until a stored width is loaded: the 300 px
    /// both adjustable columns always had before the splitters existed.
    /// </summary>
    public const double DefaultBookPanelWidth = 300;

    /// <summary>See <see cref="DefaultBookPanelWidth"/>; the AI column ships with the same default.</summary>
    public const double DefaultAiPanelWidth = 300;

    /// <summary>
    /// Usable-minimum clamp of the order book column (logical pixels): bound
    /// to the column's <c>MinWidth</c>, so the splitter itself stops here and
    /// a stored (or externally written) width below it is clamped on restore.
    /// </summary>
    public const double MinBookPanelWidth = 220;

    /// <summary>See <see cref="MinBookPanelWidth"/>; the AI column clamp.</summary>
    public const double MinAiPanelWidth = 220;

    /// <summary>
    /// Settings key of the persisted chart indicator overlay configuration:
    /// a JSON array of indicator rows (the ChartIndicatorConfig shape).
    /// </summary>
    public const string ChartIndicators = "ChartIndicators";

    /// <summary>
    /// The indicator overlay configuration applied until a stored setting is
    /// loaded: the classic SMA at period 20 enabled, so a fresh install shows
    /// the seam working over the candles.
    /// </summary>
    public const string DefaultChartIndicators = """[{"name":"SMA","enabled":true,"period":20}]""";

    /// <summary>
    /// Settings section holding persisted workspace context choices; the
    /// active-exchange facade keeps its ExchangeId in the same section.
    /// </summary>
    public const string WorkspaceSection = "Workspace";

    /// <summary>
    /// Settings section holding the local MCP server preferences: the AI
    /// assistants' trading tools ride this server, so every knob stays
    /// user-facing and off by default.
    /// </summary>
    public const string McpSection = "Mcp";

    /// <summary>
    /// Settings key of the persisted MCP server enable state (a bool): the
    /// settings window's save flips it; the host runner starts/stops the
    /// server per it, live when the stdio transport can rebind.
    /// </summary>
    public const string McpServerEnabled = "ServerEnabled";

    /// <summary>
    /// The MCP server enable state applied until a stored setting is loaded:
    /// off, because trading tools should never surface to clients the user
    /// has not opted in (and the shipped behavior before the setting existed
    /// was exactly that).
    /// </summary>
    public const bool DefaultMcpServerEnabled = false;

    /// <summary>
    /// Settings key of the persisted workspace symbol (a normalized Symbol
    /// name, e.g. ETH/BTC): written on every confirmed user selection and
    /// applied as the symbols bootstrap's first-choice preference.
    /// </summary>
    public const string WorkspaceSymbolId = "SymbolId";

    /// <summary>
    /// Settings key of the persisted per-exchange favorite symbols: a JSON
    /// map (see <c>SymbolMarkMap</c>) of exchange id to the array of the
    /// starred symbols' normalized names. Written on every favorite toggle,
    /// read by the symbols bootstrap on every exchange switch.
    /// </summary>
    public const string WorkspaceFavoriteSymbols = "FavoriteSymbols";

    /// <summary>
    /// Settings key of the persisted per-exchange hidden symbols: the same
    /// JSON map shape as <see cref="WorkspaceFavoriteSymbols"/>, for the
    /// pairs the picker stops listing until the reveal toggle is on.
    /// </summary>
    public const string WorkspaceHiddenSymbols = "HiddenSymbols";
}
