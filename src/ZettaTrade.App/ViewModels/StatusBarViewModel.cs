namespace ZettaTrade.App.ViewModels;

using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using ReactiveUI;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// ViewModel for status bar.
/// Simple strings use direct XAML binding via {loc:Localized Key}.
/// ViewModel properties only for dynamic data (settings, connection status).
/// </summary>
public sealed class StatusBarViewModel : ReactiveObject, IActivatableViewModel
{
    public ViewModelActivator Activator { get; } = new();
    
    private readonly ISettingsService _settings;
    private string _currentThemeName = string.Empty;
    private string _currentLanguage = string.Empty;
    private string _connectionStatus = string.Empty;

    public string CurrentThemeName
    {
        get => _currentThemeName;
        private set => this.RaiseAndSetIfChanged(ref _currentThemeName, value);
    }
    
    public string CurrentLanguage
    {
        get => _currentLanguage;
        private set => this.RaiseAndSetIfChanged(ref _currentLanguage, value);
    }
    
    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => this.RaiseAndSetIfChanged(ref _connectionStatus, value);
    }

    public StatusBarViewModel(ISettingsService settings)
    {
        _settings = settings;

        this.WhenActivated(disposables =>
        {
            // Load settings reactively (dynamic data needs ViewModel properties)
            Observable.FromAsync(() => _settings.GetAsync("UI", "Theme", "Dark"))
                .Subscribe(value => CurrentThemeName = value)
                .DisposeWith(disposables);

            Observable.FromAsync(() => _settings.GetAsync("UI", "Language", "en"))
                .Subscribe(value => CurrentLanguage = value)
                .DisposeWith(disposables);
        });
    }

    public void UpdateConnectionStatus(bool isConnected)
    {
        ConnectionStatus = isConnected ? "Connected" : "Disconnected";
    }
}
