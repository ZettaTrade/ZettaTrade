namespace ZettaTrade.App.ViewModels;

using System;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.UI.Extensions;

/// <summary>
/// Main window view model. Coordinates sub-ViewModels for menu, status bar, chat, and settings.
/// Follows Single Responsibility Principle by delegating concerns to specialized ViewModels.
/// </summary>
public sealed class MainViewModel : ReactiveObject
{
    /// <summary>How often the status chip re-reads the adapter connection state.</summary>
    private static readonly TimeSpan ConnectionPollInterval = TimeSpan.FromSeconds(2);

    private readonly ILogger<MainViewModel> _logger;
    private readonly IMarketDataAdapter _marketData;

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
        IMarketDataAdapter marketData,
        ILogger<MainViewModel> logger)
    {
        Menu = menu;
        StatusBar = statusBar;
        Chat = chat;
        Settings = settings;
        Chart = chart;
        OrderBook = orderBook;
        _logger = logger;
        _marketData = marketData;

        // The status chip mirrors the adapter connection state. The Core
        // contract is a property (no state stream), so an App-owned poll
        // bridges it — the chip is at most ConnectionPollInterval stale,
        // which is acceptable for a status indicator. The ViewModel lives as
        // long as the application (composes singleton sub-ViewModels), so
        // this subscription mirrors it and needs no explicit disposal.
        Observable
            .Return(marketData.IsConnected)
            .Concat(Observable
                .Interval(ConnectionPollInterval)
                .Select(_ => _marketData.IsConnected))
            .DistinctUntilChanged()
            .ObserveOnUiThread()
            .Subscribe(
                StatusBar.UpdateConnectionStatus,
                ex => _logger.LogError(ex, "The connection status poll stopped unexpectedly"));

        _logger.LogInformation("MainViewModel initialized with sub-ViewModels");
    }
}
