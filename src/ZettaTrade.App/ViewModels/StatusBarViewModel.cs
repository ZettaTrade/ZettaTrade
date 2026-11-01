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
        AppVersion = appVersion;

        // The ViewModel lives as long as the application (singleton), so these
        // subscriptions mirror it and need no explicit disposal.
        themeManager
            .ThemeChanged
            .Subscribe(name => CurrentThemeName = ResolveChipName(themeManager, name));

        localizationManager
            .LanguageChanged
            .Subscribe(code => CurrentLanguage = code);
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>
    /// Update connection status.
    /// </summary>
    public void UpdateConnectionStatus(bool isConnected)
    {
        ConnectionStatus = isConnected ? "Connected" : "Disconnected";
    }

    #endregion Public Methods

    #region Private Methods

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
