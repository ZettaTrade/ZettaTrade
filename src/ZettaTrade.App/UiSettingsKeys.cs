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
    /// Settings key of the persisted trade tape panel width (logical pixels):
    /// the same splitter-drag persistence discipline as
    /// <see cref="BookPanelWidth"/>, for the book/tape splitter.
    /// </summary>
    public const string TapePanelWidth = "TapePanelWidth";

    /// <summary>
    /// Settings key of the persisted watchlist panel width (logical pixels):
    /// the same splitter-drag persistence discipline as the book and tape
    /// columns, for the tape/watchlist splitter.
    /// </summary>
    public const string WatchlistPanelWidth = "WatchlistPanelWidth";

    /// <summary>
    /// The panel width applied until a stored width is loaded: the 300 px
    /// both adjustable columns always had before the splitters existed.
    /// </summary>
    public const double DefaultBookPanelWidth = 300;

    /// <summary>See <see cref="DefaultBookPanelWidth"/>; the AI column ships with the same default.</summary>
    public const double DefaultAiPanelWidth = 300;

    /// <summary>See <see cref="DefaultBookPanelWidth"/>; the tape column ships with a narrower
    /// default — a time and sales strip wants less space than the depth panel.</summary>
    public const double DefaultTapePanelWidth = 240;

    /// <summary>
    /// See <see cref="DefaultBookPanelWidth"/>; the watchlist column ships
    /// wide enough for the sparkline block to sit beside the symbol cell.
    /// </summary>
    public const double DefaultWatchlistPanelWidth = 260;

    /// <summary>
    /// Usable-minimum clamp of the order book column (logical pixels): bound
    /// to the column's <c>MinWidth</c>, so the splitter itself stops here and
    /// a stored (or externally written) width below it is clamped on restore.
    /// </summary>
    public const double MinBookPanelWidth = 220;

    /// <summary>See <see cref="MinBookPanelWidth"/>; the AI column clamp.</summary>
    public const double MinAiPanelWidth = 220;

    /// <summary>See <see cref="MinBookPanelWidth"/>; the tape column clamp — at least one
    /// headered time/price/size row set stays readable.</summary>
    public const double MinTapePanelWidth = 160;

    /// <summary>
    /// See <see cref="MinBookPanelWidth"/>; the watchlist column clamp — the
    /// sparkline block and the change cell stay side by side readable.
    /// </summary>
    public const double MinWatchlistPanelWidth = 200;

    /// <summary>
    /// Settings key of the persisted chart indicator overlay configuration:
    /// a JSON array of indicator rows (the ChartIndicatorConfig shape).
    /// </summary>
    public const string ChartIndicators = "ChartIndicators";

    /// <summary>
    /// Settings key of the persisted chart VWAP overlay visibility (T-093):
    /// true shows the session-anchored VWAP line with its ±1σ bands.
    /// </summary>
    public const string ChartShowVwap = "ChartShowVwap";

    /// <summary>
    /// The VWAP overlay visibility applied until a stored setting is loaded:
    /// shown, so a fresh install presents the standard intraday read the
    /// task ships (the T-092 default-indicator precedent).
    /// </summary>
    public const bool DefaultChartShowVwap = true;

    /// <summary>
    /// Settings key of the persisted chart volume-profile visibility
    /// (T-093): true shows the right-margin volume-at-price histogram.
    /// </summary>
    public const string ChartShowVolumeProfile = "ChartShowVolumeProfile";

    /// <summary>
    /// The volume-profile visibility applied until a stored setting is
    /// loaded: shown, per the T-092 default-indicator precedent.
    /// </summary>
    public const bool DefaultChartShowVolumeProfile = true;

    /// <summary>
    /// Settings key of the persisted per-symbol chart rendering type
    /// (T-089): a JSON object (see <c>SymbolChartTypeMap</c>) mapping a
    /// normalized symbol to its chosen <see cref="ZettaTrade.Charting.ChartSeriesType"/>
    /// enum name, e.g. <c>{"BTC/USDT":"HeikinAshi"}</c>. Written on every
    /// selector change, read at every startup walk and re-read live on
    /// external writes; the workspace symbol heads the per-choice restore
    /// (the T-029/T-055 discipline).
    /// </summary>
    public const string ChartSymbolTypes = "ChartSymbolTypes";

    /// <summary>
    /// The chart-type map applied until a stored one is loaded: empty — no
    /// symbol carries a preference, so every symbol starts on the
    /// candlesticks default.
    /// </summary>
    public const string DefaultChartSymbolTypes = "{}";

    /// <summary>
    /// Settings key of the persisted per-symbol Y-axis scale mode (T-090):
    /// a JSON object (see <c>SymbolScaleModeMap</c>) mapping a normalized
    /// symbol to its chosen <see cref="ZettaTrade.UI.Charting.PriceScaleMode"/>
    /// enum name, e.g. <c>{"BTC/USDT":"Logarithmic"}</c>. Written on every
    /// mode change, read at every startup walk and re-read live on external
    /// writes; the workspace symbol heads the per-choice restore (the
    /// T-029/T-055/T-089 discipline).
    /// </summary>
    public const string ChartSymbolScales = "ChartSymbolScales";

    /// <summary>
    /// The scale-mode map applied until a stored one is loaded: empty — no
    /// symbol carries a preference, so every symbol starts on the linear
    /// default.
    /// </summary>
    public const string DefaultChartSymbolScales = "{}";

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

    /// <summary>
    /// Settings key of the persisted first-run disclaimer acknowledgment:
    /// the disclaimer version (an int) the user's acknowledge covers — read
    /// fresh at every launch with no cached copy, so an externally reset
    /// settings file re-shows the dialog on the next run and the settings
    /// hot-reload never fights the check.
    /// </summary>
    public const string DisclaimerVersion = "DisclaimerVersion";

    /// <summary>
    /// The disclaimer acknowledgment applied until a stored value is loaded:
    /// none — a fresh install (or a reset file) has acknowledged no
    /// disclaimer version yet, which is exactly the first-run state.
    /// </summary>
    public const int DefaultDisclaimerVersion = 0;

    /// <summary>
    /// Version of the disclaimer wording shipped by this build: bumped when
    /// the disclaimer text changes, so every previously acknowledged install
    /// re-shows the dialog exactly once per wording update.
    /// </summary>
    public const int DisclaimerCurrentVersion = 1;

    /// <summary>
    /// Settings key of the persisted AI market-context consent: whether the
    /// chat injects the token-bounded market snapshot preamble into every
    /// message. Written by the chat panel's toggle chip, restored by the
    /// startup walk before any window opens.
    /// </summary>
    public const string ChatMarketContext = "ChatMarketContext";

    /// <summary>
    /// The market-context consent applied until a stored setting is loaded:
    /// off — the knowledge stays at explicit opt-in (the silent crowd's chat
    /// keeps the exact pre-consent wording).
    /// </summary>
    public const bool DefaultChatMarketContext = false;

    /// <summary>
    /// Settings key of the persisted first-run wizard announcement flag:
    /// written once — on the wizard's first launch — by ANY exit (finish,
    /// skip or a plain window close), read fresh at every launch with no
    /// cached copy so the settings hot-reload never fights the check. Once
    /// the flag exists, the wizard never re-announces; showing it again is
    /// the settings window's explicit job.
    /// </summary>
    public const string FirstRunWizardShown = "FirstRunWizardShown";

    /// <summary>
    /// The wizard announcement applied until a stored value is loaded: not
    /// shown — a fresh install has seen no wizard yet, which is exactly the
    /// first-run state.
    /// </summary>
    public const bool DefaultFirstRunWizardShown = false;
}
