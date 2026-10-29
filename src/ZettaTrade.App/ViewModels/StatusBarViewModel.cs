namespace ZettaTrade.App.ViewModels;

using System.Reactive.Disposables;
using System.Reactive.Linq;
using ReactiveUI;
using ReactiveUI.Primitives;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// ViewModel for status bar.
/// Simple strings use direct XAML binding via {loc:Localized Key}.
/// ViewModel properties only for dynamic data (settings, connection status).
/// </summary>
public sealed class StatusBarViewModel : ReactiveObject, IActivatableViewModel
{
    #region Properties

    public ViewModelActivator Activator { get; } = new();

    private string _currentThemeName = string.Empty;
    private string _currentLanguage = string.Empty;
    private string _connectionStatus = string.Empty;

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

    #region Private Fields

    private readonly ISettingsService _settings;

    #endregion Private Fields

    #region Constructor

    /// <summary>
    /// Create a new StatusBarViewModel instance.
    /// </summary>
    public StatusBarViewModel(ISettingsService settings)
    {
        _settings = settings;

        this.WhenActivated(disposables =>
        {
            Observable.FromAsync(() => _settings.GetAsync(
                UiSettingsKeys.Section,
                UiSettingsKeys.Theme,
                UiSettingsKeys.DefaultTheme))
                .Subscribe(value => CurrentThemeName = value)
                .DisposeWith(disposables);

            Observable.FromAsync(() => _settings.GetAsync(
                UiSettingsKeys.Section,
                UiSettingsKeys.Language,
                UiSettingsKeys.DefaultLanguage))
                .Subscribe(value => CurrentLanguage = value)
                .DisposeWith(disposables);
        });
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
}
