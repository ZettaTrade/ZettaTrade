namespace ZettaTrade.App;

using Livery.Contracts;
using Livery.Localization;
using Livery.Themes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using ZettaTrade.AI.Chat;
using ZettaTrade.AI.Mcp;
using ZettaTrade.AI.Mcp.Tools;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Charting;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Configuration;
using ZettaTrade.Infrastructure.Exchange;
using ZettaTrade.Infrastructure.Export;
using ZettaTrade.Infrastructure.Indicators;
using ZettaTrade.Infrastructure.Persistence;
using ZettaTrade.Infrastructure.Secrets;
using ZettaTrade.Infrastructure.Shell;
using ZettaTrade.Strategies;

/// <summary>
/// DI composition root. Registers all services and dependencies.
/// </summary>
internal static class CompositionRoot
{
    /// <summary>
    /// Build and configure the service provider, initializing all services
    /// that implement <see cref="IAsyncInitializable"/>.
    /// Initialization resolves instances by their registered service types,
    /// which is how App-layer registrations declare them.
    /// </summary>
    public static async Task<IServiceProvider> BuildServiceProviderAsync(CancellationToken ct = default)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Avalonia", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: Path.Combine(ConfigPathResolver.LogDirectory, "zettatrade-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(Log.Logger, dispose: true);
        });

        services.AddSingleton<ISecretStore>(sp =>
            SecretStoreFactory.Create(sp.GetRequiredService<ILoggerFactory>()));

        services.AddSettingsStoreServices(); // T-137: one concrete store singleton, forwarded as ISettingsService — the port resolves.
        services.AddSettingsPortServices(); // T-100: settings import/export + factory reset over the store above.

        // ThemeManager is registered both by its concrete type (the read-only
        // diagnostics surface — LoadErrors/LoadErrorsChanged — lives there,
        // not on the Core interface) and as the IThemeManager service.
        services.AddSingleton<ThemeManager>(sp =>
            new ThemeManager(
                sp.GetRequiredService<ILogger<ThemeManager>>(),
                ConfigPathResolver.ThemesDirectory));
        services.AddSingleton<IThemeManager>(sp => sp.GetRequiredService<ThemeManager>());

        services.AddSingleton<IShellUtils>(new ShellUtils());

        services.AddSingleton<ILocalizationManager>(sp =>
            new LocalizationManager(
                sp.GetRequiredService<ILogger<LocalizationManager>>(),
                ZettaTrade.UI.Resources.Strings.ResourceManager));

        // Exchange adapters: transport settings live inside each adapter module (Infrastructure).
        services.AddExchangeAdapters();

        // The indicator plugin seam: one catalog scans the plugins directory
        // for IIndicator assemblies (SMA ships as the first plugin; the App
        // carries no compile-time reference to any plugin).
        services.AddIndicators();

        // The strategy plugin seam (T-111, hosted by T-112): one catalog
        // scans the same plugins directory for IStrategy assemblies. The
        // signals channel stays inert output — consumers prefill the ticket,
        // the human decides.
        services.AddStrategies(ConfigPathResolver.PluginsDirectory);

        // Active-exchange facade over the concrete adapters: every market
        // data consumer resolves one IMarketDataAdapter that forwards to
        // the exchange picked in the toolbar selector, and trading rides the
        // same context (paper over the facade). Typed + IAsyncInitializable:
        // the startup walk restores the persisted choice here — before any
        // consumer VM is constructed.
        services.AddActiveExchange();

        // Venue trading surface (T-127): the Bybit testnet transport + the
        // delegating ITradingAdapter + the venue order ledger. Registered
        // AFTER the facade (its adapters resolve behind the surface) and
        // BEFORE the trading-mode service (the walk initializes the surface,
        // then the lane resolver reads its standing value). The toggle
        // (Exchange/TestnetTradingEnabled, default off) is the only arming
        // seam — nothing trades a real venue without it.
        services.AddTestnetTradingServices();

        // Adapter health fold (T-098): the status bar's dot strip folds the
        // facade's adapter catalog (populated by the construction above,
        // which binds the adapters' reconnect policies to the supervisor)
        // with the supervisor's transport state stream. Registered AFTER
        // the facade and BEFORE every consumer VM; the fold itself only
        // subscribes when the status bar VM resolves it.
        services.AddHealthUiServices();

        // Trading mode (T-123): the lane service behind the status-bar chip.
        // Registered after the exchange adapters (the resolver reads the
        // endpoint registry they initialize) and before every consumer VM;
        // the traded surface resolves from the active-exchange wiring's
        // paper registration and the prompt gate from the confirmation
        // service below.
        services.AddTradingModeServices();

        services.AddBalancesServices(); // T-081: balances panel (aggregation service + panel VM).

        // AI chat: the Ollama completion service (local models first) and its
        // named HttpClient; the workspace context rides the existing seams.
        services.AddAiServices();

        services.AddSingleton<IMcpClient>(sp =>
            new McpClientService(
                sp.GetRequiredService<ILogger<McpClientService>>(),
                sp.GetRequiredService<ISecretStore>()));
        services.AddSingleton<IMcpServerHost, McpServerHost>();

        // MCP host runner: the startup walk reads the enable choice and
        // starts the stdio server per it; the settings window's save applies
        // the flip live and the state stream renders the honest rows. Registered
        // AFTER the settings service (the walk initializes that first) and
        // BEFORE every consumer VM.
        services.AddSingleton<McpHostService>();

        // External MCP client manager (T-068): the startup walk reads the
        // persisted server list and connects the enabled entries (a dead
        // server pends behind the bounded grace, never the boot). Registered
        // AFTER the settings service (the walk initializes that first) and
        // BEFORE the settings VM, which saves through it and renders its
        // per-entry states. The typed registration walks the walk; the
        // interface aliased to the same instance.
        services.AddSingleton<McpClientManager>();
        services.AddSingleton<IMcpClientManager>(sp =>
            sp.GetRequiredService<McpClientManager>());

        services.AddSingleton<IRiskManager, DefaultRiskManager>();
        services.AddSingleton<IConfirmationService>(sp => new UiConfirmationService(
            sp.GetRequiredService<ILogger<UiConfirmationService>>(),
            sp.GetRequiredService<ILocalizationManager>()));
        services.AddSingleton<TradingToolsRegistrar>();

        // First-run disclaimer: the acknowledged-version flag rides the
        // settings store (read fresh at every launch, never cached), and the
        // dialog view model is transient per open.
        services.AddDisclaimerServices();

        // First-run wizard (T-099): the once-flag rides the settings store
        // the same way; the wizard view model is transient per open.
        services.AddFirstRunWizardServices();

        // Log viewer + global crash guard (T-101): the tail ring and the
        // guard are process-wide singletons; the viewer's view model is
        // transient per open.
        services.AddLogViewerServices();

        services.AddSingleton<MenuViewModel>();
        services.AddSingleton<StatusBarViewModel>(sp =>
            new StatusBarViewModel(
                sp.GetRequiredService<IThemeManager>(),
                sp.GetRequiredService<ILocalizationManager>(),
                sp.GetRequiredService<ActiveExchangeService>(),
                sp.GetRequiredService<TradingModeService>(),
                AppVersion.Current,
                sp.GetRequiredService<AdapterHealthService>().HealthStream,
                logger: sp.GetRequiredService<ILogger<StatusBarViewModel>>()));
        services.AddSingleton<MarketContextPreambleComposer>();
        services.AddSingleton<ChatContextService>(sp =>
            new ChatContextService(
                sp.GetRequiredService<IMarketDataAdapter>(),
                sp.GetRequiredService<ChartViewModel>().SymbolSelections,
                sp.GetRequiredService<ChartViewModel>().TimeFrameSelections,
                sp.GetRequiredService<ILogger<ChatContextService>>(),
                // T-114: the venue actually trading (the paper/live tag) and
                // the settings the enabled indicator setup rides in.
                sp.GetRequiredService<ITradingAdapter>(),
                sp.GetRequiredService<ISettingsService>()));
        services.AddSingleton<WorkspacePromptComposer>();
        services.AddSingleton<ChatViewModel>(sp =>
            new ChatViewModel(
                sp.GetRequiredService<ISettingsService>(),
                sp.GetRequiredService<IChatCompletion>(),
                sp.GetRequiredService<ChatContextService>(),
                sp.GetRequiredService<WorkspacePromptComposer>(),
                sp.GetRequiredService<ILocalizationManager>(),
                sp.GetRequiredService<ILogger<ChatViewModel>>(),
                sp.GetRequiredService<MarketContextPreambleComposer>()));
        // Workspace layout: the persisted column widths of the draggable
        // splitters. Registered AFTER ChatViewModel on purpose — the startup
        // walk runs registrations in order, and the layout restore must
        // follow the chat's visibility restore so the collapsed/visible
        // assistant geometry applies before any window opens (T-038).
        services.AddSingleton<WorkspaceLayoutViewModel>(sp =>
            new WorkspaceLayoutViewModel(
                sp.GetRequiredService<ISettingsService>(),
                sp.GetRequiredService<ChatViewModel>(),
                sp.GetRequiredService<ILogger<WorkspaceLayoutViewModel>>()));
        services.AddSingleton<SettingsViewModel>(sp =>
            new SettingsViewModel(
                sp.GetRequiredService<ISettingsService>(),
                sp.GetRequiredService<IThemeManager>(),
                sp.GetRequiredService<ILocalizationManager>(),
                sp.GetRequiredService<IShellUtils>(),
                sp.GetRequiredService<McpHostService>(),
                sp.GetRequiredService<ActiveExchangeService>(),
                sp.GetRequiredService<SymbolListProvider>(),
                sp.GetRequiredService<IIndicatorCatalog>(),
                sp.GetRequiredService<IMcpClientManager>(),
                sp.GetRequiredService<ILogger<SettingsViewModel>>(),
                sp.GetRequiredService<ThemeManager>().LoadErrorsChanged,
                sp.GetRequiredService<SettingsPortService>(),
                sp.GetRequiredService<IConfirmationService>()));
        // The strategy-seam hosting (T-112): the composition owns the
        // series-load relay so the chart and the strategy service depend on
        // the relay, never on each other's construction (the cycle-free
        // shape the clean-architecture edges keep). The chart's full loads
        // feed the candle-close host, the pump runs for the application
        // lifetime and the signals broadcast inert output (the markers and
        // the rail consume; nothing routes a signal into an order).
        services.AddSingleton<SignalSeriesRelay>();
        services.AddSingleton<StrategySignalService>(sp =>
            new StrategySignalService(
                sp.GetRequiredService<SignalSeriesRelay>().Pages,
                sp.GetRequiredService<IStrategyCatalog>(),
                sp.GetRequiredService<ILoggerFactory>()));

        // The signal rail (T-112): the coalescing streaming panel. The
        // chart exposes it as SignalRail for the workspace strip, the
        // window owns the row-press route.
        services.AddSingleton<SignalRailViewModel>(sp =>
            new SignalRailViewModel(
                sp.GetRequiredService<StrategySignalService>().Signals,
                sp.GetRequiredService<ILocalizationManager>(),
                sp.GetRequiredService<ILogger<SignalRailViewModel>>()));

        services.AddSingleton<ChartViewModel>(sp =>
            new ChartViewModel(
                sp.GetRequiredService<ActiveExchangeService>(),
                sp.GetRequiredService<SymbolListProvider>(),
                sp.GetRequiredService<ISettingsService>(),
                sp.GetRequiredService<IIndicatorCatalog>(),
                sp.GetRequiredService<ICsvExportService>(),
                sp.GetRequiredService<ILocalizationManager>(),
                sp.GetRequiredService<ICandleCache>(), // T-085: the bootstrap page serves the cache first.
                sp.GetRequiredService<CandleBackfillService>(), // T-086: the repair chain's fetcher.
                sp.GetRequiredService<StrategySignalService>(), // T-112: the stances the markers read.
                sp.GetRequiredService<SignalRailViewModel>(), // T-112: the rail strip's VM.
                sp.GetRequiredService<SignalSeriesRelay>(), // T-112: the loads the host feeds from.
                                                            // #region T-122: the footprint feed's stream (my-activity overlays).
                sp.GetRequiredService<ChartFootprintService>().Updates,
                // #endregion T-122
                sp.GetRequiredService<ILogger<ChartViewModel>>()));
        services.AddSingleton<OrderBookViewModel>(sp =>
            new OrderBookViewModel(
                sp.GetRequiredService<IMarketDataAdapter>(),
                sp.GetRequiredService<ChartViewModel>().SymbolSelections,
                sp.GetRequiredService<ILogger<OrderBookViewModel>>()));
        services.AddSingleton<TradeTapeViewModel>(sp =>
            new TradeTapeViewModel(
                sp.GetRequiredService<IMarketDataAdapter>(),
                sp.GetRequiredService<ChartViewModel>().SymbolSelections,
                sp.GetRequiredService<ILogger<TradeTapeViewModel>>()));
        // T-108: the performance HUD — the sampler + its overlay ViewModel.
        // The service taps the book ViewModel's own stream (the rate and the
        // WS-latency stamps), the log tail's bounded channel (the
        // backpressure gauge) and — via the view glue at the window's open —
        // the chart's frame tap. Registered as singletons AFTER the book
        // ViewModel they read; the sampler builds no subscription and no
        // timer until its ViewModel's visibility asks for it.
        services.AddSingleton<PerformanceHudService>(sp =>
            new PerformanceHudService(
                sp.GetRequiredService<OrderBookViewModel>().Updates,
                () => sp.GetRequiredService<LogTailService>().BackpressureBacklog,
                LogTailService.BackpressureChannelCapacity,
                sp.GetRequiredService<ILogger<PerformanceHudService>>()));
        services.AddSingleton<PerformanceHudViewModel>();
        services.AddWatchlistServices(); // T-120: the watchlist panel (feed + rows) over the same facade/selection pair above.
        services.AddHeatmapServices(); // T-121: the heatmap panel (candidate pumps + ranked tile page) beside the watchlist rail above.
        // T-095: the global symbol palette — the Ctrl+K instrument picker
        // over ALL registered adapters. Both singletons, registered after
        // the chart ViewModel it applies into and before MainViewModel;
        // the source rides IAsyncInitializable, so the startup walk warms
        // the instrument snapshot before any window opens.
        services.AddPaletteServices();
        services.AddHotkeyServices(); // T-102: the live hotkey map + the shared editor/cheat-sheet ViewModel (before MainViewModel resolves below).
        services.AddTransient<MainViewModel>(sp =>
            new MainViewModel(
                sp.GetRequiredService<MenuViewModel>(),
                sp.GetRequiredService<StatusBarViewModel>(),
                sp.GetRequiredService<ChatViewModel>(),
                sp.GetRequiredService<SettingsViewModel>(),
                sp.GetRequiredService<ChartViewModel>(),
                sp.GetRequiredService<OrderBookViewModel>(),
                sp.GetRequiredService<TradeTapeViewModel>(),
                sp.GetRequiredService<WatchlistViewModel>(),
                sp.GetRequiredService<HeatmapViewModel>(), // T-121: the right rail's heatmap tab.
                sp.GetRequiredService<WorkspaceLayoutViewModel>(),
                sp.GetRequiredService<SymbolPaletteViewModel>(),
                sp.GetRequiredService<NotificationToastHostViewModel>(),
                sp.GetRequiredService<PerformanceHudViewModel>(), // T-108: the Help menu's checkable dev-mode readout.
                sp.GetRequiredService<HotkeyViewModel>(), // T-102: the live hotkey map the key route, the menu gestures, the cheat sheet and the settings tab read.
                sp.GetRequiredService<ActiveExchangeService>(),
                sp.GetRequiredService<ILogger<MainViewModel>>()));
        services.AddTradingUiServices();
        services.AddOrderAmendServices();
        services.AddOrderHistory();
        services.AddNotificationServices(); // T-097: the notification center (toasts, history, DND) over the journal + health taps above.

        // The PnL accounting core + its app-side feed (T-082): after the
        // order history registration, so the walk initializes the ledger
        // store before the engine's boot replay reads it.
        services.AddPnlServices();

        #region T-122: the chart footprint feed (my-activity overlays on the chart)
        services.AddChartFootprintServices(); // after the journals + PnL feed: the boot read wants them initialized first.
        #endregion

        // The local candle cache (T-085): after the settings registration
        // it rides, so the walk loads the Caching policy before the chart's
        // bootstrap can serve a first page from the cache.
        services.AddCandleCache();
        services.AddCandleBackfillServices(); // T-086: the deep-history fetcher (bounded channel, write-through).
        services.AddHistoryUiServices();
        services.AddTradeStatsServices();
        services.AddCsvExportServices();
        services.AddPositionActionsServices(); // T-077: position actions (close, partial close, reverse).
        services.AddBulkCancelServices(); // T-076: bulk cancel (per-symbol, confirmed, best-effort).
        services.AddFeeServices(); // T-080: fees (schedule, estimate, columns).
        services.AddBacktestServices(); // T-113: the offline deterministic backtest harness + the run VM.
        services.AddAlertsServices(); // T-096: the price-alerts engine (roster solo owner) + the manager VM.
        services.AddSingleton<McpJournalingTradingAdapter>(); // T-133: MCP fills enter the journal.
        services.AddPositionSizerServices(); // T-079: position sizer (balance presets, risk-percent).
        services.AddSingleInstanceServices(); // T-104: the single-instance handoff host + the workspace-symbol apply worker.

        var provider = services.BuildServiceProvider();

        await InitializeRegisteredServicesAsync(provider, services, ct);

        // MCP trading tools: registered once, against the paper trading adapter
        // (mock surface; real exchange adapters are plugged in as plugins later).
        // Registration runs AFTER the startup walk: resolving the tool lane's
        // journaling wrapper constructs the whole adapter stack behind it
        // (the wrapper rides the registered ITradingAdapter paper adapter),
        // and the Binance adapter reads its endpoints from the endpoint
        // registry at construction time — the walk must have loaded the
        // settings file first, or every adapter would silently run on the
        // shipped production endpoints. The wrapper (not the raw adapter)
        // is what the tools execute against: a tool-placed order then lands
        // in the same session and SQLite journals the panels write (T-133).
        provider.GetRequiredService<TradingToolsRegistrar>().RegisterAll(
            provider.GetRequiredService<IMcpServerHost>(),
            provider.GetRequiredService<McpJournalingTradingAdapter>(),
            provider.GetRequiredService<IRiskManager>(),
            provider.GetRequiredService<IConfirmationService>(),
            provider.GetRequiredService<ILocalizationManager>());

        return provider;
    }

    /// <summary>
    /// Initialize every registered service instance implementing
    /// <see cref="IAsyncInitializable"/>, in registration order, once per instance.
    /// </summary>
    private static async Task InitializeRegisteredServicesAsync(
        IServiceProvider provider,
        IServiceCollection registrations,
        CancellationToken ct)
    {
        var initialized = new HashSet<object>(ReferenceEqualityComparer.Instance);

        foreach (var descriptor in registrations)
        {
            if (!IsStartupWalkCandidate(descriptor))
            {
                continue;
            }

            if (provider.GetService(descriptor.ServiceType) is not IAsyncInitializable service)
            {
                continue;
            }

            if (!initialized.Add(service))
            {
                continue;
            }

            await service.InitializeAsync(ct);
        }
    }

    /// <summary>
    /// Static (resolution-free) screen for the startup walk: decides whether
    /// a registration descriptor can host an <see cref="IAsyncInitializable"/>
    /// service before anything is resolved.
    /// </summary>
    /// <remarks>
    /// Typed registrations are inspected through static interface
    /// assignability: a <see cref="System.Type"/> object never pattern-matches
    /// a service interface, so a naive `type is IAsyncInitializable` check
    /// silently skipped every typed registration (the endpoint registry could
    /// never initialize). Factory registrations carry no implementation hint
    /// and are inspected dynamically by the walk; instance registrations
    /// carry the instance itself. Open-generic registrations (e.g. Options)
    /// cannot be resolved directly and are never candidates.
    /// </remarks>
    internal static bool IsStartupWalkCandidate(ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationType is { } typed &&
            !typeof(IAsyncInitializable).IsAssignableFrom(typed))
        {
            return false;
        }

        if (descriptor.ImplementationInstance is { } instance &&
            instance is not IAsyncInitializable)
        {
            return false;
        }

        if (descriptor.ServiceType.IsGenericType && !descriptor.ServiceType.IsConstructedGenericType)
        {
            return false;
        }

        return true;
    }
}

