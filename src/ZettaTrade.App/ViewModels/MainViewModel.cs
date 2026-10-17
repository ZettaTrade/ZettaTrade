using System.Collections.ObjectModel;
using System.Reactive;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.Core.Interfaces;

namespace ZettaTrade.App.ViewModels;

/// <summary>
/// Main window view model. Orchestrates the top-level UI state.
/// Inherits from LocalizedViewModelBase for automatic language change handling.
/// </summary>
public sealed class MainViewModel : LocalizedViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly IMcpServerHost _mcpServer;
    private readonly ILogger<MainViewModel> _logger;
    
    // Localized properties - updated automatically on language change
    private string _menuFile = string.Empty;
    private string _menuView = string.Empty;
    private string _menuTrading = string.Empty;
    private string _menuAi = string.Empty;
    private string _statusText = string.Empty;
    private string _connectionStatus = "Disconnected";
    private string _currentThemeName = "Dark";
    private string _currentLanguage = "en";
    private string _aiChatInput = string.Empty;

    public MainViewModel(
        ISettingsService settings,
        IMcpServerHost mcpServer,
        ILocalizationManager localizationManager,
        ILogger<MainViewModel> logger)
        : base(localizationManager)
    {
        _settings = settings;
        _mcpServer = mcpServer;
        _logger = logger;

        // Commands — use explicit Unit type to avoid RxVoid inference
        OpenSettingsCommand = ReactiveCommand.CreateFromTask(OpenSettingsAsync);
        ExitCommand = ReactiveCommand.Create(() =>
        {
            // Proper shutdown - allows Serilog to flush
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lifetime)
            {
                lifetime.Shutdown();
            }
        });
        NewOrderCommand = ReactiveCommand.Create((Action)(() => { /* TODO */ }));
        ShowPositionsCommand = ReactiveCommand.Create((Action)(() => { /* TODO */ }));
        ShowOrderHistoryCommand = ReactiveCommand.Create((Action)(() => { /* TODO */ }));
        OpenAiChatCommand = ReactiveCommand.Create((Action)(() => { /* TODO */ }));
        ManageMcpServersCommand = ReactiveCommand.Create((Action)(() => { /* TODO */ }));

        // Initialize collections
        AvailableThemes = new ObservableCollection<string> { "Dark", "Light", "Midnight Blue", "OLED Black" };
        AvailableLanguages = new ObservableCollection<string> { "English", "Русский" };
        ChatMessages = new ObservableCollection<ChatMessageViewModel>();

        // Load persisted settings
        _ = LoadSettingsAsync();
    }

    // Localized properties with change notification
    public string MenuFile
    {
        get => _menuFile;
        private set => this.RaiseAndSetIfChanged(ref _menuFile, value);
    }

    public string MenuView
    {
        get => _menuView;
        private set => this.RaiseAndSetIfChanged(ref _menuView, value);
    }

    public string MenuTrading
    {
        get => _menuTrading;
        private set => this.RaiseAndSetIfChanged(ref _menuTrading, value);
    }

    public string MenuAi
    {
        get => _menuAi;
        private set => this.RaiseAndSetIfChanged(ref _menuAi, value);
    }

    // Other observable properties
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    public string ConnectionStatus
    {
        get => _connectionStatus;
        set => this.RaiseAndSetIfChanged(ref _connectionStatus, value);
    }

    public string CurrentThemeName
    {
        get => _currentThemeName;
        set => this.RaiseAndSetIfChanged(ref _currentThemeName, value);
    }

    public string CurrentLanguage
    {
        get => _currentLanguage;
        set => this.RaiseAndSetIfChanged(ref _currentLanguage, value);
    }

    public string AiChatInput
    {
        get => _aiChatInput;
        set => this.RaiseAndSetIfChanged(ref _aiChatInput, value);
    }

    // Collections
    public ObservableCollection<string> AvailableThemes { get; }
    public ObservableCollection<string> AvailableLanguages { get; }
    public ObservableCollection<ChatMessageViewModel> ChatMessages { get; }

    // Commands
    public ReactiveCommand<Unit, Unit> OpenSettingsCommand { get; }
    public ReactiveCommand<Unit, Unit> ExitCommand { get; }
    public ReactiveCommand<Unit, Unit> NewOrderCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowPositionsCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowOrderHistoryCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenAiChatCommand { get; }
    public ReactiveCommand<Unit, Unit> ManageMcpServersCommand { get; }

    /// <summary>
    /// Called automatically on activation and when language changes.
    /// Update all localized properties here.
    /// </summary>
    protected override void OnLanguageChanged()
    {
        MenuFile = Loc.GetString("Menu.File");
        MenuView = Loc.GetString("Menu.View");
        MenuTrading = Loc.GetString("Menu.Trading");
        MenuAi = Loc.GetString("Menu.AI");
        StatusText = Loc.GetString("Status.Ready");
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            CurrentThemeName = await _settings.GetAsync("UI", "Theme", "Dark");
            CurrentLanguage = await _settings.GetAsync("UI", "Language", "en");

            _logger.LogInformation("Settings loaded: Theme={Theme}, Language={Language}",
                CurrentThemeName, CurrentLanguage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load settings");
        }
    }

    private Task OpenSettingsAsync()
    {
        // TODO: Open settings dialog
        StatusText = "Settings opened";
        return Task.CompletedTask;
    }
}

/// <summary>
/// View model for a single chat message in the AI panel.
/// </summary>
public sealed class ChatMessageViewModel : ReactiveObject
{
    public required string Role { get; init; } // "user" or "ai"
    public required string Content { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
}
