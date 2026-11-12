namespace ZettaTrade.App.ViewModels;

using System;
using System.Reactive.Linq;
using Livery.Extensions;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.App.Services;
// RxVoid rides an alias (not a plain ReactiveUI.Primitives import): plain both
// imports would make every RxVoid usage ambiguous against System's one.
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// Main window view model. Coordinates sub-ViewModels for menu, status bar, chat, and settings.
/// Follows Single Responsibility Principle by delegating concerns to specialized ViewModels.
/// </summary>
public sealed class MainViewModel : ReactiveObject
{
    /// <summary>How often the status chip re-reads the adapter connection state.</summary>
    private static readonly TimeSpan ConnectionPollInterval = TimeSpan.FromSeconds(2);

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

    /// <summary>Order book ViewModel for the depth panel.</summary>
    public OrderBookViewModel OrderBook { get; }

    /// <summary>Trade tape ViewModel for the time and sales panel.</summary>
    public TradeTapeViewModel TradeTape { get; }

    /// <summary>Watchlist panel ViewModel for the majors' live quotes row set (T-120).</summary>
    public WatchlistViewModel Watchlist { get; }

    /// <summary>Heatmap panel ViewModel for the change-colored market tiles (T-121).</summary>
    public HeatmapViewModel Heatmap { get; }

    /// <summary>Workspace layout ViewModel owning the persisted splitter column widths.</summary>
    public WorkspaceLayoutViewModel Layout { get; }

    /// <summary>The global symbol palette (T-095): Ctrl+K's instrument picker over all adapters.</summary>
    public SymbolPaletteViewModel Palette { get; }

    /// <summary>The notification toast host (T-097): the top-right stack the center's display lane feeds.</summary>
    public NotificationToastHostViewModel ToastHost { get; }

    /// <summary>The performance HUD overlay (T-108): the dev-mode readout the Help menu's checkbox toggles.</summary>
    public PerformanceHudViewModel Hud { get; }

    /// <summary>
    /// The hotkey manager (T-102): the live gesture map the main window's
    /// key route dispatches through, the menu's advertised gestures print
    /// and the cheat-sheet overlay mirrors — one seam, so advertised and
    /// real never diverge.
    /// </summary>
    public HotkeyViewModel Hotkeys { get; }

    /// <summary>
    /// Create a new MainViewModel instance.
    /// </summary>
    public MainViewModel(
        MenuViewModel menu,
        StatusBarViewModel statusBar,
        ChatViewModel chat,
        SettingsViewModel settings,
        ChartViewModel chart,
        OrderBookViewModel orderBook,
        TradeTapeViewModel tradeTape,
        WatchlistViewModel watchlist,
        HeatmapViewModel heatmap,
        WorkspaceLayoutViewModel layout,
        SymbolPaletteViewModel palette,
        NotificationToastHostViewModel toastHost,
        PerformanceHudViewModel hud,
        HotkeyViewModel hotkeys,
        ActiveExchangeService activeExchanges,
        ILogger<MainViewModel> logger)
    {
        Menu = menu;
        StatusBar = statusBar;
        Chat = chat;
        Settings = settings;
        Chart = chart;
        OrderBook = orderBook;
        TradeTape = tradeTape;
        Watchlist = watchlist;
        Heatmap = heatmap;
        Layout = layout;
        Palette = palette;
        ToastHost = toastHost;
        Hud = hud;
        Hotkeys = hotkeys;
        _logger = logger;

        // The status chip mirrors the adapter connection state. The Core
        // contract is a property (no state stream), so an App-owned poll
        // bridges it. The poll follows the active exchange: the behavioral
        // AdapterChanges re-switches the whole pipeline, so a switch reports
        // the new adapter's state immediately (deferred first value) and
        // then holds the ConnectionPollInterval cadence on it;
        // DistinctUntilChanged keeps the chip text stable unless the state
        // flips. The ViewModel lives as long as the application (composes
        // singleton sub-ViewModels), so this subscription mirrors it and
        // needs no explicit disposal.
        activeExchanges
            .AdapterChanges
            .Select(adapter => Observable
                .Return(adapter.IsConnected)
                .Concat(Observable
                    .Interval(ConnectionPollInterval)
                    .Select(_ => adapter.IsConnected)))
            .Switch()
            .DistinctUntilChanged()
            .ObserveOnUiThread()
            .Subscribe(
                StatusBar.UpdateConnectionStatus,
                ex => _logger.LogError(ex, "The connection status poll stopped unexpectedly"));

        _logger.LogInformation("MainViewModel initialized with sub-ViewModels");
    }

    /// <summary>
    /// Route one hotkey action id (T-102); the main window's key handler is
    /// the only caller. The switch carries the registry's exact ids — an
    /// unknown id (a retired binding whose stored override survived) is an
    /// honestly refused route: it faults through the log and dispatches
    /// nothing, never a substitute action.
    /// </summary>
    /// <param name="actionId">The pressed gesture's bound action id.</param>
    public void RouteHotkey(string actionId)
    {
        switch (actionId)
        {
            case HotkeyRegistry.OpenSettings:
                DispatchHotkey(Menu.OpenSettingsCommand, actionId);
                break;
            case HotkeyRegistry.NewOrder:
                DispatchHotkey(Menu.NewOrderCommand, actionId);
                break;
            case HotkeyRegistry.KillSwitch:
                DispatchHotkey(Menu.KillSwitchCommand, actionId);
                break;
            case HotkeyRegistry.ToggleAiChat:
                DispatchHotkey(Menu.ToggleAiChatCommand, actionId);
                break;
            case HotkeyRegistry.TogglePalette:
                DispatchHotkey(Palette.ToggleCommand, actionId);
                break;
            default:
                _logger.LogError(
                    "Unknown hotkey action {ActionId}: the route refuses and dispatches nothing",
                    actionId);
                break;
        }
    }

    /// <summary>
    /// Execute one hotkey's target command with its error handled — the
    /// keyboard route never goes fire-and-forget (the overlay command
    /// dispatch discipline, lifted into the coordinating ViewModel).
    /// </summary>
    /// <param name="command">The action's target command.</param>
    /// <param name="actionId">The action id (the failure log's context).</param>
    private void DispatchHotkey(ReactiveCommand<RxVoid, RxVoid> command, string actionId) =>
        command.Execute(RxVoid.Default).Subscribe(
            _ => { },
            ex => _logger.LogError(ex, "The hotkey action {ActionId} failed", actionId));
}
