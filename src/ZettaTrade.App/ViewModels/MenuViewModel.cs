namespace ZettaTrade.App.ViewModels;

using ReactiveUI;

/// <summary>
/// ViewModel for menu bar with commands.
/// Localization is handled directly in XAML via {loc:Localized Key}.
/// </summary>
public sealed class MenuViewModel : ReactiveObject
{
    #region Commands

    /// <summary>Command to open new order dialog.</summary>
    public ReactiveCommand<Unit, RxVoid> NewOrderCommand { get; }
    
    /// <summary>Command to show positions view.</summary>
    public ReactiveCommand<Unit, RxVoid> ShowPositionsCommand { get; }
    
    /// <summary>Command to show order history view.</summary>
    public ReactiveCommand<Unit, RxVoid> ShowOrderHistoryCommand { get; }
    
    /// <summary>Command to open AI chat.</summary>
    public ReactiveCommand<Unit, RxVoid> OpenAiChatCommand { get; }
    
    /// <summary>Command to manage MCP servers.</summary>
    public ReactiveCommand<Unit, RxVoid> ManageMcpServersCommand { get; }
    
    /// <summary>Command to open settings dialog.</summary>
    public ReactiveCommand<Unit, RxVoid> OpenSettingsCommand { get; }
    
    /// <summary>Command to exit application.</summary>
    public ReactiveCommand<Unit, RxVoid> ExitCommand { get; }

    #endregion Commands

    #region Constructor

    /// <summary>
    /// Create a new MenuViewModel instance.
    /// </summary>
    public MenuViewModel()
    {
        NewOrderCommand = ReactiveCommand.CreateFromTask(HandleNewOrderAsync);
        ShowPositionsCommand = ReactiveCommand.CreateFromTask(HandleShowPositionsAsync);
        ShowOrderHistoryCommand = ReactiveCommand.CreateFromTask(HandleShowOrderHistoryAsync);
        OpenAiChatCommand = ReactiveCommand.CreateFromTask(HandleOpenAiChatAsync);
        ManageMcpServersCommand = ReactiveCommand.CreateFromTask(HandleManageMcpServersAsync);
        OpenSettingsCommand = ReactiveCommand.CreateFromTask(HandleOpenSettingsAsync);
        ExitCommand = ReactiveCommand.Create(HandleExit);
    }

    #endregion Constructor

    #region Private Methods

    private Task HandleNewOrderAsync() => Task.CompletedTask;
    
    private Task HandleShowPositionsAsync() => Task.CompletedTask;
    
    private Task HandleShowOrderHistoryAsync() => Task.CompletedTask;
    
    private Task HandleOpenAiChatAsync() => Task.CompletedTask;
    
    private Task HandleManageMcpServersAsync() => Task.CompletedTask;
    
    private Task HandleOpenSettingsAsync() => Task.CompletedTask;

    private void HandleExit()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lifetime)
        {
            lifetime.Shutdown();
        }
    }

    #endregion Private Methods
}
