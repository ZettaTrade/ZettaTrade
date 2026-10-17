using System.Reactive;
using ReactiveUI;
using ZettaTrade.Core.Interfaces;

namespace ZettaTrade.App.ViewModels;

/// <summary>
/// ViewModel for menu bar with localized menu items and commands.
/// </summary>
public sealed class MenuViewModel : ReactiveObject, IActivatableViewModel
{
    public ViewModelActivator Activator { get; } = new();
    
    private readonly ILocalizationManager _localization;

    [Reactive] public string MenuFile { get; set; } = string.Empty;
    [Reactive] public string MenuView { get; set; } = string.Empty;
    [Reactive] public string MenuTrading { get; set; } = string.Empty;
    [Reactive] public string MenuAi { get; set; } = string.Empty;
    
    [Reactive] public string MenuItemNewOrder { get; set; } = string.Empty;
    [Reactive] public string MenuItemPositions { get; set; } = string.Empty;
    [Reactive] public string MenuItemOrderHistory { get; set; } = string.Empty;
    [Reactive] public string MenuItemAiChat { get; set; } = string.Empty;
    [Reactive] public string MenuItemMcpServers { get; set; } = string.Empty;
    [Reactive] public string MenuItemSettings { get; set; } = string.Empty;
    [Reactive] public string MenuItemExit { get; set; } = string.Empty;

    public ReactiveCommand<Unit, Unit> NewOrderCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowPositionsCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowOrderHistoryCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenAiChatCommand { get; }
    public ReactiveCommand<Unit, Unit> ManageMcpServersCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenSettingsCommand { get; }
    public ReactiveCommand<Unit, Unit> ExitCommand { get; }

    public MenuViewModel(ILocalizationManager localization)
    {
        _localization = localization;

        // Initialize commands with proper handlers
        NewOrderCommand = ReactiveCommand.CreateFromTask(HandleNewOrderAsync);
        ShowPositionsCommand = ReactiveCommand.CreateFromTask(HandleShowPositionsAsync);
        ShowOrderHistoryCommand = ReactiveCommand.CreateFromTask(HandleShowOrderHistoryAsync);
        OpenAiChatCommand = ReactiveCommand.CreateFromTask(HandleOpenAiChatAsync);
        ManageMcpServersCommand = ReactiveCommand.CreateFromTask(HandleManageMcpServersAsync);
        OpenSettingsCommand = ReactiveCommand.CreateFromTask(HandleOpenSettingsAsync);
        ExitCommand = ReactiveCommand.Create(HandleExit);

        this.WhenActivated(disposables =>
        {
            // Subscribe to language changes
            _localization.LanguageChanged
                .Subscribe(_ => UpdateLocalizedStrings())
                .DisposeWith(disposables);

            UpdateLocalizedStrings();
        });
    }

    private void UpdateLocalizedStrings()
    {
        MenuFile = _localization.GetString("Menu.File");
        MenuView = _localization.GetString("Menu.View");
        MenuTrading = _localization.GetString("Menu.Trading");
        MenuAi = _localization.GetString("Menu.AI");
        
        MenuItemNewOrder = _localization.GetString("Menu.Trading.NewOrder");
        MenuItemPositions = _localization.GetString("Menu.Trading.Positions");
        MenuItemOrderHistory = _localization.GetString("Menu.Trading.OrderHistory");
        MenuItemAiChat = _localization.GetString("Menu.AI.Chat");
        MenuItemMcpServers = _localization.GetString("Menu.AI.Servers");
        MenuItemSettings = _localization.GetString("Menu.File.Settings");
        MenuItemExit = _localization.GetString("Menu.File.Exit");
    }

    private Task HandleNewOrderAsync()
    {
        // TODO: Implement new order dialog
        return Task.CompletedTask;
    }

    private Task HandleShowPositionsAsync()
    {
        // TODO: Implement positions view
        return Task.CompletedTask;
    }

    private Task HandleShowOrderHistoryAsync()
    {
        // TODO: Implement order history view
        return Task.CompletedTask;
    }

    private Task HandleOpenAiChatAsync()
    {
        // TODO: Implement AI chat
        return Task.CompletedTask;
    }

    private Task HandleManageMcpServersAsync()
    {
        // TODO: Implement MCP servers management
        return Task.CompletedTask;
    }

    private Task HandleOpenSettingsAsync()
    {
        // TODO: Implement settings dialog
        return Task.CompletedTask;
    }

    private void HandleExit()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lifetime)
        {
            lifetime.Shutdown();
        }
    }
}
