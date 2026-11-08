namespace ZettaTrade.App.ViewModels;

using System;
using System.Reactive.Linq;
using Livery.Extensions;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.App.Services;

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

    /// <summary>Workspace layout ViewModel owning the persisted splitter column widths.</summary>
    public WorkspaceLayoutViewModel Layout { get; }

    /// <summary>The global symbol palette (T-095): Ctrl+K's instrument picker over all adapters.</summary>
    public SymbolPaletteViewModel Palette { get; }

    /// <summary>The notification toast host (T-097): the top-right stack the center's display lane feeds.</summary>
    public NotificationToastHostViewModel ToastHost { get; }

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
        WorkspaceLayoutViewModel layout,
        SymbolPaletteViewModel palette,
        NotificationToastHostViewModel toastHost,
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
        Layout = layout;
        Palette = palette;
        ToastHost = toastHost;
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
}
