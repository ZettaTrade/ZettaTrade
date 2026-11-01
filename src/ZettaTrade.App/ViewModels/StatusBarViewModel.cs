namespace ZettaTrade.App.ViewModels;

using System.Reactive.Linq;
using ReactiveUI;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// ViewModel for status bar.
/// Simple strings use direct XAML binding via {loc:Localized Key}.
/// ViewModel properties only for dynamic data (theme, language, connection status).
/// </summary>
/// <remarks>
/// The status bar is registered as an application-lifetime singleton, so the theme
/// and language chips are wired to their managers in the constructor: WhenActivated
/// would never fire for it. Both managers broadcast their current value on
/// subscribe, so the chips start with the theme and language applied at startup.
/// </remarks>
public sealed class StatusBarViewModel : ReactiveObject
{
    #region Properties

    private string _currentThemeName = string.Empty;
    private string _currentLanguage = string.Empty;
    private string _connectionStatus = string.Empty;
    private readonly ILocalizationManager _localizationManager;

    /// <summary>
    /// Last adapter connection state reported to
    /// <see cref="UpdateConnectionStatus"/>; null before the first report.
    /// </summary>
    private bool? _lastConnectionState;

    /// <summary>Running application version for the status bar chip.</summary>
    public string AppVersion
    {
        get;
    }

    /// <summary>Current theme name.</summary>
    public string CurrentThemeName
    {
        get => _currentThemeName;
        private set => this.RaiseAndSetIfChanged(ref _currentThemeName, value);
    }

    /// <summary>Current language code.</summary>
    public string CurrentLanguage
    {
        get => _currentLanguage;
        private set => this.RaiseAndSetIfChanged(ref _currentLanguage, value);
    }

    /// <summary>Connection status text.</summary>
    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => this.RaiseAndSetIfChanged(ref _connectionStatus, value);
    }

    #endregion Properties

    #region Constructor

    /// <summary>
    /// Create a new StatusBarViewModel instance.
    /// </summary>
    /// <param name="themeManager">Theme manager providing the theme chip state.</param>
    /// <param name="localizationManager">Localization manager providing the language chip state.</param>
    /// <param name="appVersion">Running version for the version chip.</param>
    public StatusBarViewModel(
        IThemeManager themeManager,
        ILocalizationManager localizationManager,
        string appVersion)
    {
        _localizationManager = localizationManager;
        AppVersion = appVersion;

        // The ViewModel lives as long as the application (singleton), so these
        // subscriptions mirror it and need no explicit disposal.
        themeManager
            .ThemeChanged
            .Subscribe(name => CurrentThemeName = ResolveChipName(themeManager, name));

        localizationManager
            .LanguageChanged
            .Subscribe(code =>
            {
                CurrentLanguage = code;
                RefreshConnectionStatus();
            });
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>
    /// Store the adapter connection state and refresh the checkpoint chip
    /// (also invoked on language changes).
    /// </summary>
    /// <param name="isConnected">Current adapter connection state.</param>
    public void UpdateConnectionStatus(bool isConnected)
    {
        _lastConnectionState = isConnected;
        RefreshConnectionStatus();
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>
    /// Localized connection chip text keys.
    /// </summary>
    private const string ConnectedStatusKey = "Status.Connected";
    private const string DisconnectedStatusKey = "Status.Disconnected";

    /// <summary>
    /// Derive the connection chip text from the stored adapter state: empty
    /// before the first report, otherwise the localized
    /// <see cref="ConnectedStatusKey"/> / <see cref="DisconnectedStatusKey"/>
    /// value (re-derived on every language change so a runtime switch keeps
    /// the chip localized).
    /// </summary>
    private void RefreshConnectionStatus() =>
        ConnectionStatus = _lastConnectionState is not { } isConnected
            ? string.Empty
            : _localizationManager.GetString(
                isConnected ? ConnectedStatusKey : DisconnectedStatusKey);

    /// <summary>
    /// Chip display name for a canonical theme Name: the matching theme's
    /// DisplayName, or the raw name when the theme is not (yet) registered
    /// (subscribe can run before the manager populated its list).
    /// </summary>
    private static string ResolveChipName(IThemeManager themeManager, string themeName) =>
        themeManager.AvailableThemes
            .FirstOrDefault(t => t.Name.Equals(themeName, StringComparison.OrdinalIgnoreCase))
            ?.DisplayName ?? themeName;

    #endregion Private Methods
}
