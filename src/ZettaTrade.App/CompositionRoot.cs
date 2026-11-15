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
using ZettaTrade.Infrastructure.Backup;
using ZettaTrade.Infrastructure.Configuration;
using ZettaTrade.Infrastructure.Exchange;
using ZettaTrade.Infrastructure.Export;
using ZettaTrade.Infrastructure.Indicators;
using ZettaTrade.Infrastructure.Licensing;
using ZettaTrade.Infrastructure.Persistence;
using ZettaTrade.Infrastructure.Plugins;
using ZettaTrade.Infrastructure.Secrets;
using ZettaTrade.Infrastructure.Shell;
using ZettaTrade.Strategies;

/// <summary>
/// DI composition root. Registers all services and dependencies.
/// </summary>
internal static class CompositionRoot
{
    /// <summary>The observed pre-warm receipt task (the T-231 pump canon:
    /// a fire-and-forget rides a field, never a naked discard).</summary>
    private static Task? _prewarmReceipt;

    /// <summary>One Debug receipt for the model pre-warm (T-237): the
    /// builds settle off the critical path, so the line logs whenever they
    /// are done. The outcome task never faults.</summary>
    private static async Task ReportPrewarmReceiptAsync()
    {
        var outcome = await PersistenceModelPrewarm.Outcome;
        Log.Debug(
            "Startup model pre-warm: {Warmed}/{Total} models warm ({WallMs:F0} ms, {Failed} degraded)",
            outcome.Warmed,
            outcome.Warmed + outcome.Failed,
            outcome.WallMs,
            outcome.Failed);
    }

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
        services.AddProfileBackupServices(); // T-215: profile backup/restore over the resolved paths.

        // ThemeManager is registered both by its concrete type (the read-only
        // diagnostics surface — LoadErrors/LoadErrorsChanged — lives there,
        // not on the Core interface) and as the IThemeManager service.
        services.AddSingleton<ThemeManager>(sp =>
            new ThemeManager(
                sp.GetRequiredService<ILogger<ThemeManager>>(),
                ConfigPathResolver.ThemesDirectory));
        services.AddSingleton<IThemeManager>(sp => sp.GetRequiredService<ThemeManager>());

        // The chip foreground derivation (T-152): every theme apply serves
        // the state-colored chips a readable text color (the dark themes
        // fill the chips bright, the light theme dark — no fixed foreground
        // serves both). IAsyncInitializable: the startup walk owns the
        // subscription.
        services.AddSingleton<ChipForegroundPublisher>();

        // The scrim veil derivation (T-182): every theme apply serves the
        // overlay scrims a translucent Background wash (the palette carries
        // no translucent key — the derivation is arithmetic over the applied
        // Background, never a scored pick). IAsyncInitializable: the startup
        // walk owns the subscription.
        services.AddSingleton<ScrimBrushPublisher>();

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

        // The SDK plugin seam (T-131): the blessed path out-of-tree plugin
        // assemblies travel — one manifest scan at the startup walk, the
        // registrations landing in the extension registry for the shell.
        services.AddPluginSdk(ConfigPathResolver.PluginsDirectory);
        services.AddLicensingServices();

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

        // Persistent chat history (T-206): the conversations' transcripts of
        // record. Registered BEFORE the ChatViewModel factory — the startup
        // walk applies its migrations ahead of the history restore.
        services.AddChatHistory();

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

        services.AddRiskLimitServices();
        services.AddSingleton<IRiskManager, DefaultRiskManager>();
        services.AddSingleton<IConfirmationService>(sp => new UiConfirmationService(
            sp.GetRequiredService<ILogger<UiConfirmationService>>(),
            sp.GetRequiredService<ILocalizationManager>()));
        services.AddSingleton<TradingToolsRegistrar>();
        services.AddSingleton<McpWorkspaceToolsRegistrar>(); // T-208: the workspace surface (resources, prompts, workspace tools).

        // First-run disclaimer: the acknowledged-version flag rides the
        // settings store (read fresh at every launch, never cached), and the
        // dialog view model is transient per open.
        services.AddDisclaimerServices();
        services.AddLicenseActivationServices();

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
                sp.GetRequiredService<ILocalizationManager>(),
                sp.GetRequiredService<ActiveExchangeService>(),
                sp.GetRequiredService<TradingModeService>(),
                sp.GetRequiredService<NotificationCenter>().Events,
                sp.GetRequiredService<AdapterHealthService>().HealthStream,
                logger: sp.GetRequiredService<ILogger<StatusBarViewModel>>(),
                // T-213: the update chip rides the checker's verdict stream.
                updateReleases: sp.GetRequiredService<UpdateCheckService>().Results));
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
                sp.GetRequiredService<MarketContextPreambleComposer>(),
                sp.GetRequiredService<IStreamingChatCompletion>(),
                sp.GetRequiredService<IChatHistoryStore>()));
        // The detached-panel runtime (T-103): the one authority of the
        // detached state with its per-panel window shells. Registered
        // before the layout ViewModel — the layout mirror and the View
        // menu's toggle route through it.
        services.AddDetachedPanelsServices();
        // Workspace layout: the persisted column widths of the draggable
        // splitters. Registered AFTER ChatViewModel on purpose — the startup
        // walk runs registrations in order, and the layout restore must
        // follow the chat's visibility restore so the collapsed/visible
        // assistant geometry applies before any window opens (T-038).
        services.AddSingleton<WorkspaceLayoutViewModel>(sp =>
            new WorkspaceLayoutViewModel(
                sp.GetRequiredService<ISettingsService>(),
                sp.GetRequiredService<ChatViewModel>(),
                sp.GetRequiredService<IDetachedPanelCoordinator>(),
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
                sp.GetRequiredService<IConfirmationService>(),
                sp.GetRequiredService<ProfileBackupService>(),
                sp.GetRequiredService<ISecretStore>()));
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
        // window owns the row-press route. IAsyncInitializable (T-177):
        // the startup walk restores the rail's persisted height before
        // the first frame.
        services.AddSingleton<SignalRailViewModel>(sp =>
            new SignalRailViewModel(
                sp.GetRequiredService<StrategySignalService>().Signals,
                sp.GetRequiredService<ILocalizationManager>(),
                sp.GetRequiredService<ISettingsService>(),
                sp.GetRequiredService<ILogger<SignalRailViewModel>>()));

        // The named chart templates' manager (T-202): IAsyncInitializable —
        // the startup walk restores the stored templates envelope and the
        // default template's name before the chart constructs, so the
        // chart's entry-less seeding pulls a resolved default. Registered
        // BEFORE the chart ViewModel it serves (the walk's order warms it
        // first); the primary passes it in, the slots pass none.
        services.AddSingleton<ChartTemplatesViewModel>();

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
                sp.GetRequiredService<IChartDrawingStore>(), // T-091: the persisted drawings the page mirrors.
                sp.GetRequiredService<StrategySignalService>(), // T-112: the stances the markers read.
                sp.GetRequiredService<SignalRailViewModel>(), // T-112: the rail strip's VM.
                sp.GetRequiredService<SignalSeriesRelay>(), // T-112: the loads the host feeds from.
                                                            // #region T-122: the footprint feed's stream (my-activity overlays).
                sp.GetRequiredService<ChartFootprintService>().Updates,
                // #endregion T-122
                sp.GetRequiredService<ILogger<ChartViewModel>>(),
                // T-202: the primary hosts the templates manager (the grid
                // slots resolve their own charts without it).
                templates: sp.GetRequiredService<ChartTemplatesViewModel>()));
        // The multi-chart grid (T-201): the workspace's layout-mode
        // ViewModel over lazy slot factories. IAsyncInitializable: the
        // startup walk restores the persisted layout and seam sizes before
        // any window opens; a restored tiled layout creates its slots
        // right there (their init walks ride the ViewModel's serialized
        // queue).
        services.AddWorkspaceChartsServices();
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
        services.AddWatchlistServices(); // T-120/T-196: the watchlist panel (catalog + feed + rows) over the same facade/selection pair above.
        services.AddHeatmapServices(); // T-121: the heatmap panel (candidate pumps + ranked tile page) beside the watchlist rail above.
        services.AddScreenerServices(); // T-195: the screener panel (candidate pumps + the sortable table page) beside the heatmap rail above.
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
                sp.GetRequiredService<WorkspaceChartsViewModel>(), // T-201: the multi-chart grid's layout mode + slots.
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

        // The trade-notification sinks (T-260, the webhook batch T-261, the
        // Matrix and email pair T-262): the best-effort sink fleet over the
        // center's trade events (opt-in per kind — Telegram first, then
        // Discord/Slack/ntfy, then Matrix and email; the webhook URLs, the
        // Matrix access token and the SMTP credentials ride the secret
        // store, the ntfy topic URL and the Matrix/SMTP addresses are
        // public by design and ride the settings). Registered AFTER the
        // notification services (the bridge rides the center's event lane)
        // and AFTER the trading-mode registration (the message's paper/live
        // tag reads its lane stream).
        services.AddNotificationSinkServices();

        // The update checker (T-213): the periodic (and manual) GitHub
        // Releases check. Registered AFTER the notification services it
        // publishes through; its first check rides its own loop task, so
        // the startup walk never awaits the network.
        services.AddUpdateServices();

        // The tray presence (T-214): the close policy the main window's
        // gesture consults and the tray icon's menu. Registered after the
        // update services — the settings service and the notification
        // center it reads must be in the container first.
        services.AddTrayServices();

        // The PnL accounting core + its app-side feed (T-082): after the
        // order history registration, so the walk initializes the ledger
        // store before the engine's boot replay reads it.
        services.AddPnlServices();

        // The portfolio-value history (T-209): the store migrates first
        // (the walk's order), then the snapshot service's cadence loop
        // starts — its first write rides the migrated schema.
        services.AddPortfolioValueStore();
        services.AddPortfolioValueServices();

        #region T-122: the chart footprint feed (my-activity overlays on the chart)
        services.AddChartFootprintServices(); // after the journals + PnL feed: the boot read wants them initialized first.
        #endregion

        // The local candle cache (T-085): after the settings registration
        // it rides, so the walk loads the Caching policy before the chart's
        // bootstrap can serve a first page from the cache.
        services.AddCandleCache();
        services.AddChartDrawingStore(); // T-091: the persisted chart drawings (loads/saves whole annotation layers).
        services.AddCandleBackfillServices(); // T-086: the deep-history fetcher (bounded channel, write-through).
        services.AddHistoryUiServices();
        services.AddTradeStatsServices();
        services.AddTradeNotesServices(); // T-211: the transient trade note-and-tags dialog VM.
        services.AddCsvExportServices();
        services.AddPositionActionsServices(); // T-077: position actions (close, partial close, reverse).
        services.AddBulkCancelServices(); // T-076: bulk cancel (per-symbol, confirmed, best-effort).
        services.AddKillSwitchServices(); // T-188: kill switch (cancel all + close all across the venues, ONE confirmation).
        services.AddFeeServices(); // T-080: fees (schedule, estimate, columns).
        services.AddBacktestServices(); // T-113: the offline deterministic backtest harness + the run VM.
        services.AddAlertsServices(); // T-096: the price-alerts engine (roster solo owner) + the manager VM.
        services.AddSingleton<McpJournalingTradingAdapter>(); // T-133: MCP fills enter the journal.
        services.AddPositionSizerServices(); // T-079: position sizer (balance presets, risk-percent).
        services.AddSingleInstanceServices(); // T-104: the single-instance handoff host + the workspace-symbol apply worker.

        var provider = services.BuildServiceProvider();

        // The walk batches the SQLite stores (T-226): their migrations are
        // mutually independent database files, so they warm concurrently at
        // the earliest store's walk position instead of paying the sum.
        await InitializeRegisteredServicesAsync(
            provider,
            services,
            ct,
            static serviceType => StoreInitBatchTypes.Contains(serviceType));

        // T-237: the model pre-warm's one receipt (Debug) — a stored task
        // keeps the fire-and-forget observed (the T-231 pump canon): the
        // receipt logs whenever the builds settle, never blocking the
        // composition. Gated on the kick: a process that never started
        // the warm-up holds no pending outcome.
        if (PersistenceModelPrewarm.Started)
        {
            _prewarmReceipt = ReportPrewarmReceiptAsync();
        }
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

        // The workspace MCP surface (T-208): the read-only resources, the
        // prompt templates and the workspace tools register against the SAME
        // host, riding the same journaling wrapper (one venue view), the
        // alert engine, the workspace context and the journal read queries.
        // Registration stays after the startup walk for the same reason the
        // trading tools' does: the wrapper's construction reads the settings
        // file's endpoints first.
        provider.GetRequiredService<McpWorkspaceToolsRegistrar>().RegisterAll(
            provider.GetRequiredService<IMcpServerHost>(),
            provider.GetRequiredService<McpJournalingTradingAdapter>(),
            provider.GetRequiredService<IAlertEngine>(),
            provider.GetRequiredService<ChatContextService>(),
            provider.GetRequiredService<IOrderHistoryStore>(),
            provider.GetRequiredService<IOrderStatsQuery>(),
            provider.GetRequiredService<IConfirmationService>(),
            provider.GetRequiredService<ILocalizationManager>());

        // T-258: the composition walk completed — this start IS the
        // healthy-launch proof an applied update waits for. The runner
        // settles the swap and cleans the parking spots (non-throwing by
        // design; a build that never reaches here serves the previous one
        // again on the next start).
        UpdateSwapRunner.MarkHealthy(AppContext.BaseDirectory);

        return provider;
    }

    /// <summary>
    /// The SQLite stores whose <see cref="IAsyncInitializable"/> migrations
    /// are mutually independent (one database file each): the walk starts
    /// the whole batch CONCURRENTLY at the earliest store's walk position
    /// and awaits it there, so every later walk step still sees every store
    /// ready while the batch's wall time becomes the slowest store instead
    /// of the sum (T-226 — the chat history spot's ~0.85 s serialized the
    /// walk, and the first store also paid the one-time EF Core internal
    /// warm-up that every other store then reused).
    /// </summary>
    internal static readonly Type[] StoreInitBatchTypes =
    [
        typeof(IChatHistoryStore),
        typeof(IOrderHistoryStore),
        typeof(IPortfolioValueStore),
        typeof(ICandleCache),
        typeof(IChartDrawingStore),
    ];

    /// <summary>
    /// Initialize every registered service instance implementing
    /// <see cref="IAsyncInitializable"/>, in registration order, once per instance.
    /// The optional <paramref name="storeBatch"/> predicate marks service types
    /// whose initialization batches concurrently (T-226): the first matching
    /// descriptor's walk position starts and awaits the whole batch, later
    /// matches skip (the walk's once-per-instance dedup holds).
    /// </summary>
    internal static async Task InitializeRegisteredServicesAsync(
        IServiceProvider provider,
        IServiceCollection registrations,
        CancellationToken ct,
        Func<Type, bool>? storeBatch = null)
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

            if (storeBatch?.Invoke(descriptor.ServiceType) == true)
            {
                // The batch resolves every store instance fresh (the
                // triggering one included — it is not in the dedup set yet),
                // starts them all concurrently and awaits HERE: everything
                // the walk visits after this descriptor sees the batch done.
                await InitializeStoreBatchAsync(provider, initialized, ct);
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
    /// Start every batched store's initialization concurrently and await the
    /// whole batch at the caller's walk position. Instances the walk already
    /// initialized (a repeated batch descriptor) drop; a missing registration
    /// drops silently (GetService answers null — the predicate's own types
    /// always register, so the miss is theoretical). Failures surface through
    /// <see cref="Task.WhenAll"/>'s first exception — the walk's fail-fast
    /// contract (BUG-019) stays exactly as it was.
    /// </summary>
    private static async Task InitializeStoreBatchAsync(
        IServiceProvider provider,
        HashSet<object> initialized,
        CancellationToken ct)
    {
        var batch = StoreInitBatchTypes
            .Select(provider.GetService)
            .OfType<IAsyncInitializable>()
            .Where(initialized.Add)
            .ToList();

        if (batch.Count == 0)
        {
            return;
        }

        await Task.WhenAll(batch.Select(service => service.InitializeAsync(ct)));
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

