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

    /// <summary>Command to toggle the AI chat panel column.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ToggleAiChatCommand { get; }

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
    /// Interaction that routes the show-positions request to the View, which
    /// hosts the modal panel (same pattern as <see cref="NewOrderRequested"/>).
    /// </summary>
    public Interaction<RxVoid, RxVoid> ShowPositionsRequested { get; }

    /// <summary>
    /// Interaction that routes the show-order-history request to the View,
    /// which hosts the modal window (same pattern as
    /// <see cref="ShowPositionsRequested"/>).
    /// </summary>
    public Interaction<RxVoid, RxVoid> ShowOrderHistoryRequested { get; }

    /// <summary>
    /// Interaction that routes the focus-AI-chat request to the View, which
    /// focuses the assistant panel input. Fired only when the toggle SHOWS
    /// the panel: a hide needs no View cooperation (same pattern as
    /// <see cref="NewOrderRequested"/>).
    /// </summary>
    public Interaction<RxVoid, RxVoid> FocusAiChatRequested { get; }

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
    /// <param name="chat">The AI chat ViewModel: the toggle flips its
    /// persisted visibility and the checkable menu item mirrors the applied
    /// state live (the panel state lives where the column binds it).</param>
    public MenuViewModel(ChatViewModel chat)
    {
        _chat = chat;

        OpenSettingsRequested = new Interaction<RxVoid, RxVoid>();
        NewOrderRequested = new Interaction<RxVoid, RxVoid>();
        ShowPositionsRequested = new Interaction<RxVoid, RxVoid>();
        ShowOrderHistoryRequested = new Interaction<RxVoid, RxVoid>();
        FocusAiChatRequested = new Interaction<RxVoid, RxVoid>();
        NewOrderCommand = ReactiveCommand.CreateFromTask(HandleNewOrderAsync);
        ShowPositionsCommand = ReactiveCommand.CreateFromTask(HandleShowPositionsAsync);
        ShowOrderHistoryCommand = ReactiveCommand.CreateFromTask(HandleShowOrderHistoryAsync);
        ToggleAiChatCommand = ReactiveCommand.CreateFromTask(HandleToggleAiChatAsync);
        ManageMcpServersCommand = ReactiveCommand.CreateFromTask(HandleManageMcpServersAsync);
        OpenSettingsCommand = ReactiveCommand.CreateFromTask(HandleOpenSettingsAsync);
        ExitCommand = ReactiveCommand.Create(HandleExit);

        // The checkable item mirrors the panel state: the when-any-value
        // subscription emits the current (already restored) value at once and
        // follows every flip. The subscription mirrors the app-lifetime
        // singletons (same contract as the menu interactions' handlers); a
        // property stream never errors, so a plain Subscribe suffices.
        chat.WhenAnyValue(x => x.IsAiChatVisible)
            .Subscribe(visible => IsAiChatVisible = visible);
    }

    private readonly ChatViewModel _chat;

    private bool _isAiChatVisible = UiSettingsKeys.DefaultShowAiPanel;

    /// <summary>
    /// Checkable mirror of the AI chat panel visibility for the menu item;
    /// driven by the chat ViewModel (OneWay in XAML — the command owns the
    /// flips, the mirror never writes back).
    /// </summary>
    public bool IsAiChatVisible
    {
        get => _isAiChatVisible;
        private set => this.RaiseAndSetIfChanged(ref _isAiChatVisible, value);
    }

    #endregion Constructor

    #region Private Methods

    /// <summary>
    /// Route the new-order request to the View (window plumbing stays out of
    /// the ViewModel, same as the settings window).
    /// </summary>
    private Task HandleNewOrderAsync() => NewOrderRequested.Handle(RxVoid.Default);

    /// <summary>
    /// Route the show-positions request to the View (window plumbing stays
    /// out of the ViewModel, same as the new-order dialog).
    /// </summary>
    private Task HandleShowPositionsAsync() => ShowPositionsRequested.Handle(RxVoid.Default);

    /// <summary>
    /// Route the show-order-history request to the View (window plumbing
    /// stays out of the ViewModel, same as the positions panel).
    /// </summary>
    private Task HandleShowOrderHistoryAsync() => ShowOrderHistoryRequested.Handle(RxVoid.Default);

    /// <summary>
    /// Toggle the assistant panel; when the toggle SHOWS it, route the
    /// focus request to the View (the input becomes reachable only now).
    /// Hiding needs no View cooperation.
    /// </summary>
    private async Task HandleToggleAiChatAsync()
    {
        var becameVisible = await _chat.ToggleAsync();

        if (becameVisible)
        {
            await FocusAiChatRequested.Handle(RxVoid.Default);
        }
    }

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
