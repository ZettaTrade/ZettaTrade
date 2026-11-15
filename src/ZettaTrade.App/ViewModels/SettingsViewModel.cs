namespace ZettaTrade.App.ViewModels;

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Reactive.Linq;
using Livery.Localization;
using Livery.Themes;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.AI.Chat;
using ZettaTrade.AI.Mcp;
using ZettaTrade.App.Services;
using ZettaTrade.Charting;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Backup;
using ZettaTrade.Infrastructure.Configuration;
using ZettaTrade.Infrastructure.Exchange;
using ZettaTrade.Infrastructure.Shell;
using ZettaTrade.UI.Extensions;
/// <summary>The half-day-agnostic status byte ReactiveUI.Primitives names RxVoid.</summary>
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// ViewModel for the settings window (theme, language, etc.).
/// Delegates available options to ThemeManager and LocalizationManager (DRY, SoC).
/// Changes take effect only on the explicit <see cref="SaveAsync"/> call: the
/// window starts from selections synced to the applied state
/// (<see cref="ResetSelectionsAsync"/>) and only a real difference is applied and
/// persisted, so closing without saving never leaves a half-applied state.
/// The pairs tab manages both workspace marks: the all-pairs list and the
/// hidden list form the hidden draft (a hide move or a reveal lands in it
/// at once), and every all-pairs row carries a star button feeding the
/// favorites draft. A starred pair pins to the list head and is never
/// hidden until unstarred (the marks are mutually exclusive by rule: a
/// favorite beats a stored hidden entry). Only Save applies and persists
/// the real difference.
/// Theme diagnostics are read-only: broken files are listed
/// (<see cref="ThemeFileErrors"/>) and the user themes folder opens through
/// the injected <see cref="IShellUtils"/> (platform code stays in
/// Infrastructure).
/// </summary>
public sealed class SettingsViewModel : ReactiveObject
{
    #region Constants

    /// <summary>Combo box index meaning "nothing selected".</summary>
    private const int NoSelection = -1;

    /// <summary>Combo box index of the default 24-hour clock option.</summary>
    private const int Hours24Index = 0;

    /// <summary>Combo box index of the 12-hour clock option.</summary>
    private const int Hours12Index = 1;

    /// <summary>The SMA row name the indicator overlay settings hold (the catalog resolves it).</summary>
    private const string SmaIndicatorName = "SMA";

    /// <summary>The persisted SMA period sentinel meaning "no readable SMA row is cached yet" (the selection shows empty).</summary>
    private const int NoSmaPeriod = 0;

    /// <summary>
    /// The standard pack's row names (T-092): the plugin's canonical names,
    /// keyed against the scanned catalog exactly the SMA row is keyed. They
    /// stay product names, not localized strings.
    /// </summary>
    private const string EmaIndicatorName = "EMA";
    private const string WmaIndicatorName = "WMA";
    private const string RsiIndicatorName = "RSI";
    private const string MacdIndicatorName = "MACD";
    private const string AtrIndicatorName = "ATR";
    private const string BollIndicatorName = "BOLL";

    /// <summary>The period sentinel the pack rows share: "no row cached yet" (the selection shows empty).</summary>
    private const int NoRowPeriod = 0;

    /// <summary>The localization keys the settings port's outcomes render through (values composed at event time).</summary>
    private const string PortExportedToKey = "Port.ExportedTo";
    private const string PortImportAppliedKey = "Port.ImportApplied";
    private const string PortResetDoneKey = "Port.ResetDone";
    private const string PortResetDeclinedKey = "Port.ResetDeclined";

    /// <summary>The per-action failure lines (each names its own action; nothing else composes).</summary>
    private const string PortExportFailedKey = "Port.Error.ExportFailed";
    private const string PortImportRejectedKey = "Port.Error.ImportRejected";
    private const string PortApplyFailedKey = "Port.Error.ApplyFailed";
    private const string PortResetFailedKey = "Port.Error.ResetFailed";

    /// <summary>The per-reason rejection lines the import's validation surfaces.</summary>
    private const string PortErrorMalformedJsonKey = "Port.Error.MalformedJson";
    private const string PortErrorSchemaKey = "Port.Error.Schema";
    private const string PortErrorVersionKey = "Port.Error.Version";
    private const string PortErrorUnknownEntryKey = "Port.Error.UnknownEntry";
    private const string PortErrorWrongTypeKey = "Port.Error.WrongType";
    private const string PortImportNoChangeKey = "Port.ImportNoChange";

    /// <summary>The localization keys the profile backup's outcomes render through (values composed at event time).</summary>
    private const string BackupDoneKey = "Backup.Status.BackupDone";
    private const string BackupRestoreDoneKey = "Backup.Status.RestoreDone";
    private const string BackupRestoreDeclinedKey = "Backup.Status.RestoreDeclined";

    /// <summary>The per-action failure lines (each names its own action; nothing else composes).</summary>
    private const string BackupFailedKey = "Backup.Error.BackupFailed";
    private const string BackupRestoreFailedKey = "Backup.Error.RestoreFailed";

    /// <summary>The per-reason rejection lines the restore's validation surfaces.</summary>
    private const string BackupErrorMalformedKey = "Backup.Error.Malformed";
    private const string BackupErrorSchemaKey = "Backup.Error.Schema";
    private const string BackupErrorVersionKey = "Backup.Error.Version";
    private const string BackupErrorManifestKey = "Backup.Error.Manifest";
    private const string BackupErrorEmptyKey = "Backup.Error.Empty";
    private const string BackupErrorLockedKey = "Backup.Error.Locked";

    /// <summary>The preview row's byte-size line ({0} is the human-readable size).</summary>
    private const string BackupRowBytesKey = "Backup.Row.Bytes";

    #endregion Constants

    #region Properties

    private int _selectedThemeIndex = NoSelection;
    private int _selectedLanguageIndex = NoSelection;

    /// <summary>
    /// The applied clock format, synced from the settings service (the
    /// source of truth): the async initial read seeds it at construction,
    /// and the changes stream keeps it current — the selections sync from
    /// this cache on every window open.
    /// </summary>
    private bool _persistedUse12HourClock = UiSettingsKeys.DefaultUse12HourClock;

    /// <summary>Selected time format index (0 = 24-hour, 1 = 12-hour); <see cref="NoSelection"/> while the initial read has not landed.</summary>
    private int _selectedTimeFormatIndex = NoSelection;

    /// <summary>The category rail's selected index (T-184): view state only —
    /// the rail and the TabControl's content host ride this one property, the
    /// window opens on the first category and never persists the choice.</summary>
    private int _selectedTabIndex;
    private IReadOnlyList<ThemeFileLoadError> _themeFileErrors = Array.Empty<ThemeFileLoadError>();

    private bool _isSmaEnabled;

    private string _smaPeriodText = string.Empty;

    /// <summary>The standard pack's enable drafts (T-092): the checkboxes the save applies real differences from.</summary>
    private bool _isEmaEnabled;
    private bool _isWmaEnabled;
    private bool _isRsiEnabled;
    private bool _isMacdEnabled;
    private bool _isAtrEnabled;
    private bool _isBollEnabled;

    /// <summary>The standard pack's period drafts: free text, unparsable input skips the row on the save.</summary>
    private string _emaPeriodText = string.Empty;
    private string _wmaPeriodText = string.Empty;
    private string _rsiPeriodText = string.Empty;
    private string _macdPeriodText = string.Empty;
    private string _atrPeriodText = string.Empty;
    private string _bollPeriodText = string.Empty;

    /// <summary>Selected theme index (combo box); <see cref="NoSelection"/> when nothing is selected.</summary>
    public int SelectedThemeIndex
    {
        get => _selectedThemeIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedThemeIndex, value);
    }

    /// <summary>Selected language index (combo box); <see cref="NoSelection"/> when nothing is selected.</summary>
    public int SelectedLanguageIndex
    {
        get => _selectedLanguageIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedLanguageIndex, value);
    }

    /// <summary>Selected time format index (0 = 24-hour, 1 = 12-hour); <see cref="NoSelection"/> when nothing is selected.</summary>
    public int SelectedTimeFormatIndex
    {
        get => _selectedTimeFormatIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedTimeFormatIndex, value);
    }

    /// <summary>The category rail's selected index (0 = Appearance … 3 = Workspace):
    /// the T-184 rail and the TabControl's index binding share this one state —
    /// the window opens on the first category; the choice is view state and is
    /// never persisted (no settings seam moves).</summary>
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedTabIndex, value);
    }

    /// <summary>
    /// Whether the SMA overlay row is enabled. A selection - like every other
    /// one here - applies only on the explicit Save: the checkbox is the
    /// selection, the persisted row is the applied state.
    /// </summary>
    public bool IsSmaEnabled
    {
        get => _isSmaEnabled;
        set => this.RaiseAndSetIfChanged(ref _isSmaEnabled, value);
    }

    /// <summary>
    /// The SMA period as typed. Free text on purpose: an unparsable value
    /// simply fails the save (the cached config stays in charge - nothing
    /// defaults or clamps), the invariant culture parses it.
    /// </summary>
    public string SmaPeriodText
    {
        get => _smaPeriodText;
        set => this.RaiseAndSetIfChanged(ref _smaPeriodText, value);
    }

    /// <summary>
    /// Whether the scanned plugin catalog actually registers the SMA
    /// indicator: the indicators tab renders the SMA row only for a plugin
    /// the scan loaded (a missing or skipped-by-scan plugin assembly would
    /// otherwise render a dead row). The plugin set is fixed for the
    /// session (the scan runs at startup only), so the flag is settled at
    /// construction.
    /// </summary>
    public bool IsSmaPluginLoaded { get; }

    /// <summary>
    /// Whether the scanned plugin catalog registered no indicators at all:
    /// the indicators tab shows the honest empty state (nothing was found
    /// beside the application's plugin folder) instead of any row. Mutually
    /// exclusive with <see cref="IsSmaPluginLoaded"/> by construction.
    /// </summary>
    public bool IsIndicatorCatalogEmpty { get; }

    /// <summary>
    /// The standard pack's per-plugin gates (T-092): exactly the SMA row's
    /// contract — the row renders only when the scanned catalog actually
    /// registers the name, and a save never fabricates a config row for a
    /// plugin the session does not own. The plugin set is fixed for the
    /// session, so the flags are settled at construction.
    /// </summary>
    public bool IsEmaPluginLoaded { get; }

    /// <summary>The WMA row's loaded-plugin gate (see <see cref="IsEmaPluginLoaded"/>).</summary>
    public bool IsWmaPluginLoaded { get; }

    /// <summary>The RSI row's loaded-plugin gate (see <see cref="IsEmaPluginLoaded"/>).</summary>
    public bool IsRsiPluginLoaded { get; }

    /// <summary>The MACD row's loaded-plugin gate (see <see cref="IsEmaPluginLoaded"/>).</summary>
    public bool IsMacdPluginLoaded { get; }

    /// <summary>The ATR row's loaded-plugin gate (see <see cref="IsEmaPluginLoaded"/>).</summary>
    public bool IsAtrPluginLoaded { get; }

    /// <summary>The Bollinger row's loaded-plugin gate (see <see cref="IsEmaPluginLoaded"/>).</summary>
    public bool IsBollPluginLoaded { get; }

    /// <summary>The EMA overlay row's enable draft (the checkbox; the save applies a real difference only).</summary>
    public bool IsEmaEnabled
    {
        get => _isEmaEnabled;
        set => this.RaiseAndSetIfChanged(ref _isEmaEnabled, value);
    }

    /// <summary>
    /// The EMA period as typed. Free text on purpose: an unparsable value
    /// skips the row on the save (the cached config stays in charge -
    /// nothing defaults or clamps), the invariant culture parses it. The
    /// other pack rows' period drafts carry the same contract.
    /// </summary>
    public string EmaPeriodText
    {
        get => _emaPeriodText;
        set => this.RaiseAndSetIfChanged(ref _emaPeriodText, value);
    }

    /// <summary>The WMA overlay row's enable draft.</summary>
    public bool IsWmaEnabled
    {
        get => _isWmaEnabled;
        set => this.RaiseAndSetIfChanged(ref _isWmaEnabled, value);
    }

    /// <summary>The WMA period as typed.</summary>
    public string WmaPeriodText
    {
        get => _wmaPeriodText;
        set => this.RaiseAndSetIfChanged(ref _wmaPeriodText, value);
    }

    /// <summary>The RSI overlay row's enable draft.</summary>
    public bool IsRsiEnabled
    {
        get => _isRsiEnabled;
        set => this.RaiseAndSetIfChanged(ref _isRsiEnabled, value);
    }

    /// <summary>The RSI period as typed.</summary>
    public string RsiPeriodText
    {
        get => _rsiPeriodText;
        set => this.RaiseAndSetIfChanged(ref _rsiPeriodText, value);
    }

    /// <summary>The MACD overlay row's enable draft.</summary>
    public bool IsMacdEnabled
    {
        get => _isMacdEnabled;
        set => this.RaiseAndSetIfChanged(ref _isMacdEnabled, value);
    }

    /// <summary>The MACD period as typed (the fast EMA's length; the plugin pins the classic trio's slow/signal).</summary>
    public string MacdPeriodText
    {
        get => _macdPeriodText;
        set => this.RaiseAndSetIfChanged(ref _macdPeriodText, value);
    }

    /// <summary>The ATR overlay row's enable draft.</summary>
    public bool IsAtrEnabled
    {
        get => _isAtrEnabled;
        set => this.RaiseAndSetIfChanged(ref _isAtrEnabled, value);
    }

    /// <summary>The ATR period as typed.</summary>
    public string AtrPeriodText
    {
        get => _atrPeriodText;
        set => this.RaiseAndSetIfChanged(ref _atrPeriodText, value);
    }

    /// <summary>The Bollinger overlay row's enable draft.</summary>
    public bool IsBollEnabled
    {
        get => _isBollEnabled;
        set => this.RaiseAndSetIfChanged(ref _isBollEnabled, value);
    }

    /// <summary>The Bollinger window as typed (the deviation count stays the classic 2).</summary>
    public string BollPeriodText
    {
        get => _bollPeriodText;
        set => this.RaiseAndSetIfChanged(ref _bollPeriodText, value);
    }

    /// <summary>
    /// Whether the local MCP server is enabled. A selection - like every
    /// other one here - applies only on the explicit Save: the checkbox is
    /// the selection, the persisted row is the applied state, and the saved
    /// match starts/stops the host live (the state below renders what stuck).
    /// </summary>
    public bool IsMcpServerEnabled
    {
        get => _isMcpServerEnabled;
        set => this.RaiseAndSetIfChanged(ref _isMcpServerEnabled, value);
    }

    /// <summary>
    /// Whether the update checker runs (T-213). A selection like every
    /// other one here: applies only on the explicit Save — the persisted
    /// write lands and the checker's change stream applies it live (no
    /// host apply is needed; the next cadence tick, manual ask or boot
    /// honors the saved choice).
    /// </summary>
    public bool IsUpdateCheckEnabled
    {
        get => _isUpdateCheckEnabled;
        set => this.RaiseAndSetIfChanged(ref _isUpdateCheckEnabled, value);
    }

    /// <summary>
    /// Whether the auto-update loop runs (T-258). A selection like every
    /// other one here: applies only on the explicit Save — the persisted
    /// write lands and the staging service's change stream applies it
    /// live. The default is off: nothing downloads, stages or asks before
    /// the user switches the loop on.
    /// </summary>
    public bool IsAutoUpdateEnabled
    {
        get => _isAutoUpdateEnabled;
        set => this.RaiseAndSetIfChanged(ref _isAutoUpdateEnabled, value);
    }

    /// <summary>
    /// The update channel pick's combo index (T-258): 0 = stable (the
    /// released stables only), 1 = beta (every parsable release,
    /// pre-releases included). A selection like every other one here:
    /// applies only on the explicit Save — the checker's own change
    /// subscription ranks the next check against the saved channel.
    /// </summary>
    public int SelectedUpdateChannelIndex
    {
        get => _selectedUpdateChannelIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedUpdateChannelIndex, value);
    }

    #region AI provider (T-207)

    /// <summary>
    /// The AI chat provider pick's combo index: 0 = Ollama (the shipped
    /// local-first default), 1 = OpenAI-compatible. A selection - like every
    /// other one here - applies only on the explicit Save; the pick swaps
    /// the visible field stacks live (the two derived flags below).
    /// </summary>
    public int AiProviderIndex
    {
        get => _aiProviderIndex;
        set
        {
            if (_aiProviderIndex == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _aiProviderIndex, value);
            this.RaisePropertyChanged(nameof(IsAiOllama));
            this.RaisePropertyChanged(nameof(IsAiOpenAiCompatible));
        }
    }

    /// <summary>The Ollama stack's visibility (the pick rides Ollama).</summary>
    public bool IsAiOllama => _aiProviderIndex == OllamaProviderIndex;

    /// <summary>The OpenAI-compatible stack's visibility (the pick rides it).</summary>
    public bool IsAiOpenAiCompatible => _aiProviderIndex == OpenAiProviderIndex;

    /// <summary>The Ollama base URL as drafted (applies on Save).</summary>
    public string OllamaUrlDraft
    {
        get => _ollamaUrlDraft;
        set => this.RaiseAndSetIfChanged(ref _ollamaUrlDraft, value);
    }

    /// <summary>The Ollama model tag as drafted (applies on Save).</summary>
    public string OllamaModelDraft
    {
        get => _ollamaModelDraft;
        set => this.RaiseAndSetIfChanged(ref _ollamaModelDraft, value);
    }

    /// <summary>The OpenAI-compatible base URL as drafted (applies on Save).</summary>
    public string OpenAiBaseUrlDraft
    {
        get => _openAiBaseUrlDraft;
        set => this.RaiseAndSetIfChanged(ref _openAiBaseUrlDraft, value);
    }

    /// <summary>The OpenAI-compatible model name as drafted (applies on Save;
    /// an emptied box saves absence — no model is a send-time error, never a
    /// guessed name).</summary>
    public string OpenAiModelDraft
    {
        get => _openAiModelDraft;
        set => this.RaiseAndSetIfChanged(ref _openAiModelDraft, value);
    }

    /// <summary>
    /// The OpenAI-compatible API key as typed. NOT a save-applied draft:
    /// the secret commits to the secret store immediately per keystroke
    /// (the key's only home — it never rides the settings file, the logs or
    /// the port export). The box re-seeds empty on every window open (the
    /// secret never echoes; the re-seed never touches the stored key).
    /// </summary>
    public string OpenAiApiKeyDraft
    {
        get => _openAiApiKeyDraft;
        set => this.RaiseAndSetIfChanged(ref _openAiApiKeyDraft, value);
    }

    #endregion AI provider (T-207)

    /// <summary>The MCP tab's honest running row: the server is live.</summary>
    public bool IsMcpRunning => _mcpHostState == McpHostState.Running;

    /// <summary>The MCP tab's honest stopped row (the default state until the server runs).</summary>
    public bool IsMcpStopped => _mcpHostState == McpHostState.Stopped;

    /// <summary>The MCP tab's honest failed row: the apply did not reach the server, the stored choice applies next launch.</summary>
    public bool IsMcpFailed => _mcpHostState == McpHostState.Failed;

    /// <summary>
    /// The exchange-credentials section's per-venue rows (T-227): each row
    /// owns its pair's masked drafts and its immediate store commits; the
    /// list is fixed at construction (the adapters' key names are constants)
    /// and only the rows' status chips refresh on every window open.
    /// </summary>
    public IReadOnlyList<CredentialSlot> CredentialSlots { get; }

    /// <summary>
    /// The external-servers block's draft rows: the editor surface for the
    /// terminal's external MCP client entries (T-068). The rows live only in
    /// memory — the save maps them into the persisted list and replays the
    /// manager's apply; a window open re-seeds them from the settings.
    /// </summary>
    public ObservableCollection<McpServerRow> McpServers { get; } = [];

    private PairRow? _selectedAllPair;

    /// <summary>The row picked in the all-pairs list (the hide command's
    /// input); null while nothing is picked — a fresh reset clears both
    /// picks.</summary>
    public PairRow? SelectedAllPair
    {
        get => _selectedAllPair;
        set => this.RaiseAndSetIfChanged(ref _selectedAllPair, value);
    }

    private string? _selectedHiddenPair;

    /// <summary>The pair picked in the hidden list (the reveal command's input);
    /// null while nothing is picked — a fresh reset clears both picks.</summary>
    public string? SelectedHiddenPair
    {
        get => _selectedHiddenPair;
        set => this.RaiseAndSetIfChanged(ref _selectedHiddenPair, value);
    }

    /// <summary>
    /// The workspace exchange's all pairs as selectable rows, starred pairs
    /// pinned to the head (the exchange's own order inside each group; the
    /// hidden names are not here — they form the other list).
    /// </summary>
    public ObservableCollection<PairRow> AllPairs => _allPairs;

    /// <summary>The workspace exchange's hidden pairs: the listed names in the
    /// exchange's order, then the stale ones (still hidden yet no longer
    /// listed) in their normalized order.</summary>
    public ObservableCollection<string> HiddenPairs => _hiddenPairs;

    /// <summary>Available themes from ThemeManager (no duplication).</summary>
    public IReadOnlyList<ThemeInfo> AvailableThemes => _themeManager.AvailableThemes;

    /// <summary>Available languages from LocalizationManager (no duplication).</summary>
    public IReadOnlyList<LanguageInfo> AvailableLanguages => _localization.AvailableLanguages;

    /// <summary>Theme files that failed to load, with the file name shown per entry.</summary>
    public IReadOnlyList<ThemeFileLoadError> ThemeFileErrors
    {
        get => _themeFileErrors;
        private set
        {
            if (ReferenceEquals(_themeFileErrors, value))
            {
                return;
            }

            _themeFileErrors = value;
            this.RaisePropertyChanged(nameof(ThemeFileErrors));
            this.RaisePropertyChanged(nameof(HasThemeFileErrors));
        }
    }

    /// <summary>True while at least one theme file failed to load; drives the warning list visibility.</summary>
    public bool HasThemeFileErrors => _themeFileErrors.Count > 0;

    /// <summary>Whether the settings port surface (T-100) is wired at all: the DI composition always passes it; the tests may omit it.</summary>
    public bool IsPortAvailable => _settingsPort is not null && _confirmations is not null;

    /// <summary>The dry-run diff preview rows of the last validated import (added + changed entries only).</summary>
    public ObservableCollection<PortImportRow> ImportPreview { get; } = [];

    /// <summary>Whether the import preview is on screen (a validated plan is pending the user's apply).</summary>
    public bool IsImportPreviewVisible
    {
        get => _isImportPreviewVisible;
        set => this.RaiseAndSetIfChanged(ref _isImportPreviewVisible, value);
    }

    /// <summary>The import's rejection line (localized at event time); null while the last draft was accepted.</summary>
    public string? ImportErrorText
    {
        get => _importErrorText;
        private set
        {
            if (string.Equals(_importErrorText, value, StringComparison.Ordinal))
            {
                return;
            }

            _importErrorText = value;
            this.RaisePropertyChanged(nameof(ImportErrorText));
            this.RaisePropertyChanged(nameof(HasImportError));
        }
    }

    /// <summary>True while the last import draft was rejected; drives the error row's visibility.</summary>
    public bool HasImportError => _importErrorText is not null;

    /// <summary>The port's outcome line (export, apply, reset); null while nothing happened this open.</summary>
    public string? PortStatusText
    {
        get => _portStatusText;
        private set
        {
            if (string.Equals(_portStatusText, value, StringComparison.Ordinal))
            {
                return;
            }

            _portStatusText = value;
            this.RaisePropertyChanged(nameof(PortStatusText));
            this.RaisePropertyChanged(nameof(HasPortStatusText));
        }
    }

    /// <summary>True while a port outcome line shows; drives the status row's visibility.</summary>
    public bool HasPortStatusText => _portStatusText is not null;

    /// <summary>Whether the profile backup surface (T-215) is wired at all: the DI composition always passes it; the tests may omit it.</summary>
    public bool IsBackupAvailable => _profileBackup is not null && _confirmations is not null;

    /// <summary>The pre-localized restore preview rows of the last validated archive draft (one per restorable entry).</summary>
    public ObservableCollection<BackupPreviewRow> RestorePreview { get; } = [];

    /// <summary>Whether the restore preview is on screen (a validated plan is pending the user's apply).</summary>
    public bool IsRestorePreviewVisible
    {
        get => _isRestorePreviewVisible;
        set => this.RaiseAndSetIfChanged(ref _isRestorePreviewVisible, value);
    }

    /// <summary>The restore's rejection line (localized at event time); null while the last draft was accepted.</summary>
    public string? RestoreErrorText
    {
        get => _restoreErrorText;
        private set
        {
            if (string.Equals(_restoreErrorText, value, StringComparison.Ordinal))
            {
                return;
            }

            _restoreErrorText = value;
            this.RaisePropertyChanged(nameof(RestoreErrorText));
            this.RaisePropertyChanged(nameof(HasRestoreError));
        }
    }

    /// <summary>True while the last restore draft was rejected; drives the error row's visibility.</summary>
    public bool HasRestoreError => _restoreErrorText is not null;

    /// <summary>The backup's outcome line (backup done, restore done, declined); null while nothing happened this open.</summary>
    public string? BackupStatusText
    {
        get => _backupStatusText;
        private set
        {
            if (string.Equals(_backupStatusText, value, StringComparison.Ordinal))
            {
                return;
            }

            _backupStatusText = value;
            this.RaisePropertyChanged(nameof(BackupStatusText));
            this.RaisePropertyChanged(nameof(HasBackupStatusText));
        }
    }

    /// <summary>True while a backup outcome line shows; drives the status row's visibility.</summary>
    public bool HasBackupStatusText => _backupStatusText is not null;

    #endregion Properties

    #region Commands

    /// <summary>Command to open the user themes folder in the OS file manager.</summary>
    public ReactiveCommand<RxVoid, RxVoid> OpenThemesFolderCommand { get; }

    /// <summary>
    /// Hide the pair picked in the all-pairs list: the pair moves into the
    /// hidden draft (the lists repaint at once; only Save applies and
    /// persists the map). A starred pick never hides — the canExecute gate
    /// refuses it until the star is removed.
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid> HidePairCommand { get; }

    /// <summary>
    /// Reveal the pair picked in the hidden list: the pair leaves the hidden
    /// draft (the lists repaint at once; only Save applies and persists the
    /// map).
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid> RevealPairCommand { get; }

    /// <summary>
    /// Star or unstar the given all-pairs row: the favorites draft updates,
    /// the lists repaint (a starred pair pins to the head) and only Save
    /// applies and persists the map — the same draft contract the moves
    /// follow.
    /// </summary>
    public ReactiveCommand<PairRow, RxVoid> TogglePairFavoriteCommand { get; }

    /// <summary>
    /// Add a fresh external-server draft row: the empty draft stays local
    /// until Save (a blank-named row simply drops out of the persisted list
    /// with a logged warning).
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid> AddMcpServerCommand { get; }

    /// <summary>Remove one external-server draft row: local until Save (the persisted list and its connections apply on Save only).</summary>
    public ReactiveCommand<McpServerRow, RxVoid> RemoveMcpServerCommand { get; }

    /// <summary>
    /// Apply the pending (already-validated) import plan: enabled while the
    /// preview is on screen; the apply is the one locked batch and every
    /// subscriber re-applies live (the T-053 canon).
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid> ApplyImportCommand { get; }

    /// <summary>
    /// Apply the pending (already-validated) restore plan: enabled while the
    /// preview is on screen; the apply runs behind the typed human
    /// confirmation the backup service itself requests (the destructive
    /// batch replaces the live profile files).
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid> ApplyRestoreCommand { get; }

    #endregion Commands

    #region Private Fields

    private readonly ISettingsService _settings;
    private readonly IThemeManager _themeManager;
    private readonly ILocalizationManager _localization;
    private readonly IShellUtils _shellUtils;
    private readonly ActiveExchangeService _activeExchanges;
    private readonly SymbolListProvider _symbolList;
    private readonly ILogger<SettingsViewModel> _logger;

    /// <summary>
    /// The scanned indicator catalog (shared with the chart pipeline): its
    /// loaded-name set decides whether the overlay rows render at all.
    /// </summary>
    private readonly IIndicatorCatalog _indicatorCatalog;

    /// <summary>
    /// The persisted hidden-pairs map (all exchanges) as last read: the
    /// pairs tab's drafts seed from it and the save rewrites it with the
    /// current exchange's entry replaced (the other exchanges' entries
    /// survive untouched).
    /// </summary>
    private IReadOnlyDictionary<string, IReadOnlySet<string>> _persistedHiddenMarks =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

    /// <summary>
    /// The persisted favorites map (all exchanges) as last read: the same
    /// seed-and-save contract as the hidden map (the star drafts seed from
    /// it, the save rewrites the draft exchange's entry).
    /// </summary>
    private IReadOnlyDictionary<string, IReadOnlySet<string>> _persistedFavoriteMarks =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

    /// <summary>
    /// The exchange the pair lists and drafts were built for, captured at
    /// every reset (the modal window cannot race an exchange switch). Empty
    /// until the first reset — the save never writes a draft with no
    /// exchange context (nothing would be behind it).
    /// </summary>
    private string _draftExchangeId = string.Empty;

    /// <summary>The hidden-names draft: re-seeded from the persisted map at
    /// every reset, mutated by the hide and reveal commands, consumed by the
    /// save's diff.</summary>
    private HashSet<string> _draftHiddenNames = new(StringComparer.Ordinal);

    /// <summary>The favorites draft: re-seeded from the persisted map at
    /// every reset, mutated by the row star buttons, consumed by the save's
    /// diff.</summary>
    private HashSet<string> _draftFavoriteNames = new(StringComparer.Ordinal);

    /// <summary>The workspace exchange's listing (normalized names, exchange
    /// order): the lists repaint from it and the draft edits.</summary>
    private IReadOnlyList<string> _listingNames = [];

    /// <summary>The all-pairs list backing (rebuilt wholesale at every reset
    /// and draft move; the identity never changes for the binding).</summary>
    private readonly ObservableCollection<PairRow> _allPairs = new();

    /// <summary>The hidden list backing (the same rebuild contract).</summary>
    private readonly ObservableCollection<string> _hiddenPairs = new();

    /// <summary>
    /// The persisted indicator config as last read (seed + changes stream):
    /// the SMA row's own cache derives from it and the save rewrites the
    /// whole row array (future rows survive a save untouched).
    /// </summary>
    private string _persistedIndicatorConfig = UiSettingsKeys.DefaultChartIndicators;

    /// <summary>The cached SMA row pieces (the sentinel period means "no row cached").</summary>
    private bool _persistedSmaEnabled;
    private int _persistedSmaPeriod = NoSmaPeriod;

    /// <summary>The cached pack row pieces (T-092): per-name enable + period, the sentinel period means "no row cached".</summary>
    private bool _persistedEmaEnabled;
    private int _persistedEmaPeriod = NoRowPeriod;
    private bool _persistedWmaEnabled;
    private int _persistedWmaPeriod = NoRowPeriod;
    private bool _persistedRsiEnabled;
    private int _persistedRsiPeriod = NoRowPeriod;
    private bool _persistedMacdEnabled;
    private int _persistedMacdPeriod = NoRowPeriod;
    private bool _persistedAtrEnabled;
    private int _persistedAtrPeriod = NoRowPeriod;
    private bool _persistedBollEnabled;
    private int _persistedBollPeriod = NoRowPeriod;

    /// <summary>
    /// The persisted MCP server enable choice as last read: the checkbox
    /// drafts seed from it and the save persists only a real difference
    /// (the same seed + changes pattern as the clock choice).
    /// </summary>
    private bool _persistedMcpServerEnabled = UiSettingsKeys.DefaultMcpServerEnabled;

    /// <summary>The unsaved MCP enable checkbox (the draft the save applies).</summary>
    private bool _isMcpServerEnabled;

    /// <summary>
    /// The persisted update-check choice as last read (T-213): the
    /// checkbox drafts seed from it and the save persists only a real
    /// difference (the same seed + changes pattern as the MCP choice).
    /// </summary>
    private bool _persistedUpdateCheckEnabled = AppSettingsKeys.DefaultUpdateCheckEnabled;

    /// <summary>The unsaved update-check checkbox (the draft the save applies).</summary>
    private bool _isUpdateCheckEnabled;

    /// <summary>
    /// The persisted auto-update choice as last read (T-258): the checkbox
    /// drafts seed from it and the save persists only a real difference
    /// (the same seed + changes pattern as the update-check choice).
    /// </summary>
    private bool _persistedAutoUpdateEnabled = AppSettingsKeys.DefaultAutoUpdateEnabled;

    /// <summary>The unsaved auto-update checkbox (the draft the save applies).</summary>
    private bool _isAutoUpdateEnabled;

    /// <summary>
    /// The persisted update-channel choice as last read (T-258, the
    /// canonical stored spelling): the combo drafts index from it on every
    /// open and the save persists only a real difference (the same shape
    /// as the AI provider pick — the stored side carries the name, the
    /// draft carries the index).
    /// </summary>
    private string _persistedUpdateChannel = AppSettingsKeys.DefaultUpdateChannel;

    /// <summary>The unsaved update-channel combo index (the draft the save applies).</summary>
    private int _selectedUpdateChannelIndex;

    /// <summary>
    /// The MCP host's lifecycle state as the runner surfaces it: the three
    /// honest rows derive from it and every transition raises the bools.
    /// </summary>
    private McpHostState _mcpHostState = McpHostState.Stopped;

    /// <summary>The host runner (start/stop live on save, state surfacing).</summary>
    private readonly McpHostService _mcpHosts;

    /// <summary>
    /// The external MCP client connections' owner (T-068): the save applies
    /// the editor's list live through its reconcile and the state snapshots
    /// feed the per-entry rows.
    /// </summary>
    private readonly IMcpClientManager _mcpClients;

    /// <summary>
    /// The persisted external-server list as last read: the drafts seed from
    /// it and the save compares against it, so an unchanged save stays
    /// silent (the same no-op discipline every other row keeps).
    /// </summary>
    private string _persistedServerJson = McpClientManager.DefaultServersJson;

    #region AI provider fields (T-207)

    /// <summary>The combo index each pick maps to (the OpenAI-compatible stack rides 1).</summary>
    private const int OllamaProviderIndex = 0;
    private const int OpenAiProviderIndex = 1;

    /// <summary>The update channel combo's stable slot (T-258): the shipped default pick.</summary>
    private const int StableChannelIndex = 0;

    /// <summary>The update channel combo's beta slot (T-258): the pre-releases-included pick.</summary>
    private const int BetaChannelIndex = 1;

    /// <summary>
    /// The persisted AI chat provider pick as last read (seed + changes
    /// stream): the combo drafts from it and the save persists only a real
    /// difference (the same seed + changes pattern as the MCP enable).
    /// </summary>
    private string _persistedAiProvider = AiSettingsKeys.DefaultProvider;

    /// <summary>The persisted Ollama pieces as last read (the drafts seed from them).</summary>
    private string _persistedOllamaUrl = AiSettingsKeys.DefaultOllamaUrl;
    private string _persistedOllamaModel = AiSettingsKeys.DefaultModel;

    /// <summary>The persisted OpenAI-compatible pieces as last read. The model
    /// has NO default: absence and blank are the same honest empty.</summary>
    private string _persistedOpenAiBaseUrl = AiSettingsKeys.DefaultOpenAiCompatibleBaseUrl;
    private string _persistedOpenAiModel = string.Empty;

    /// <summary>The unsaved provider pick (the combo index the save applies).</summary>
    private int _aiProviderIndex;

    /// <summary>The unsaved text drafts (each applies on Save).</summary>
    private string _ollamaUrlDraft = string.Empty;
    private string _ollamaModelDraft = string.Empty;
    private string _openAiBaseUrlDraft = string.Empty;
    private string _openAiModelDraft = string.Empty;

    /// <summary>The API key as typed (commits immediately — see the property).</summary>
    private string _openAiApiKeyDraft = string.Empty;

    /// <summary>
    /// The key-commit guard: set while the VM seeds the key draft itself
    /// (construction and every window-open re-seed), so the property's
    /// re-seed emissions never commit — only the user's typing does. The
    /// guard is checked at emission time (the stream filters on it), so a
    /// re-seed can never queue a deleting commit behind it.
    /// </summary>
    private bool _aiKeyCommitSuppressed;

    /// <summary>
    /// The secret store the OpenAI-compatible API key commits to (T-046
    /// canon: secrets only through the store). Null in tests without the
    /// surface — the key draft then stays memory-only.
    /// </summary>
    private readonly ISecretStore? _secrets;

    #endregion AI provider fields (T-207)

    /// <summary>The settings port service (T-100): export, import, factory reset; null in tests without the surface.</summary>
    private readonly SettingsPortService? _settingsPort;

    /// <summary>The typed confirmation the factory reset gates itself behind; null in the same tests.</summary>
    private readonly IConfirmationService? _confirmations;

    /// <summary>The validated import plan pending the user's apply (null until a draft is accepted).</summary>
    private SettingsPortImportPlan? _pendingImportPlan;

    /// <summary>The import preview visibility (the apply command rides it).</summary>
    private bool _isImportPreviewVisible;

    /// <summary>The import's rejection line as last composed.</summary>
    private string? _importErrorText;

    /// <summary>The port's outcome line as last composed.</summary>
    private string? _portStatusText;

    /// <summary>The profile backup service (T-215): archive the profile, restore it back; null in tests without the surface.</summary>
    private readonly ProfileBackupService? _profileBackup;

    /// <summary>The validated restore plan pending the user's apply (null until a draft is accepted).</summary>
    private ProfileBackupRestorePlan? _pendingRestorePlan;

    /// <summary>The restore preview visibility (the apply command rides it).</summary>
    private bool _isRestorePreviewVisible;

    /// <summary>The restore's rejection line as last composed.</summary>
    private string? _restoreErrorText;

    /// <summary>The backup's outcome line as last composed.</summary>
    private string? _backupStatusText;

    #endregion Private Fields

    #region Constructor

    /// <summary>
    /// Create a new SettingsViewModel instance. The managers must already be
    /// initialized (app startup applies the stored preferences before the
    /// settings window can open), so their current state defines the selection.
    /// </summary>
    /// <param name="settings">Stored UI preferences (applied on Save).</param>
    /// <param name="themeManager">Theme manager providing applied/available themes.</param>
    /// <param name="localization">Localization manager providing applied/available languages.</param>
    /// <param name="shellUtils">File manager launcher for the themes folder button.</param>
    /// <param name="mcpHosts">The MCP host runner: the save applies the enable choice live and the state stream renders the honest rows.</param>
    /// <param name="indicatorCatalog">The scanned plugin catalog: the indicators tab renders its rows only for plugins the set actually loaded.</param>
    /// <param name="mcpClients">
    /// The external MCP client connections' owner: the save applies the
    /// external-server list live and the state snapshots render the per-entry
    /// connection rows honestly.
    /// </param>
    /// <param name="logger">Injected application logger.</param>
    /// <param name="themeLoadErrors">
    /// Stream of theme file load errors; when omitted (tests without the
    /// diagnostics stream), the warning list simply stays empty.
    /// </param>
    /// <param name="settingsPort">
    /// The settings port service (T-100): export, import and factory reset.
    /// When omitted (tests without the port surface), the port commands stay
    /// inert — <see cref="IsPortAvailable"/> renders the tab's honest posture.
    /// </param>
    /// <param name="confirmations">
    /// The typed confirmation service the factory reset gates itself behind.
    /// Paired with <paramref name="settingsPort"/>; omitted together with it.
    /// </param>
    /// <param name="secrets">
    /// The secret store the OpenAI-compatible API key commits to immediately
    /// (T-207): the key's only home — it never rides the settings file, the
    /// logs or the port export. When omitted (tests without the secret
    /// surface), the key draft stays memory-only and commits nothing.
    /// </param>
    public SettingsViewModel(
        ISettingsService settings,
        IThemeManager themeManager,
        ILocalizationManager localization,
        IShellUtils shellUtils,
        McpHostService mcpHosts,
        ActiveExchangeService activeExchanges,
        SymbolListProvider symbolList,
        IIndicatorCatalog indicatorCatalog,
        IMcpClientManager mcpClients,
        ILogger<SettingsViewModel> logger,
        IObservable<IReadOnlyList<ThemeFileLoadError>>? themeLoadErrors = null,
        SettingsPortService? settingsPort = null,
        IConfirmationService? confirmations = null,
        ProfileBackupService? profileBackup = null,
        ISecretStore? secrets = null)
    {
        _settings = settings;
        _themeManager = themeManager;
        _localization = localization;
        _shellUtils = shellUtils;
        _mcpHosts = mcpHosts;
        _activeExchanges = activeExchanges;
        _symbolList = symbolList;
        _indicatorCatalog = indicatorCatalog;
        _mcpClients = mcpClients;
        _logger = logger;
        _settingsPort = settingsPort;
        _confirmations = confirmations;
        _profileBackup = profileBackup;
        _secrets = secrets;

        // The exchange-credentials rows (T-227): one per secret-key pair the
        // adapters read, the venue captions carrying the adapters' own proper
        // names (the T-127 English precedent). The store rides the slot (the
        // pair's only home); the presence seed lands through the async lane
        // beside the other seeded reads.
        CredentialSlots =
        [
            new("Alpaca", AlpacaAdapter.ApiKeySecretKey, AlpacaAdapter.ApiSecretSecretKey, _secrets, _localization, _logger),
            new("Bybit Testnet", BybitTradingAdapter.ApiKeySecretKey, BybitTradingAdapter.ApiSecretSecretKey, _secrets, _localization, _logger),
            new("Binance Testnet", BinanceTradingAdapter.ApiKeySecretKey, BinanceTradingAdapter.ApiSecretSecretKey, _secrets, _localization, _logger),
        ];
        Observable
            .FromAsync((CancellationToken ct) => RefreshCredentialSlotsAsync(ct))
            .Subscribe(
                _ => { },
                ex => _logger.LogError(ex, "The exchange-credentials presence seed failed"));

        // The plugin surface is settled at construction (the scan runs at
        // startup only): the SMA row renders only when the catalog holds
        // the plugin; a nameless (null) set reads as an empty one. A
        // catalog that registered other plugins but not SMA renders
        // neither the row nor the empty state — the two flags answer the
        // two independent questions of the tab's honesty contract.
        var loadedNames = indicatorCatalog.LoadedNames ?? Array.Empty<string>();
        IsSmaPluginLoaded = loadedNames.Contains(SmaIndicatorName, StringComparer.Ordinal);
        IsEmaPluginLoaded = loadedNames.Contains(EmaIndicatorName, StringComparer.Ordinal);
        IsWmaPluginLoaded = loadedNames.Contains(WmaIndicatorName, StringComparer.Ordinal);
        IsRsiPluginLoaded = loadedNames.Contains(RsiIndicatorName, StringComparer.Ordinal);
        IsMacdPluginLoaded = loadedNames.Contains(MacdIndicatorName, StringComparer.Ordinal);
        IsAtrPluginLoaded = loadedNames.Contains(AtrIndicatorName, StringComparer.Ordinal);
        IsBollPluginLoaded = loadedNames.Contains(BollIndicatorName, StringComparer.Ordinal);
        IsIndicatorCatalogEmpty = loadedNames.Count == 0;

        OpenThemesFolderCommand = ReactiveCommand.Create(HandleOpenThemesFolder);

        if (themeLoadErrors is not null)
        {
            // App-lifetime singleton: the subscription mirrors the ViewModel's
            // lifetime (same contract as the status bar chips). The manager
            // publishes on the UI thread when Avalonia runs, so a plain
            // subscription is UI-thread safe; in tests it publishes directly.
            themeLoadErrors.Subscribe(
                errors => ThemeFileErrors = errors,
                ex => _logger.LogError(ex, "Theme load error stream failed"));
        }

        // The clock format lives in the settings service (no manager owns its
        // applied state, unlike the theme and the language): one async read
        // seeds the cache the selections sync from, the changes stream keeps
        // it current, and both subscriptions mirror the app-lifetime
        // singleton. Non-bool values are dropped — the cached choice stays
        // in charge until a real change arrives.
        Observable.FromAsync(() => _settings.GetAsync<bool>(
                UiSettingsKeys.Section,
                UiSettingsKeys.ChartUse12HourClock,
                UiSettingsKeys.DefaultUse12HourClock))
            .Subscribe(
                use12HourClock => _persistedUse12HourClock = use12HourClock,
                ex => _logger.LogError(ex, "Failed to read the chart clock format"));

        // The changes stream keeps the cached choice current — non-bool
        // values are dropped, the cached choice stays in charge until a real
        // change arrives. Both subscriptions mirror the app-lifetime
        // singleton.
        _settings.Changes
            .Where(change => change.Section == UiSettingsKeys.Section &&
                             change.Key == UiSettingsKeys.ChartUse12HourClock)
            .Select(change => change.NewValue)
            .OfType<bool>()
            .Subscribe(
                use12HourClock => _persistedUse12HourClock = use12HourClock,
                ex => _logger.LogError(ex, "The chart clock format stream stopped unexpectedly"));

        // The indicator overlay config: the same seed + changes pattern as
        // the clock. The SMA row's cache derives from the whole array (the
        // first SMA row wins), the save rewrites the array keeping the
        // other rows.
        Observable.FromAsync(() => _settings.GetAsync<string>(
                UiSettingsKeys.Section,
                UiSettingsKeys.ChartIndicators,
                UiSettingsKeys.DefaultChartIndicators))
            .Subscribe(
                CacheIndicatorConfig,
                ex => _logger.LogError(ex, "Failed to read the chart indicator config"));

        _settings.Changes
            .Where(change => change.Section == UiSettingsKeys.Section &&
                             change.Key == UiSettingsKeys.ChartIndicators)
            .Select(change => change.NewValue)
            .OfType<string>()
            .Subscribe(
                CacheIndicatorConfig,
                ex => _logger.LogError(ex, "The chart indicator config stream stopped unexpectedly"));

        // The hidden-pairs map: the pairs tab reads it (the drafts seed from
        // it) and writes it on Save. The same seed + changes pattern as the
        // clock: one async read fills the cache, the stream keeps it current
        // and non-string payloads drop — the cached document stays in
        // charge until a real change arrives.
        Observable.FromAsync(() => _settings.GetAsync<string>(
                UiSettingsKeys.WorkspaceSection,
                UiSettingsKeys.WorkspaceHiddenSymbols))
            .Subscribe(
                CacheHiddenMarks,
                ex => _logger.LogError(ex, "Failed to read the hidden-pairs map"));

        _settings.Changes
            .Where(change => change.Section == UiSettingsKeys.WorkspaceSection &&
                             change.Key == UiSettingsKeys.WorkspaceHiddenSymbols)
            .Select(change => change.NewValue)
            .OfType<string>()
            .Subscribe(
                CacheHiddenMarks,
                ex => _logger.LogError(ex, "The hidden-pairs map stream stopped unexpectedly"));

        // The favorites map: the pairs tab reads it (the star drafts seed
        // from it) and writes it on Save. The same seed + changes pattern as
        // the hidden map.
        Observable.FromAsync(() => _settings.GetAsync<string>(
                UiSettingsKeys.WorkspaceSection,
                UiSettingsKeys.WorkspaceFavoriteSymbols))
            .Subscribe(
                CacheFavoriteMarks,
                ex => _logger.LogError(ex, "Failed to read the favorites map"));

        _settings.Changes
            .Where(change => change.Section == UiSettingsKeys.WorkspaceSection &&
                             change.Key == UiSettingsKeys.WorkspaceFavoriteSymbols)
            .Select(change => change.NewValue)
            .OfType<string>()
            .Subscribe(
                CacheFavoriteMarks,
                ex => _logger.LogError(ex, "The favorites map stream stopped unexpectedly"));

        // The MCP server: the same seed + changes pattern as the clock; the
        // enable choice drafts the checkbox and the save applies it live.
        Observable.FromAsync(() => _settings.GetAsync<bool>(
                UiSettingsKeys.McpSection,
                UiSettingsKeys.McpServerEnabled,
                UiSettingsKeys.DefaultMcpServerEnabled))
            .Subscribe(
                enabled => _persistedMcpServerEnabled = enabled,
                ex => _logger.LogError(ex, "Failed to read the MCP server enable state"));

        _settings.Changes
            .Where(change => change.Section == UiSettingsKeys.McpSection &&
                             change.Key == UiSettingsKeys.McpServerEnabled)
            .Select(change => change.NewValue)
            .OfType<bool>()
            .Subscribe(
                enabled => _persistedMcpServerEnabled = enabled,
                ex => _logger.LogError(ex, "The MCP server enable stream stopped unexpectedly"));

        // The update check (T-213): the same seed + changes pattern as the
        // MCP enable; the choice drafts the checkbox and the save applies
        // it (the checker's own change subscription picks the flip live).
        Observable.FromAsync(() => _settings.GetAsync<bool>(
                AppSettingsKeys.Section,
                AppSettingsKeys.UpdateCheck,
                AppSettingsKeys.DefaultUpdateCheckEnabled))
            .Subscribe(
                enabled => _persistedUpdateCheckEnabled = enabled,
                ex => _logger.LogError(ex, "Failed to read the update check enable state"));

        _settings.Changes
            .Where(change => change.Section == AppSettingsKeys.Section &&
                             change.Key == AppSettingsKeys.UpdateCheck)
            .Select(change => change.NewValue)
            .OfType<bool>()
            .Subscribe(
                enabled => _persistedUpdateCheckEnabled = enabled,
                ex => _logger.LogError(ex, "The update check enable stream stopped unexpectedly"));

        // The auto-update loop (T-258): the same seed + changes pattern as
        // the update check; the choice drafts the checkbox and the save
        // applies it (the staging service's own change subscription picks
        // the flip live).
        Observable.FromAsync(() => _settings.GetAsync<bool>(
                AppSettingsKeys.Section,
                AppSettingsKeys.AutoUpdate,
                AppSettingsKeys.DefaultAutoUpdateEnabled))
            .Subscribe(
                enabled => _persistedAutoUpdateEnabled = enabled,
                ex => _logger.LogError(ex, "Failed to read the auto-update enable state"));

        _settings.Changes
            .Where(change => change.Section == AppSettingsKeys.Section &&
                             change.Key == AppSettingsKeys.AutoUpdate)
            .Select(change => change.NewValue)
            .OfType<bool>()
            .Subscribe(
                enabled => _persistedAutoUpdateEnabled = enabled,
                ex => _logger.LogError(ex, "The auto-update enable stream stopped unexpectedly"));

        // The update channel (T-258): the same pattern over the stored
        // channel spelling; the combo drafts from it on every open (a
        // garbage stored spelling drafts the stable default — the
        // checker's own seed read already logged the repair).
        Observable.FromAsync(() => _settings.GetAsync<string>(
                AppSettingsKeys.Section,
                AppSettingsKeys.UpdateChannel,
                AppSettingsKeys.DefaultUpdateChannel))
            .Subscribe(
                channel => _persistedUpdateChannel = channel,
                ex => _logger.LogError(ex, "Failed to read the update channel"));

        _settings.Changes
            .Where(change => change.Section == AppSettingsKeys.Section &&
                             change.Key == AppSettingsKeys.UpdateChannel)
            .Select(change => change.NewValue)
            .OfType<string>()
            .Subscribe(
                channel => _persistedUpdateChannel = channel,
                ex => _logger.LogError(ex, "The update channel stream stopped unexpectedly"));

        // The host's honest state (running / stopped / failed): the three
        // rows derive and rise on every transition; both subscriptions
        // mirror the app-lifetime singleton.
        _mcpHosts.State
            .Subscribe(
                state =>
                {
                    _mcpHostState = state;
                    this.RaisePropertyChanged(nameof(IsMcpRunning));
                    this.RaisePropertyChanged(nameof(IsMcpStopped));
                    this.RaisePropertyChanged(nameof(IsMcpFailed));
                },
                ex => _logger.LogError(ex, "The MCP host state stream stopped unexpectedly"));

        // The AI chat provider (T-207): the same seed + changes pattern as
        // the clock. The reads carry the shipped defaults, so a fresh store
        // caches the defaults (an untouched save never fabricates a write)
        // and the drafts re-seed from the caches on every open.
        Observable.FromAsync(() => _settings.GetAsync<string>(
                AiSettingsKeys.Section,
                AiSettingsKeys.Provider,
                AiSettingsKeys.DefaultProvider))
            .Subscribe(
                provider => _persistedAiProvider = provider,
                ex => _logger.LogError(ex, "Failed to read the AI chat provider"));

        _settings.Changes
            .Where(change => change.Section == AiSettingsKeys.Section &&
                             change.Key == AiSettingsKeys.Provider)
            .Select(change => change.NewValue)
            .OfType<string>()
            .Subscribe(
                provider => _persistedAiProvider = provider,
                ex => _logger.LogError(ex, "The AI chat provider stream stopped unexpectedly"));

        Observable.FromAsync(() => _settings.GetAsync<string>(
                AiSettingsKeys.Section,
                AiSettingsKeys.OllamaUrl,
                AiSettingsKeys.DefaultOllamaUrl))
            .Subscribe(
                url => _persistedOllamaUrl = url,
                ex => _logger.LogError(ex, "Failed to read the Ollama base URL"));

        _settings.Changes
            .Where(change => change.Section == AiSettingsKeys.Section &&
                             change.Key == AiSettingsKeys.OllamaUrl)
            .Select(change => change.NewValue)
            .OfType<string>()
            .Subscribe(
                url => _persistedOllamaUrl = url,
                ex => _logger.LogError(ex, "The Ollama base URL stream stopped unexpectedly"));

        Observable.FromAsync(() => _settings.GetAsync<string>(
                AiSettingsKeys.Section,
                AiSettingsKeys.Model,
                AiSettingsKeys.DefaultModel))
            .Subscribe(
                model => _persistedOllamaModel = model,
                ex => _logger.LogError(ex, "Failed to read the Ollama model"));

        _settings.Changes
            .Where(change => change.Section == AiSettingsKeys.Section &&
                             change.Key == AiSettingsKeys.Model)
            .Select(change => change.NewValue)
            .OfType<string>()
            .Subscribe(
                model => _persistedOllamaModel = model,
                ex => _logger.LogError(ex, "The Ollama model stream stopped unexpectedly"));

        Observable.FromAsync(() => _settings.GetAsync<string>(
                AiSettingsKeys.OpenAiCompatibleSection,
                AiSettingsKeys.OpenAiCompatibleBaseUrl,
                AiSettingsKeys.DefaultOpenAiCompatibleBaseUrl))
            .Subscribe(
                url => _persistedOpenAiBaseUrl = url,
                ex => _logger.LogError(ex, "Failed to read the OpenAI-compatible base URL"));

        _settings.Changes
            .Where(change => change.Section == AiSettingsKeys.OpenAiCompatibleSection &&
                             change.Key == AiSettingsKeys.OpenAiCompatibleBaseUrl)
            .Select(change => change.NewValue)
            .OfType<string>()
            .Subscribe(
                url => _persistedOpenAiBaseUrl = url,
                ex => _logger.LogError(ex, "The OpenAI-compatible base URL stream stopped unexpectedly"));

        Observable.FromAsync(() => _settings.GetAsync<string>(
                AiSettingsKeys.OpenAiCompatibleSection,
                AiSettingsKeys.OpenAiCompatibleModel,
                string.Empty))
            .Subscribe(
                model => _persistedOpenAiModel = model,
                ex => _logger.LogError(ex, "Failed to read the OpenAI-compatible model"));

        _settings.Changes
            .Where(change => change.Section == AiSettingsKeys.OpenAiCompatibleSection &&
                             change.Key == AiSettingsKeys.OpenAiCompatibleModel)
            .Select(change => change.NewValue)
            .OfType<string>()
            .Subscribe(
                model => _persistedOpenAiModel = model,
                ex => _logger.LogError(ex, "The OpenAI-compatible model stream stopped unexpectedly"));

        // The OpenAI-compatible API key commits immediately (per keystroke):
        // the secret store is the key's only home, so there is no save step
        // to hold it. The guard keeps the VM's own re-seeds out of the
        // pipeline: the construction and the window-open resets clear the
        // box without ever committing (a re-seed must never queue a
        // deleting commit behind it — the guard is checked at emission
        // time). A store-less construction commits nothing (memory-only).
        _aiKeyCommitSuppressed = true;
        this
            .WhenAnyValue(vm => vm.OpenAiApiKeyDraft)
            .Where(_ => !_aiKeyCommitSuppressed)
            .SubscribeAsync(
                _ => CommitOpenAiApiKeyAsync(),
                ex => _logger.LogError(ex, "Failed to store the OpenAI-compatible API key"));
        _aiKeyCommitSuppressed = false;

        // The pairs tab's commands act on a pick: no pick, no move. A hide
        // move needs an unstarred pick (a starred pair never hides — that is
        // the point of the star) and more than one visible pair — the
        // workspace keeps at least one tradable context (the '>' button
        // grays out on a starred pick and on the last row). The lists
        // repaint through CollectionChanged events, so the count rides the
        // same observable.
        var allPairCount = Observable
            .FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                handler => _allPairs.CollectionChanged += handler,
                handler => _allPairs.CollectionChanged -= handler)
            .Select(_ => _allPairs.Count)
            .StartWith(_allPairs.Count);
        var hasHideablePick = this
            .WhenAnyValue(x => x.SelectedAllPair)
            .Select(static pick => pick is not null && !pick.IsFavorite);
        var hasHiddenPairPick = this.WhenAnyValue(x => x.SelectedHiddenPair).Select(static name => name is not null);
        HidePairCommand = ReactiveCommand.Create(
            HideSelectedPair,
            hasHideablePick.CombineLatest(allPairCount, static (hideable, count) => hideable && count > 1));
        RevealPairCommand = ReactiveCommand.Create(RevealSelectedPair, hasHiddenPairPick);
        TogglePairFavoriteCommand = ReactiveCommand.Create<PairRow>(TogglePairFavorite);

        AddMcpServerCommand = ReactiveCommand.Create(AddServerDraftRow);
        RemoveMcpServerCommand = ReactiveCommand.Create<McpServerRow>(row => McpServers.Remove(row));

        // The external servers' honest per-entry states: the manager's
        // snapshots repaint the matching draft rows by name (the name is the
        // key both sides share). The subscription mirrors the app-lifetime
        // singleton, the same contract the theme errors carry.
        _mcpClients.StatesChanged.Subscribe(
            ApplyMcpClientStates,
            ex => _logger.LogError(ex, "The MCP client state stream stopped unexpectedly"));

        // The settings port's apply gate (T-100): the validated preview's own
        // confirmation control — it enables only while a plan is pending, and
        // the apply is the one locked batch every subscriber re-applies from.
        ApplyImportCommand = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(
            (_, cancellationToken) => ApplyImportCoreAsync(cancellationToken),
            this.WhenAnyValue(vm => vm.IsImportPreviewVisible));

        // The profile restore's apply gate (T-215): the same preview-then-apply
        // control as the settings import — it enables only while a validated
        // plan is pending, and the apply runs behind the typed human
        // confirmation the backup service itself requests.
        ApplyRestoreCommand = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(
            (_, cancellationToken) => ApplyRestoreCoreAsync(cancellationToken),
            this.WhenAnyValue(vm => vm.IsRestorePreviewVisible));

        ResetAppearanceSelections();
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>
    /// Sync the selections with the currently applied theme and language,
    /// then repaint the pairs tab for the current exchange. The ViewModel is
    /// an app-lifetime singleton, so the window refreshes the selections on
    /// every open; otherwise an abandoned (unsaved) selection would show up
    /// as already-applied state on the next open.
    /// </summary>
    public async Task ResetSelectionsAsync(CancellationToken ct = default)
    {
        ResetAppearanceSelections();
        await RefreshSymbolPairsAsync(ct);
        await RefreshMcpClientRowsAsync(ct);
        await RefreshCredentialSlotsAsync(ct);
    }

    /// <summary>
    /// Read every credentials row' status from the store (the pair's
    /// presence, never its values — the secret never echoes): the window
    /// open and the post-save refreshes both ride this one path.
    /// </summary>
    private async Task RefreshCredentialSlotsAsync(CancellationToken ct)
    {
        foreach (var slot in CredentialSlots)
        {
            await slot.RefreshAsync(ct);
        }
    }

    /// <summary>
    /// Apply and persist the selected theme and language. A selection equal to
    /// the applied state is skipped (no redundant events, no settings rewrite).
    /// </summary>
    public async Task SaveAsync()
    {
        var theme = PickAt(_themeManager.AvailableThemes, _selectedThemeIndex);
        if (theme is not null && !IsThemeApplied(theme))
        {
            await _themeManager.ApplyThemeAsync(theme.Name);
            await _settings.SetAsync(
                UiSettingsKeys.Section,
                UiSettingsKeys.Theme,
                theme.Name);
        }

        var language = PickAt(_localization.AvailableLanguages, _selectedLanguageIndex);
        if (language is not null && !IsLanguageApplied(language))
        {
            await _localization.SetLanguageAsync(language.Code);
            await _settings.SetAsync(
                UiSettingsKeys.Section,
                UiSettingsKeys.Language,
                language.Code);
        }

        // The chart clock format: only a real difference applies (the same
        // no-op discipline the theme and language choices follow).
        if (_selectedTimeFormatIndex != NoSelection)
        {
            var use12HourClock = _selectedTimeFormatIndex == Hours12Index;
            if (use12HourClock != _persistedUse12HourClock)
            {
                await _settings.SetAsync(
                    UiSettingsKeys.Section,
                    UiSettingsKeys.ChartUse12HourClock,
                    use12HourClock);
                _persistedUse12HourClock = use12HourClock;
            }
        }

        // The indicator overlay row: the period must read as an integer for
        // the row to save at all (an unparsable text skips the row with a
        // logged warning - nothing defaults, the cached config stays in
        // charge). A real difference rewrites the row array: the SMA row is
        // replaced, every other (future) row rides untouched. The block only
        // runs when the catalog actually loaded the plugin: the save never
        // fabricates a config row for a plugin the session does not have
        // (the persisted config rides untouched otherwise — the chart
        // pipeline skips unknown names on its own seam).
        if (IsSmaPluginLoaded)
        {
            if (TryReadSmaSelection(out var smaEnabled, out var smaPeriod))
            {
                if (smaEnabled != _persistedSmaEnabled || smaPeriod != _persistedSmaPeriod)
                {
                    var (configs, _) = ChartIndicatorConfig.ParseList(_persistedIndicatorConfig);
                    var updated = configs
                        .Where(config => !string.Equals(config.Name, SmaIndicatorName, StringComparison.Ordinal))
                        .ToList();
                    updated.Add(new ChartIndicatorConfig(SmaIndicatorName, smaEnabled, smaPeriod));

                    var written = ChartIndicatorConfig.Write(updated);
                    await _settings.SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartIndicators, written);
                    CacheIndicatorConfig(written);
                }
            }
            else
            {
                _logger.LogWarning(
                    "The SMA period '{PeriodText}' is not a readable integer; the indicator selection is not saved",
                    SmaPeriodText);
            }
        }

        // The standard pack rows (T-092): the same loaded-plugins gate the
        // SMA row rides, one shared row-save helper. The non-short-circuit
        // OR is deliberate: every row runs (each decides its own silence),
        // the write lands once for the whole tab (one settings key).
        var persistedIndicatorRows = _persistedIndicatorConfig;
        var wroteIndicatorRows =
            TryPersistIndicatorRow(
                EmaIndicatorName,
                IsEmaPluginLoaded,
                IsEmaEnabled,
                EmaPeriodText,
                ref _persistedEmaEnabled,
                ref _persistedEmaPeriod,
                ref persistedIndicatorRows)
            | TryPersistIndicatorRow(
                WmaIndicatorName,
                IsWmaPluginLoaded,
                IsWmaEnabled,
                WmaPeriodText,
                ref _persistedWmaEnabled,
                ref _persistedWmaPeriod,
                ref persistedIndicatorRows)
            | TryPersistIndicatorRow(
                RsiIndicatorName,
                IsRsiPluginLoaded,
                IsRsiEnabled,
                RsiPeriodText,
                ref _persistedRsiEnabled,
                ref _persistedRsiPeriod,
                ref persistedIndicatorRows)
            | TryPersistIndicatorRow(
                MacdIndicatorName,
                IsMacdPluginLoaded,
                IsMacdEnabled,
                MacdPeriodText,
                ref _persistedMacdEnabled,
                ref _persistedMacdPeriod,
                ref persistedIndicatorRows)
            | TryPersistIndicatorRow(
                AtrIndicatorName,
                IsAtrPluginLoaded,
                IsAtrEnabled,
                AtrPeriodText,
                ref _persistedAtrEnabled,
                ref _persistedAtrPeriod,
                ref persistedIndicatorRows)
            | TryPersistIndicatorRow(
                BollIndicatorName,
                IsBollPluginLoaded,
                IsBollEnabled,
                BollPeriodText,
                ref _persistedBollEnabled,
                ref _persistedBollPeriod,
                ref persistedIndicatorRows);
        if (wroteIndicatorRows)
        {
            await _settings.SetAsync(
                UiSettingsKeys.Section,
                UiSettingsKeys.ChartIndicators,
                persistedIndicatorRows);
            _persistedIndicatorConfig = persistedIndicatorRows;
        }

        // The MCP server enable: only a real difference applies (the same
        // no-op discipline every other row keeps). The persisted write lands
        // first, the host apply follows: the saved choice starts or stops
        // the live server; the runner surfaces anything it could not reach.
        if (_isMcpServerEnabled != _persistedMcpServerEnabled)
        {
            await _settings.SetAsync(
                UiSettingsKeys.McpSection,
                UiSettingsKeys.McpServerEnabled,
                _isMcpServerEnabled);
            _persistedMcpServerEnabled = _isMcpServerEnabled;
            await _mcpHosts.ApplyAsync(_isMcpServerEnabled);
        }

        // The update check enable (T-213): only a real difference applies
        // (the same no-op discipline every other row keeps). No live apply
        // is needed — the checker's own change subscription reads the flip
        // without a restart; the next cadence tick, manual ask or launch
        // honors the saved choice.
        if (_isUpdateCheckEnabled != _persistedUpdateCheckEnabled)
        {
            await _settings.SetAsync(
                AppSettingsKeys.Section,
                AppSettingsKeys.UpdateCheck,
                _isUpdateCheckEnabled);
            _persistedUpdateCheckEnabled = _isUpdateCheckEnabled;
        }

        // The auto-update enable (T-258): only a real difference applies
        // (the same no-op discipline every other row keeps). No live apply
        // is needed — the staging service's own change subscription reads
        // the flip without a restart: a disabled loop downloads nothing,
        // and a newly enabled one waits for the next completed check.
        if (_isAutoUpdateEnabled != _persistedAutoUpdateEnabled)
        {
            await _settings.SetAsync(
                AppSettingsKeys.Section,
                AppSettingsKeys.AutoUpdate,
                _isAutoUpdateEnabled);
            _persistedAutoUpdateEnabled = _isAutoUpdateEnabled;
        }

        // The update channel (T-258): the combo index maps to the
        // canonical stored spelling; only a real difference applies. The
        // checker's own change subscription ranks the next check against
        // the saved channel; the combo offers exactly the two names, so no
        // garbage can be drafted here.
        var updateChannelName = UpdateChannel.ToSettingName(
            _selectedUpdateChannelIndex == BetaChannelIndex
                ? UpdateChannelKind.Beta
                : UpdateChannelKind.Stable);
        if (!string.Equals(updateChannelName, _persistedUpdateChannel, StringComparison.Ordinal))
        {
            await _settings.SetAsync(
                AppSettingsKeys.Section,
                AppSettingsKeys.UpdateChannel,
                updateChannelName);
            _persistedUpdateChannel = updateChannelName;
        }

        // The AI chat provider pick (T-207): only a real difference applies
        // (the same no-op discipline every row keeps). The stored spelling
        // rides the router's constants; a stored garbage pick repairs to the
        // drafted name here (the draft is what the user confirmed — the
        // router itself refuses garbage at send time).
        var aiProviderName = _aiProviderIndex == OpenAiProviderIndex
            ? ChatProviderRouter.OpenAiCompatibleProviderName
            : ChatProviderRouter.OllamaProviderName;
        if (!string.Equals(aiProviderName, _persistedAiProvider, StringComparison.Ordinal))
        {
            await _settings.SetAsync(AiSettingsKeys.Section, AiSettingsKeys.Provider, aiProviderName);
            _persistedAiProvider = aiProviderName;
        }

        // The Ollama and the OpenAI-compatible pieces: the same no-op
        // discipline, each piece its own key. An emptied draft saves
        // emptiness (the provider's read falls back — Ollama to its default,
        // the OpenAI model to the honest no-model send-time error).
        var ollamaUrlDraft = _ollamaUrlDraft.Trim();
        if (!string.Equals(ollamaUrlDraft, _persistedOllamaUrl, StringComparison.Ordinal))
        {
            await _settings.SetAsync(AiSettingsKeys.Section, AiSettingsKeys.OllamaUrl, ollamaUrlDraft);
            _persistedOllamaUrl = ollamaUrlDraft;
        }

        var ollamaModelDraft = _ollamaModelDraft.Trim();
        if (!string.Equals(ollamaModelDraft, _persistedOllamaModel, StringComparison.Ordinal))
        {
            await _settings.SetAsync(AiSettingsKeys.Section, AiSettingsKeys.Model, ollamaModelDraft);
            _persistedOllamaModel = ollamaModelDraft;
        }

        var openAiBaseUrlDraft = _openAiBaseUrlDraft.Trim();
        if (!string.Equals(openAiBaseUrlDraft, _persistedOpenAiBaseUrl, StringComparison.Ordinal))
        {
            await _settings.SetAsync(
                AiSettingsKeys.OpenAiCompatibleSection,
                AiSettingsKeys.OpenAiCompatibleBaseUrl,
                openAiBaseUrlDraft);
            _persistedOpenAiBaseUrl = openAiBaseUrlDraft;
        }

        var openAiModelDraft = _openAiModelDraft.Trim();
        if (!string.Equals(openAiModelDraft, _persistedOpenAiModel, StringComparison.Ordinal))
        {
            await _settings.SetAsync(
                AiSettingsKeys.OpenAiCompatibleSection,
                AiSettingsKeys.OpenAiCompatibleModel,
                openAiModelDraft);
            _persistedOpenAiModel = openAiModelDraft;
        }

        // The external-servers drafts: the rows map into the persisted list
        // verbatim — nothing coerced, an unusable value rides the entry and
        // the transport boundary fails it loudly — and only a real
        // difference applies (the same no-op discipline every row keeps);
        // the apply reconciles the live connections with the new list.
        List<McpServerConfig> serverConfigs = [];
        List<string> serverProblems = [];
        foreach (var row in McpServers)
        {
            var config = row.ToConfig(serverProblems);
            if (config is null)
            {
                serverProblems.Add("A draft row is dropped: no name — a nameless entry keys nothing");
                continue;
            }

            serverConfigs.Add(config);
        }

        foreach (var problem in serverProblems)
        {
            _logger.LogWarning("The external MCP server drafts problem: {Problem}", problem);
        }

        var writtenServers = McpServerConfigList.Write(serverConfigs);

        // The no-op discipline rides the semantic lists, not the wire bytes:
        // a hand-edited persisted file (a different key order) that describes
        // the same rows never triggers a rewrite or a live reconcile.
        var (persistedServers, _) = McpServerConfigList.Parse(_persistedServerJson);
        if (!persistedServers.SequenceEqual(serverConfigs))
        {
            await _settings.SetAsync(
                McpClientManager.ServersSection,
                McpClientManager.ServersKey,
                writtenServers);
            _persistedServerJson = writtenServers;
            await _mcpClients.ApplyAsync(serverConfigs);
        }

        // The hidden-pairs draft: only a real difference applies (the same
        // no-op discipline every other row keeps) and only with a real
        // exchange context behind it (a failed listing reset never wipes the
        // applied map). The map is rewritten with the draft exchange's entry
        // replaced by the draft — the other exchanges' entries ride
        // untouched.
        if (_draftExchangeId.Length > 0)
        {
            var appliedHidden = (IReadOnlySet<string>?)(_persistedHiddenMarks.GetValueOrDefault(_draftExchangeId));
            if (!_draftHiddenNames.SetEquals(appliedHidden ?? new HashSet<string>(StringComparer.Ordinal)))
            {
                var updatedMap = SymbolMarkMap.WithSet(_persistedHiddenMarks, _draftExchangeId, _draftHiddenNames);
                var written = SymbolMarkMap.Serialize(updatedMap);
                await _settings.SetAsync(
                    UiSettingsKeys.WorkspaceSection,
                    UiSettingsKeys.WorkspaceHiddenSymbols,
                    written);
                CacheHiddenMarks(written);
            }

            // The favorites draft: the same no-op discipline as the hidden
            // one (the other exchanges' entries ride untouched).
            var appliedFavorites = (IReadOnlySet<string>?)(_persistedFavoriteMarks.GetValueOrDefault(_draftExchangeId));
            if (!_draftFavoriteNames.SetEquals(appliedFavorites ?? new HashSet<string>(StringComparer.Ordinal)))
            {
                var updatedMap = SymbolMarkMap.WithSet(_persistedFavoriteMarks, _draftExchangeId, _draftFavoriteNames);
                var written = SymbolMarkMap.Serialize(updatedMap);
                await _settings.SetAsync(
                    UiSettingsKeys.WorkspaceSection,
                    UiSettingsKeys.WorkspaceFavoriteSymbols,
                    written);
                CacheFavoriteMarks(written);
            }
        }
    }

    #region Settings port (T-100)

    /// <summary>
    /// Export the user-adjustable settings subset to the user-picked local
    /// path (the write itself is the port service's work): the file carries
    /// the registry-owned stored keys only — secrets can never ride it. An
    /// outcome line renders the destination or the failure; the file system
    /// failure spears the status, the store stays untouched.
    /// </summary>
    public async Task ExportToFileAsync(string filePath, CancellationToken ct = default)
    {
        if (_settingsPort is null)
        {
            return;
        }

        ClearImport();
        try
        {
            await _settingsPort.ExportToFileAsync(filePath, ct);
            PortStatusText = _localization.GetString(PortExportedToKey, filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export the settings to {Path}", filePath);
            PortStatusText = _localization.GetString(PortExportFailedKey);
        }
    }

    /// <summary>
    /// Draft the import from the file text the view picked: the validation
    /// pass (schema, version, allowlist, value kinds — no defaults ever
    /// substituted) fills the dry-run preview when it passes and becomes the
    /// localized rejection line when it does not. Nothing applies here.
    /// </summary>
    public async Task DraftImportAsync(string jsonText, CancellationToken ct = default)
    {
        if (_settingsPort is null)
        {
            return;
        }

        // A fresh draft starts every outcome surface over: the previous
        // preview (and its pending plan) dies at once.
        ClearImport();
        try
        {
            var plan = await _settingsPort.DraftImportAsync(jsonText, ct);
            _pendingImportPlan = plan;

            foreach (var row in plan.Rows)
            {
                ImportPreview.Add(row);
            }

            // An import that validates but carries nothing new says that out
            // loud: the preview stays hidden and the one status line reports
            // the empty diff (nothing to confirm is not silence).
            IsImportPreviewVisible = plan.Rows.Count > 0;
            if (plan.Rows.Count == 0)
            {
                PortStatusText = _localization.GetString(PortImportNoChangeKey);
            }
        }
        catch (SettingsPortImportException exception)
        {
            _logger.LogWarning(exception, "The settings import was rejected");
            ImportErrorText = _localization.GetString(ErrorTextKeyOf(exception.Reason));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Everything typed above is a validation reject; anything here is
            // a real failure (a storage error under the draft) — the generic
            // honesty line speaks, never a specific claim the failure does
            // not support.
            _logger.LogError(exception, "Failed to draft the settings import");
            ImportErrorText = _localization.GetString(PortImportRejectedKey);
        }
    }

    /// <summary>
    /// Apply the pending import plan (the preview has already shown it): the
    /// one locked batch — the file lands once and every subscriber re-applies
    /// live (no restart). The plan dies after applying (a second application
    /// is a fresh preview's business).
    /// </summary>
    private async Task<RxVoid> ApplyImportCoreAsync(CancellationToken ct)
    {
        if (_settingsPort is null)
        {
            return RxVoid.Default;
        }

        var plan = _pendingImportPlan;
        if (plan is null)
        {
            return RxVoid.Default;
        }

        ClearImport();
        try
        {
            await _settingsPort.ApplyImportAsync(plan, ct);
            PortStatusText = _localization.GetString(PortImportAppliedKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply the settings import");
            PortStatusText = _localization.GetString(PortApplyFailedKey);
        }

        return RxVoid.Default;
    }

    /// <summary>
    /// The factory reset: behind the typed human confirmation (a decline
    /// renders the honest declined line and writes nothing), every
    /// registry-owned key returns to its default and the secrets store is
    /// never touched (the port has no secret-store dependency).
    /// </summary>
    public async Task ResetToDefaultsAsync(CancellationToken ct = default)
    {
        if (_settingsPort is null || _confirmations is null)
        {
            return;
        }

        try
        {
            var ran = await _settingsPort.ResetToDefaultsAsync(_confirmations, ct);
            PortStatusText = _localization.GetString(ran ? PortResetDoneKey : PortResetDeclinedKey);
            ClearImport();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset the settings to the factory defaults");
            PortStatusText = _localization.GetString(PortResetFailedKey);
        }
    }

    /// <summary>The typed rejection reason → its localized line (every key lives in both cultures).</summary>
    private static string ErrorTextKeyOf(SettingsPortErrorReason reason) => reason switch
    {
        SettingsPortErrorReason.MalformedJson => PortErrorMalformedJsonKey,
        SettingsPortErrorReason.UnknownSchema => PortErrorSchemaKey,
        SettingsPortErrorReason.UnsupportedVersion => PortErrorVersionKey,
        SettingsPortErrorReason.UnknownEntry => PortErrorUnknownEntryKey,
        SettingsPortErrorReason.WrongPayloadKind => PortErrorWrongTypeKey,
        _ => PortImportRejectedKey,
    };

    /// <summary>Hide + empty the pending draft (the preview, the plan and the error line).</summary>
    private void ClearImport()
    {
        _pendingImportPlan = null;
        ImportPreview.Clear();
        IsImportPreviewVisible = false;
        ImportErrorText = null;
    }

    #endregion Settings port (T-100)

    #region Profile backup (T-215)

    /// <summary>
    /// Archive the user's profile to the user-picked local path (the write
    /// itself is the backup service's work): one zip carries the manifest
    /// plus the settings file and every store snapshot; secrets are
    /// structurally absent from the entry list. An outcome line renders the
    /// destination or the failure; the file system failure spears the
    /// status, the live profile stays untouched.
    /// </summary>
    public async Task BackupToFileAsync(string filePath, CancellationToken ct = default)
    {
        if (_profileBackup is null)
        {
            return;
        }

        ClearRestoreDraft();
        try
        {
            await _profileBackup.BackupToFileAsync(filePath, ct);
            BackupStatusText = _localization.GetString(BackupDoneKey, filePath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to back the profile up to {Path}", filePath);
            BackupStatusText = _localization.GetString(BackupFailedKey);
        }
    }

    /// <summary>
    /// Draft the restore from the archive the view picked: the validation
    /// pass (schema, version, the manifest allowlist, the zip cross-check —
    /// no defaults ever substituted) fills the pre-localized preview when it
    /// passes and becomes the localized rejection line when it does not.
    /// Nothing applies here.
    /// </summary>
    public async Task DraftRestoreFromFileAsync(string archivePath, CancellationToken ct = default)
    {
        if (_profileBackup is null)
        {
            return;
        }

        // A fresh draft starts every outcome surface over: the previous
        // preview (and its pending plan) dies at once.
        ClearRestoreDraft();
        try
        {
            var plan = _profileBackup.DraftRestore(archivePath);
            _pendingRestorePlan = plan;

            foreach (var row in plan.Rows)
            {
                RestorePreview.Add(new BackupPreviewRow(
                    _localization.GetString(row.LabelKey),
                    _localization.GetString(BackupRowBytesKey, FormatSize(row.Bytes))));
            }

            IsRestorePreviewVisible = true;
        }
        catch (ProfileBackupRestoreException exception)
        {
            _logger.LogWarning(exception, "The profile restore draft was rejected");
            RestoreErrorText = _localization.GetString(BackupErrorTextKeyOf(exception.Reason));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Everything typed above is a validation reject; anything here is
            // a real failure (an I/O error under the draft) — the generic
            // honesty line speaks, never a specific claim the failure does
            // not support.
            _logger.LogError(exception, "Failed to draft the profile restore");
            RestoreErrorText = _localization.GetString(BackupRestoreFailedKey);
        }
    }

    /// <summary>
    /// Apply the pending restore plan (the preview has already shown it):
    /// the typed human confirmation gates the destructive batch (a decline
    /// renders the honest declined line and writes nothing), then the
    /// service re-validates the SAME archive and replaces exactly what it
    /// described. The plan dies after applying (a second application is a
    /// fresh preview's business).
    /// </summary>
    private async Task<RxVoid> ApplyRestoreCoreAsync(CancellationToken ct)
    {
        if (_profileBackup is null || _confirmations is null)
        {
            return RxVoid.Default;
        }

        var plan = _pendingRestorePlan;
        if (plan is null)
        {
            return RxVoid.Default;
        }

        ClearRestoreDraft();
        try
        {
            var ran = await _profileBackup.RestoreAsync(plan, _confirmations, ct);
            BackupStatusText = _localization.GetString(ran ? BackupRestoreDoneKey : BackupRestoreDeclinedKey);
        }
        catch (ProfileBackupRestoreException exception)
        {
            _logger.LogWarning(exception, "The profile restore was rejected");
            RestoreErrorText = _localization.GetString(BackupErrorTextKeyOf(exception.Reason));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply the profile restore");
            BackupStatusText = _localization.GetString(BackupRestoreFailedKey);
        }

        return RxVoid.Default;
    }

    /// <summary>The typed rejection reason → its localized line (every key lives in both cultures).</summary>
    private static string BackupErrorTextKeyOf(ProfileBackupErrorReason reason) => reason switch
    {
        ProfileBackupErrorReason.MalformedArchive => BackupErrorMalformedKey,
        ProfileBackupErrorReason.UnknownSchema => BackupErrorSchemaKey,
        ProfileBackupErrorReason.UnsupportedVersion => BackupErrorVersionKey,
        ProfileBackupErrorReason.ManifestMismatch => BackupErrorManifestKey,
        ProfileBackupErrorReason.NothingRestorable => BackupErrorEmptyKey,
        ProfileBackupErrorReason.LiveStoreLocked => BackupErrorLockedKey,
        _ => BackupRestoreFailedKey,
    };

    /// <summary>Hide + empty the pending draft (the preview, the plan and the error line).</summary>
    private void ClearRestoreDraft()
    {
        _pendingRestorePlan = null;
        RestorePreview.Clear();
        IsRestorePreviewVisible = false;
        RestoreErrorText = null;
    }

    /// <summary>
    /// Human-readable byte size for the preview rows (invariant digits; the
    /// unit suffixes are culture-neutral symbols, not localized words).
    /// </summary>
    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 20 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 20):F1} MB"),
        >= 1L << 10 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 10):F1} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
    };

    #endregion Profile backup (T-215)

    #endregion Public Methods

    #region Private Methods

    /// <summary>
    /// Sync the appearance selections (theme, language, clock, indicator row,
    /// the MCP enable draft, the update picks, the AI chat provider drafts)
    /// with the currently applied state. A pure in-memory sync — no I/O.
    /// </summary>
    private void ResetAppearanceSelections()
    {
        SelectedThemeIndex = IndexOfAppliedTheme();
        SelectedLanguageIndex = IndexOfAppliedLanguage();
        SelectedTimeFormatIndex = _persistedUse12HourClock ? Hours12Index : Hours24Index;
        IsSmaEnabled = _persistedSmaEnabled;
        SmaPeriodText = _persistedSmaPeriod > NoSmaPeriod
            ? _persistedSmaPeriod.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        IsEmaEnabled = _persistedEmaEnabled;
        EmaPeriodText = PeriodTextOf(_persistedEmaPeriod);
        IsWmaEnabled = _persistedWmaEnabled;
        WmaPeriodText = PeriodTextOf(_persistedWmaPeriod);
        IsRsiEnabled = _persistedRsiEnabled;
        RsiPeriodText = PeriodTextOf(_persistedRsiPeriod);
        IsMacdEnabled = _persistedMacdEnabled;
        MacdPeriodText = PeriodTextOf(_persistedMacdPeriod);
        IsAtrEnabled = _persistedAtrEnabled;
        AtrPeriodText = PeriodTextOf(_persistedAtrPeriod);
        IsBollEnabled = _persistedBollEnabled;
        BollPeriodText = PeriodTextOf(_persistedBollPeriod);
        IsMcpServerEnabled = _persistedMcpServerEnabled;
        IsUpdateCheckEnabled = _persistedUpdateCheckEnabled;
        IsAutoUpdateEnabled = _persistedAutoUpdateEnabled;
        SelectedUpdateChannelIndex = IndexOfUpdateChannel(_persistedUpdateChannel);

        // The AI chat provider (T-207): the pick drafts the combo (a garbage
        // stored pick drafts the default with a logged warning) and the text
        // drafts seed from the caches (blank stored values already resolved
        // to the shipped defaults at the seed reads; the OpenAI model has no
        // default — an unset one drafts empty). The key box re-seeds empty
        // (the secret never echoes) under the commit guard: a re-seed never
        // commits, the stored key stays exactly as the user left it.
        AiProviderIndex = IndexOfAiProvider(_persistedAiProvider);
        OllamaUrlDraft = _persistedOllamaUrl.Trim();
        OllamaModelDraft = _persistedOllamaModel.Trim();
        OpenAiBaseUrlDraft = _persistedOpenAiBaseUrl.Trim();
        OpenAiModelDraft = _persistedOpenAiModel.Trim();
        _aiKeyCommitSuppressed = true;
        OpenAiApiKeyDraft = string.Empty;
        _aiKeyCommitSuppressed = false;
    }

    /// <summary>
    /// Map the stored provider pick to its combo index: blank (unset) and
    /// the known spellings (case-insensitive) map directly; anything else
    /// drafts the default with a logged warning (the router refuses the
    /// same garbage honestly at send time).
    /// </summary>
    private int IndexOfAiProvider(string stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return OllamaProviderIndex;
        }

        var pick = stored.Trim();
        if (string.Equals(pick, ChatProviderRouter.OllamaProviderName, StringComparison.OrdinalIgnoreCase))
        {
            return OllamaProviderIndex;
        }

        if (string.Equals(pick, ChatProviderRouter.OpenAiCompatibleProviderName, StringComparison.OrdinalIgnoreCase))
        {
            return OpenAiProviderIndex;
        }

        _logger.LogWarning(
            "The stored AI chat provider '{Provider}' is not one of the known providers; the pick drafts the default",
            stored);
        return OllamaProviderIndex;
    }

    /// <summary>
    /// Map the stored channel pick to its combo index: the known spellings
    /// (case-insensitive) map directly; anything else drafts the stable
    /// default without a warning here — a garbage stored spelling is
    /// already repaired (with the logged warning) at the checker's own
    /// seed read, and the combo offers exactly the two names.
    /// </summary>
    private int IndexOfUpdateChannel(string stored) =>
        UpdateChannel.TryParse(stored, out var channel) && channel == UpdateChannelKind.Beta
            ? BetaChannelIndex
            : StableChannelIndex;

    /// <summary>
    /// Commit the typed API key to the secret store immediately: a typed
    /// value stores (overwrites), an emptied one removes (a clear is a real
    /// intent). Nothing here touches the settings file — the secret's only
    /// home is the store. A store-less construction stays memory-only.
    /// </summary>
    private async Task CommitOpenAiApiKeyAsync()
    {
        var store = _secrets;
        if (store is null)
        {
            return;
        }

        var draft = OpenAiApiKeyDraft;
        if (string.IsNullOrWhiteSpace(draft))
        {
            await store.RemoveAsync(AiSettingsKeys.OpenAiCompatibleApiKeySecretKey);
        }
        else
        {
            await store.SetAsync(AiSettingsKeys.OpenAiCompatibleApiKeySecretKey, draft);
        }
    }

    /// <summary>
    /// Render one cached period for its draft box: the sentinel (no row
    /// cached) reads as empty, any readable period renders invariant.
    /// </summary>
    private static string PeriodTextOf(int persistedPeriod) =>
        persistedPeriod > NoRowPeriod
            ? persistedPeriod.ToString(CultureInfo.InvariantCulture)
            : string.Empty;

    /// <summary>
    /// Repaint the pairs tab for the current exchange: the workspace
    /// exchange's listing seeds the all-pairs list and the persisted hidden
    /// and favorites maps seed the drafts (a draft re-derives from the
    /// applied state, so an unsaved draft dies at the next open). A failed
    /// listing fetch logs and leaves the lists stale — the theme rows keep
    /// working, the window must not fail to open over a listing hiccup.
    /// </summary>
    private async Task RefreshSymbolPairsAsync(CancellationToken ct)
    {
        try
        {
            var exchangeId = _activeExchanges.Id;
            var names = await _symbolList.GetSymbolsAsync(ct);
            var listing = names.Select(Symbol.Parse).ToList();

            _draftExchangeId = exchangeId;
            _draftHiddenNames = new HashSet<string>(
                _persistedHiddenMarks.GetValueOrDefault(exchangeId) ?? new HashSet<string>(StringComparer.Ordinal),
                StringComparer.Ordinal);
            _draftFavoriteNames = new HashSet<string>(
                _persistedFavoriteMarks.GetValueOrDefault(exchangeId) ?? new HashSet<string>(StringComparer.Ordinal),
                StringComparer.Ordinal);
            _listingNames = listing.Select(symbol => symbol.Normalized).ToList();

            RebuildPairCollections();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh the symbol pairs; the lists stay stale");
        }
    }

    /// <summary>
    /// Repaint the two pair lists from the listing and the drafts: the
    /// all-pairs list pins the starred names to the head (the exchange's own
    /// order inside each group), the rest follows that order; the hidden
    /// list keeps the hidden names in the exchange's order and appends the
    /// stale ones (still hidden, no longer listed) in their normalized
    /// order. The marks are mutually exclusive by rule: a favorite beats a
    /// stored hidden entry (the legacy-collision decision — a starred name
    /// never shows as hidden; the moves and the save repair the document).
    /// Both picks reset — the lists repainted, the inputs died.
    /// </summary>
    private void RebuildPairCollections()
    {
        _allPairs.Clear();
        foreach (var name in _listingNames.Where(_draftFavoriteNames.Contains))
        {
            _allPairs.Add(new PairRow(name, IsFavorite: true));
        }

        foreach (var name in _listingNames.Where(name =>
            !_draftHiddenNames.Contains(name) && !_draftFavoriteNames.Contains(name)))
        {
            _allPairs.Add(new PairRow(name, IsFavorite: false));
        }

        _hiddenPairs.Clear();
        foreach (var name in _listingNames.Where(name =>
            _draftHiddenNames.Contains(name) && !_draftFavoriteNames.Contains(name)))
        {
            _hiddenPairs.Add(name);
        }

        foreach (var name in _draftHiddenNames
            .Except(_listingNames)
            .Where(name => !_draftFavoriteNames.Contains(name))
            .OrderBy(static name => name, StringComparer.Ordinal))
        {
            _hiddenPairs.Add(name);
        }

        SelectedAllPair = null;
        SelectedHiddenPair = null;
    }

    /// <summary>
    /// The all-pairs pick joins the hidden draft; the lists repaint and the
    /// pick lands on the pair that took the moved row's slot (the next row
    /// heading up, the new last one when the list tail moved). A starred
    /// pick never reaches this code — the canExecute gate refuses it.
    /// </summary>
    private void HideSelectedPair()
    {
        if (SelectedAllPair is not { } picked)
        {
            return;
        }

        var movedIndex = _allPairs.IndexOf(picked);
        _draftHiddenNames.Add(picked.Name);
        RebuildPairCollections();
        if (_allPairs.Count > 0)
        {
            SelectedAllPair = _allPairs[Math.Min(Math.Max(movedIndex, 0), _allPairs.Count - 1)];
        }
    }

    /// <summary>
    /// The hidden pick leaves the hidden draft; the lists repaint and the
    /// pick lands on the pair that took the moved row's slot (the next row
    /// heading up, the new last one when the list tail moved).
    /// </summary>
    private void RevealSelectedPair()
    {
        if (SelectedHiddenPair is not { } name)
        {
            return;
        }

        var movedIndex = _hiddenPairs.IndexOf(name);
        _draftHiddenNames.Remove(name);
        RebuildPairCollections();
        if (_hiddenPairs.Count > 0)
        {
            SelectedHiddenPair = _hiddenPairs[Math.Min(Math.Max(movedIndex, 0), _hiddenPairs.Count - 1)];
        }
    }

    /// <summary>
    /// Star or unstar the given row's pair: the favorites draft updates, the
    /// lists repaint (a starred pair pins to the head) and the pick lands on
    /// the toggled row's fresh slot — the reassignment raises the pick
    /// observable, so the move buttons' gates recompute against the new star
    /// state. Nothing persists here: the save applies the draft.
    /// </summary>
    private void TogglePairFavorite(PairRow row)
    {
        var name = row.Name;
        if (!_draftFavoriteNames.Contains(name))
        {
            _draftFavoriteNames.Add(name);
        }
        else
        {
            _draftFavoriteNames.Remove(name);
        }

        RebuildPairCollections();
        var fresh = _allPairs.FirstOrDefault(candidate => candidate.Name == name);
        if (fresh is not null)
        {
            SelectedAllPair = fresh;
        }
    }

    /// <summary>
    /// Cache the persisted hidden-pairs document (fail-safe parse, garbage
    /// drops — see <see cref="SymbolMarkMap"/>).
    /// </summary>
    private void CacheHiddenMarks(string? json)
    {
        _persistedHiddenMarks = SymbolMarkMap.Parse(json);
    }

    /// <summary>
    /// Cache the persisted favorites document (fail-safe parse, garbage
    /// drops — see <see cref="SymbolMarkMap"/>).
    /// </summary>
    private void CacheFavoriteMarks(string? json)
    {
        _persistedFavoriteMarks = SymbolMarkMap.Parse(json);
    }

    /// <summary>
    /// Cache the persisted indicator config and derive the SMA row's pieces
    /// (the first SMA row in the array wins; no row caches the sentinel
    /// period, the selections show an empty box).
    /// </summary>
    private void CacheIndicatorConfig(string indicatorSettingsJson)
    {
        _persistedIndicatorConfig = indicatorSettingsJson;

        var (configs, _) = ChartIndicatorConfig.ParseList(indicatorSettingsJson);
        var sma = configs.FirstOrDefault(config =>
            string.Equals(config.Name, SmaIndicatorName, StringComparison.Ordinal));

        _persistedSmaEnabled = sma?.Enabled ?? false;
        _persistedSmaPeriod = sma?.Period ?? NoSmaPeriod;

        // The standard pack rows (T-092): the same first-row-wins derivation
        // keyed per canonical name. A nameless config leaves every gate
        // false and every period at the sentinel (an empty period box).
        var ema = configs.FirstOrDefault(config =>
            string.Equals(config.Name, EmaIndicatorName, StringComparison.Ordinal));
        var wma = configs.FirstOrDefault(config =>
            string.Equals(config.Name, WmaIndicatorName, StringComparison.Ordinal));
        var rsi = configs.FirstOrDefault(config =>
            string.Equals(config.Name, RsiIndicatorName, StringComparison.Ordinal));
        var macd = configs.FirstOrDefault(config =>
            string.Equals(config.Name, MacdIndicatorName, StringComparison.Ordinal));
        var atr = configs.FirstOrDefault(config =>
            string.Equals(config.Name, AtrIndicatorName, StringComparison.Ordinal));
        var boll = configs.FirstOrDefault(config =>
            string.Equals(config.Name, BollIndicatorName, StringComparison.Ordinal));

        _persistedEmaEnabled = ema?.Enabled ?? false;
        _persistedEmaPeriod = ema?.Period ?? NoRowPeriod;
        _persistedWmaEnabled = wma?.Enabled ?? false;
        _persistedWmaPeriod = wma?.Period ?? NoRowPeriod;
        _persistedRsiEnabled = rsi?.Enabled ?? false;
        _persistedRsiPeriod = rsi?.Period ?? NoRowPeriod;
        _persistedMacdEnabled = macd?.Enabled ?? false;
        _persistedMacdPeriod = macd?.Period ?? NoRowPeriod;
        _persistedAtrEnabled = atr?.Enabled ?? false;
        _persistedAtrPeriod = atr?.Period ?? NoRowPeriod;
        _persistedBollEnabled = boll?.Enabled ?? false;
        _persistedBollPeriod = boll?.Period ?? NoRowPeriod;
    }

    /// <summary>
    /// Parse the SMA selection: the period text must read as an integer
    /// (invariant culture, trimmed). Invalid text returns false - the save
    /// skips the row, nothing substitutes a default.
    /// </summary>
    private bool TryReadSmaSelection(out bool enabled, out int period)
    {
        enabled = IsSmaEnabled;
        return int.TryParse(
            SmaPeriodText.Trim(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out period);
    }

    /// <summary>
    /// The pack row's save gate (T-092): exactly the discipline the SMA row
    /// keeps, generalized. A plugin the session does not own saves nothing;
    /// a readable draft with no real difference saves nothing (the no-op
    /// discipline); a readable real difference patches the row into the
    /// array and reports the write; an unreadable draft saves nothing — with
    /// a real difference candidate it is loud (a warning, nothing defaults),
    /// in the natural zero state (no row cached, the box empty, the enable
    /// unchanged) it is silent. The cached pair and the array travel by
    /// reference: the rows save sequentially, each seeing the previous
    /// row's write.
    /// </summary>
    private bool TryPersistIndicatorRow(
        string name,
        bool isLoaded,
        bool draftEnabled,
        string draftPeriodText,
        ref bool cachedEnabled,
        ref int cachedPeriod,
        ref string indicatorJson)
    {
        if (!isLoaded)
        {
            return false;
        }

        var readable = int.TryParse(
            draftPeriodText.Trim(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var period);

        if (readable)
        {
            if (draftEnabled == cachedEnabled && period == cachedPeriod)
            {
                return false;
            }

            indicatorJson = ReplaceIndicatorRow(indicatorJson, name, draftEnabled, period);
            cachedEnabled = draftEnabled;
            cachedPeriod = period;
            return true;
        }

        if (draftEnabled == cachedEnabled && string.IsNullOrWhiteSpace(draftPeriodText))
        {
            return false;
        }

        _logger.LogWarning(
            "The {Name} period '{PeriodText}' is not a readable integer; the indicator selection is not saved",
            name,
            draftPeriodText);
        return false;
    }

    /// <summary>
    /// Patch one row into the indicator config array: every other row rides
    /// verbatim (T-067's rewrite discipline), the row's own previous entry
    /// is replaced by the draft, a first-time entry appends.
    /// </summary>
    private static string ReplaceIndicatorRow(
        string indicatorJson,
        string name,
        bool enabled,
        int period)
    {
        var (configs, _) = ChartIndicatorConfig.ParseList(indicatorJson);
        var updated = configs
            .Where(config => !string.Equals(config.Name, name, StringComparison.Ordinal))
            .ToList();
        updated.Add(new ChartIndicatorConfig(name, enabled, period));

        return ChartIndicatorConfig.Write(updated);
    }

    /// <summary>
    /// Open the user themes folder in the OS file manager; the platform
    /// selection (explorer.exe / open / xdg-open) lives in Infrastructure.
    /// </summary>
    private void HandleOpenThemesFolder()
    {
        try
        {
            _shellUtils.OpenInFileManager(ConfigPathResolver.ThemesDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open the themes folder {Path}", ConfigPathResolver.ThemesDirectory);
        }
    }

    /// <summary>Index of the applied theme (matched by canonical Name) in AvailableThemes.</summary>
    private int IndexOfAppliedTheme()
    {
        var themes = _themeManager.AvailableThemes;
        for (var i = 0; i < themes.Count; i++)
        {
            if (IsThemeApplied(themes[i]))
            {
                return i;
            }
        }

        _logger.LogWarning("Applied theme {Theme} is not among available themes", _themeManager.CurrentTheme);
        return NoSelection;
    }

    /// <summary>Index of the applied language in AvailableLanguages: exact code, then two-letter fallback ("ru-RU" -> "ru").</summary>
    private int IndexOfAppliedLanguage()
    {
        var languages = _localization.AvailableLanguages;
        var current = _localization.CurrentLanguage;
        for (var i = 0; i < languages.Count; i++)
        {
            if (IsLanguageApplied(languages[i]))
            {
                return i;
            }
        }

        try
        {
            var shortCode = new CultureInfo(current).TwoLetterISOLanguageName;
            for (var i = 0; i < languages.Count; i++)
            {
                if (languages[i].Code.Equals(shortCode, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }
        catch (CultureNotFoundException ex)
        {
            _logger.LogWarning(ex, "Unknown current culture {Language}", current);
        }

        _logger.LogWarning("Applied language {Language} is not among available languages", current);
        return NoSelection;
    }

    private bool IsThemeApplied(ThemeInfo theme) =>
        _themeManager.CurrentTheme.Equals(theme.Name, StringComparison.OrdinalIgnoreCase);

    private bool IsLanguageApplied(LanguageInfo language) =>
        _localization.CurrentLanguage.Equals(language.Code, StringComparison.OrdinalIgnoreCase);

    private static T? PickAt<T>(IReadOnlyList<T> items, int index)
        where T : class
    {
        return index >= 0 && index < items.Count ? items[index] : null;
    }

    /// <summary>
    /// Seed the external-servers drafts from the persisted list: the rows
    /// carry the configs' fields verbatim and their connection states ride
    /// the manager's latest snapshot (a name the manager has no state for —
    /// an unsaved brand-new row — keeps the honest "not known" dash). The
    /// window open calls this, so abandoned (unsaved) drafts are discarded
    /// the same way every other selection discards.
    /// </summary>
    private async Task RefreshMcpClientRowsAsync(CancellationToken ct)
    {
        var persisted = await _settings.GetAsync<string>(
            McpClientManager.ServersSection,
            McpClientManager.ServersKey,
            McpClientManager.DefaultServersJson);
        ct.ThrowIfCancellationRequested();

        var (servers, problems) = McpServerConfigList.Parse(persisted);
        foreach (var problem in problems)
        {
            _logger.LogWarning("The persisted MCP client list skipped a row: {Problem}", problem);
        }

        var states = _mcpClients.States;
        McpServers.Clear();
        foreach (var server in servers)
        {
            var row = McpServerRow.FromConfig(server);
            row.SetState(states.FirstOrDefault(state => state.Name.Equals(server.Name, StringComparison.Ordinal)));
            McpServers.Add(row);
        }

        _persistedServerJson = persisted;
    }

    /// <summary>
    /// Repaint the draft rows with the manager's fresh snapshot: the states
    /// key by entry name (the key both surfaces share), a row the snapshot
    /// has no entry for keeps the honest "not known" dash.
    /// </summary>
    private void ApplyMcpClientStates(IReadOnlyList<McpClientState> states)
    {
        foreach (var row in McpServers)
        {
            row.SetState(states.FirstOrDefault(state => state.Name.Equals(row.Name, StringComparison.Ordinal)));
        }
    }

    /// <summary>Append one blank external-server draft row; Add stays local, only Save persists.</summary>
    private void AddServerDraftRow() =>
        McpServers.Add(new McpServerRow());

    #endregion Private Methods
}

/// <summary>
/// One pre-localized row of the restore preview (T-215): the backup service
/// hands over a label KEY (the entry's archive path is machine text); the
/// ViewModel localizes it at draft time and pairs it with the entry's
/// human-readable byte size, so the view binds plain display strings.
/// </summary>
/// <param name="Label">The localized entry name ("Settings", "Order history", ...).</param>
/// <param name="SizeText">The localized size line ("12.3 KB" through the row-size key).</param>
public sealed record BackupPreviewRow(string Label, string SizeText);

