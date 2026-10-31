namespace ZettaTrade.App.ViewModels;

using ReactiveUI;
using ReactiveUI.Primitives;

/// <summary>
/// ViewModel for menu bar with commands.
/// Localization is handled directly in XAML via {loc:Localized Key}.
/// </summary>
public sealed class MenuViewModel : ReactiveObject
{
    #region Commands

    /// <summary>Command to open new order dialog.</summary>
    public ReactiveCommand<RxVoid, RxVoid> NewOrderCommand { get; }

    /// <summary>Command to show positions view.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ShowPositionsCommand { get; }

    /// <summary>Command to show order history view.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ShowOrderHistoryCommand { get; }

    /// <summary>Command to open AI chat.</summary>
    public ReactiveCommand<RxVoid, RxVoid> OpenAiChatCommand { get; }

    /// <summary>Command to manage MCP servers.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ManageMcpServersCommand { get; }

    /// <summary>Command to open settings dialog.</summary>
    public ReactiveCommand<RxVoid, RxVoid> OpenSettingsCommand { get; }

    /// <summary>
    /// Interaction that routes the open-settings request to the View, which
    /// hosts the actual window (window plumbing stays out of the ViewModel).
    /// </summary>
    public Interaction<RxVoid, RxVoid> OpenSettingsRequested { get; }

    /// <summary>
    /// Interaction that routes the new-order request to the View, which hosts
    /// the dialog window (same pattern as <see cref="OpenSettingsRequested"/>).
    /// </summary>
    public Interaction<RxVoid, RxVoid> NewOrderRequested { get; }

    /// <summary>Command to exit application.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ExitCommand { get; }

    #endregion Commands

    #region Constructor

    /// <summary>
    /// Create a new MenuViewModel instance.
    /// </summary>
    public MenuViewModel()
    {
        OpenSettingsRequested = new Interaction<RxVoid, RxVoid>();
        NewOrderRequested = new Interaction<RxVoid, RxVoid>();
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

    /// <summary>
    /// Route the new-order request to the View (window plumbing stays out of
    /// the ViewModel, same as the settings window).
    /// </summary>
    private Task HandleNewOrderAsync() => NewOrderRequested.Handle(RxVoid.Default);

    private Task HandleShowPositionsAsync() => Task.CompletedTask;

    private Task HandleShowOrderHistoryAsync() => Task.CompletedTask;

    private Task HandleOpenAiChatAsync() => Task.CompletedTask;

    private Task HandleManageMcpServersAsync() => Task.CompletedTask;

    private Task HandleOpenSettingsAsync() => OpenSettingsRequested.Handle(RxVoid.Default);

    private void HandleExit()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lifetime)
        {
            lifetime.Shutdown();
        }
    }

    #endregion Private Methods
}
