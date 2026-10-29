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

    /// <summary>Menu ViewModel for menu bar.</summary>
    public MenuViewModel Menu { get; }

    /// <summary>Status bar ViewModel.</summary>
    public StatusBarViewModel StatusBar { get; }

    /// <summary>Chat ViewModel for AI chat panel.</summary>
    public ChatViewModel Chat { get; }

    /// <summary>Settings ViewModel for theme and language management.</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>Chart ViewModel for displaying price charts.</summary>
    public ChartViewModel Chart { get; }

    /// <summary>
    /// Create a new MainViewModel instance.
    /// </summary>
    public MainViewModel(
        MenuViewModel menu,
        StatusBarViewModel statusBar,
        ChatViewModel chat,
        SettingsViewModel settings,
        ChartViewModel chart,
        ILogger<MainViewModel> logger)
    {
        Menu = menu;
        StatusBar = statusBar;
        Chat = chat;
        Settings = settings;
        Chart = chart;
        _logger = logger;

        _logger.LogInformation("MainViewModel initialized with sub-ViewModels");
    }
}
