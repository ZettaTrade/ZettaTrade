namespace ZettaTrade.App.ViewModels;

using Microsoft.Extensions.Logging;
using ReactiveUI;

/// <summary>
/// Main window view model. Coordinates sub-ViewModels for menu, status bar, chat, and settings.
/// Follows Single Responsibility Principle by delegating concerns to specialized ViewModels.
/// </summary>
public sealed class MainViewModel : ReactiveObject
{
    private readonly ILogger<MainViewModel> _logger;

    public MenuViewModel Menu { get; }
    public StatusBarViewModel StatusBar { get; }
    public ChatViewModel Chat { get; }
    public SettingsViewModel Settings { get; }

    public MainViewModel(
        MenuViewModel menu,
        StatusBarViewModel statusBar,
        ChatViewModel chat,
        SettingsViewModel settings,
        ILogger<MainViewModel> logger)
    {
        Menu = menu;
        StatusBar = statusBar;
        Chat = chat;
        Settings = settings;
        _logger = logger;

        _logger.LogInformation("MainViewModel initialized with sub-ViewModels");
    }
}
