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

    /// <summary>Command to show the trade statistics window.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ShowTradeStatsCommand { get; }

    /// <summary>Command to show the notification center window.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ShowNotificationsCommand { get; }

    /// <summary>Command to open the offline backtest window (T-113).</summary>
    public ReactiveCommand<RxVoid, RxVoid> ShowBacktestCommand { get; }

    /// <summary>Command to open the price-alerts manager window (T-096).</summary>
    public ReactiveCommand<RxVoid, RxVoid> ShowAlertsCommand { get; }

    /// <summary>Command to toggle the AI chat panel column.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ToggleAiChatCommand { get; }

    /// <summary>Command to manage MCP servers.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ManageMcpServersCommand { get; }

    /// <summary>Command to open settings dialog.</summary>
    public ReactiveCommand<RxVoid, RxVoid> OpenSettingsCommand { get; }

    /// <summary>Command to show the first-run disclaimer dialog again (Help menu).</summary>
    public ReactiveCommand<RxVoid, RxVoid> ShowDisclaimerCommand { get; }

    /// <summary>Command to open the log viewer window (Help menu, T-101).</summary>
    public ReactiveCommand<RxVoid, RxVoid> ShowLogViewerCommand { get; }

    /// <summary>
    /// Interaction that routes the show-log-viewer request to the View,
    /// which hosts the modal window (same pattern as
    /// <see cref="ShowTradeStatsRequested"/>).
    /// </summary>
    public Interaction<RxVoid, RxVoid> ShowLogViewerRequested { get; }

    /// <summary>
    /// Interaction that routes the show-disclaimer request to the View, which
    /// hosts the dialog window (same pattern as <see cref="OpenSettingsRequested"/>).
    /// </summary>
    public Interaction<RxVoid, RxVoid> ShowDisclaimerRequested { get; }

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
    /// Interaction that routes the show-trade-statistics request to the
    /// View, which hosts the modal window (same pattern as
    /// <see cref="ShowOrderHistoryRequested"/>).
    /// </summary>
    public Interaction<RxVoid, RxVoid> ShowTradeStatsRequested { get; }

    /// <summary>
    /// Interaction that routes the show-notification-center request to the
    /// View, which hosts the modal window (same pattern as
    /// <see cref="ShowTradeStatsRequested"/>).
    /// </summary>
    public Interaction<RxVoid, RxVoid> ShowNotificationsRequested { get; }

    /// <summary>
    /// Interaction that routes the show-alerts request to the View, which
    /// hosts the modal manager window (same pattern as
    /// <see cref="ShowTradeStatsRequested"/>; T-096).
    /// </summary>
    public Interaction<RxVoid, RxVoid> ShowAlertsRequested { get; }

    /// <summary>
    /// Interaction that routes the show-balances request to the View, which
    /// hosts the modal panel (same pattern as
    /// <see cref="ShowPositionsRequested"/>).
    /// </summary>
    public Interaction<RxVoid, RxVoid> ShowBalancesRequested { get; }

    /// <summary>
    /// Interaction that routes the show-backtest request to the View, which
    /// hosts the modal window (same pattern as
    /// <see cref="ShowTradeStatsRequested"/>).
    /// </summary>
    public Interaction<RxVoid, RxVoid> ShowBacktestRequested { get; }

    /// <summary>Command to show balances panel.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ShowBalancesCommand { get; }

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
    /// <param name="hud">The performance HUD ViewModel (T-108): the menu's
    /// checkable item toggles the persisted overlay visibility and mirrors
    /// the applied state live (the same pattern the chat item keeps).</param>
    /// <param name="hotkeys">The hotkey manager ViewModel (T-102): the Help
    /// menu's checkable cheat-sheet item drives its own toggle command and
    /// mirrors the sheet's visibility; the advertised gestures the menu
    /// items print come from the same live map the window routes.</param>
    public MenuViewModel(ChatViewModel chat, PerformanceHudViewModel hud, HotkeyViewModel hotkeys)
    {
        _chat = chat;
        _hud = hud;
        Hotkeys = hotkeys;

        OpenSettingsRequested = new Interaction<RxVoid, RxVoid>();
        NewOrderRequested = new Interaction<RxVoid, RxVoid>();
        ShowPositionsRequested = new Interaction<RxVoid, RxVoid>();
        ShowOrderHistoryRequested = new Interaction<RxVoid, RxVoid>();
        ShowTradeStatsRequested = new Interaction<RxVoid, RxVoid>();
        ShowNotificationsRequested = new Interaction<RxVoid, RxVoid>();
        ShowAlertsRequested = new Interaction<RxVoid, RxVoid>();
        ShowBalancesRequested = new Interaction<RxVoid, RxVoid>();
        ShowBacktestRequested = new Interaction<RxVoid, RxVoid>();
        FocusAiChatRequested = new Interaction<RxVoid, RxVoid>();
        ShowDisclaimerRequested = new Interaction<RxVoid, RxVoid>();
        ShowLogViewerRequested = new Interaction<RxVoid, RxVoid>();
        NewOrderCommand = ReactiveCommand.CreateFromTask(HandleNewOrderAsync);
        ShowPositionsCommand = ReactiveCommand.CreateFromTask(HandleShowPositionsAsync);
        ShowOrderHistoryCommand = ReactiveCommand.CreateFromTask(HandleShowOrderHistoryAsync);
        ShowTradeStatsCommand = ReactiveCommand.CreateFromTask(HandleShowTradeStatsAsync);
        ShowNotificationsCommand = ReactiveCommand.CreateFromTask(HandleShowNotificationsAsync);
        ShowAlertsCommand = ReactiveCommand.CreateFromTask(HandleShowAlertsAsync);
        ShowBalancesCommand = ReactiveCommand.CreateFromTask(HandleShowBalancesAsync);
        ShowBacktestCommand = ReactiveCommand.CreateFromTask(HandleShowBacktestAsync);
        ToggleAiChatCommand = ReactiveCommand.CreateFromTask(HandleToggleAiChatAsync);
        ManageMcpServersCommand = ReactiveCommand.CreateFromTask(HandleManageMcpServersAsync);
        OpenSettingsCommand = ReactiveCommand.CreateFromTask(HandleOpenSettingsAsync);
        ShowDisclaimerCommand = ReactiveCommand.CreateFromTask(HandleShowDisclaimerAsync);
        ShowLogViewerCommand = ReactiveCommand.CreateFromTask(HandleShowLogViewerAsync);
        ExitCommand = ReactiveCommand.Create(HandleExit);

        // The checkable item mirrors the panel state: the when-any-value
        // subscription emits the current (already restored) value at once and
        // follows every flip. The subscription mirrors the app-lifetime
        // singletons (same contract as the menu interactions' handlers); a
        // property stream never errors, so a plain Subscribe suffices.
        chat.WhenAnyValue(x => x.IsAiChatVisible)
            .Subscribe(visible => IsAiChatVisible = visible);

        // The HUD item mirrors the overlay state the same way: the command
        // is the HUD ViewModel's own toggle (the menu only routes the
        // press), the checkbox reads the applied visibility OneWay.
        hud.WhenAnyValue(x => x.IsVisible)
            .Subscribe(visible => IsHudVisible = visible);
    }

    private readonly ChatViewModel _chat;

    private readonly PerformanceHudViewModel _hud;

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

    private bool _isHudVisible = UiSettingsKeys.DefaultHudEnabled;

    /// <summary>
    /// Checkable mirror of the performance HUD overlay visibility (T-108)
    /// for the menu item; driven by the HUD ViewModel the same OneWay way.
    /// </summary>
    public bool IsHudVisible
    {
        get => _isHudVisible;
        private set => this.RaiseAndSetIfChanged(ref _isHudVisible, value);
    }

    /// <summary>
    /// Toggle the performance HUD overlay (T-108): the command is the HUD
    /// ViewModel's own toggle — the menu item only routes the press to it,
    /// the persistence lives where the visibility does.
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid> ToggleHudCommand => _hud.ToggleCommand;

    /// <summary>
    /// The hotkey manager (T-102) the Help menu's cheat-sheet item reads:
    /// the checkable item's press routes the sheet toggle's own command and
    /// the checkbox mirrors the sheet's visibility OneWay — the same shape
    /// the HUD item keeps. The editor's gesture strings ride the same live
    /// map this instance serves.
    /// </summary>
    public HotkeyViewModel Hotkeys { get; }

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
    /// Route the show-trade-statistics request to the View (window plumbing
    /// stays out of the ViewModel, same as the order history window).
    /// </summary>
    private Task HandleShowTradeStatsAsync() => ShowTradeStatsRequested.Handle(RxVoid.Default);

    /// <summary>
    /// Route the show-notification-center request to the View (window
    /// plumbing stays out of the ViewModel, same as the stats window).
    /// </summary>
    private Task HandleShowNotificationsAsync() => ShowNotificationsRequested.Handle(RxVoid.Default);

    /// <summary>
    /// Route the show-alerts request to the View (window plumbing stays out
    /// of the ViewModel, same as the stats window; T-096).
    /// </summary>
    private Task HandleShowAlertsAsync() => ShowAlertsRequested.Handle(RxVoid.Default);

    /// <summary>
    /// Route the show-balances request to the View (window plumbing stays
    /// out of the ViewModel, same as the positions panel).
    /// </summary>
    private Task HandleShowBalancesAsync() => ShowBalancesRequested.Handle(RxVoid.Default);

    /// <summary>
    /// Route the show-backtest request to the View (window plumbing stays
    /// out of the ViewModel, same as the stats window).
    /// </summary>
    private Task HandleShowBacktestAsync() => ShowBacktestRequested.Handle(RxVoid.Default);

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

    /// <summary>
    /// Route the show-disclaimer request to the View (window plumbing stays
    /// out of the ViewModel, same as the settings window).
    /// </summary>
    private Task HandleShowDisclaimerAsync() => ShowDisclaimerRequested.Handle(RxVoid.Default);

    /// <summary>
    /// Route the show-log-viewer request to the View (window plumbing stays
    /// out of the ViewModel, same as the stats window).
    /// </summary>
    private Task HandleShowLogViewerAsync() => ShowLogViewerRequested.Handle(RxVoid.Default);

    private void HandleExit()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lifetime)
        {
            lifetime.Shutdown();
        }
    }

    #endregion Private Methods
}
