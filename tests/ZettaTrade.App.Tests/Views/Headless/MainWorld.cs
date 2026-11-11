namespace ZettaTrade.App.Tests.Views.Headless;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Livery.Localization;
using Livery.Themes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ZettaTrade.AI.Mcp;
using ZettaTrade.App.Services;
using ZettaTrade.App.Tests.Services;
using ZettaTrade.App.Tests.ViewModels;
using ZettaTrade.App.ViewModels;
using ZettaTrade.App.Views;
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Exchange;
using ZettaTrade.Infrastructure.Export;
using ZettaTrade.Infrastructure.Persistence;
using ZettaTrade.Infrastructure.Shell;
using ZettaTrade.Plugins.Abstractions;
using ZettaTrade.Strategies;

/// <summary>
/// One wired main window world (T-144): the full
/// <see cref="MainViewModel"/> graph — every panel, overlay and the
/// workspace shell — over mocked services, exactly the composition the
/// app's container serves, assembled by hand off the per-ViewModel test
/// kits' recipes (the chat, HUD and hotkey kits; the chart recipe the
/// palette facts ride; plain substitutes everywhere else). The facts
/// materialize the REAL windows against it and drive the routes the
/// window owns.
/// </summary>
/// <param name="Main">The composed main ViewModel.</param>
/// <param name="Exchanges">The active-exchange facade over the one fake adapter.</param>
/// <param name="Adapter">The offline market-data fake.</param>
/// <param name="Settings">The shared settings substitute.</param>
/// <param name="Changes">The settings change stream the mirror reads.</param>
/// <param name="Localization">The REAL localization singleton the world initialized.</param>
/// <param name="Detacher">The detach runtime the layout's mirror rides (the
/// T-103 fake: its emits drive the real window's detach geometry, the
/// T-173 lane included).</param>
internal sealed record MainWorld(
    MainViewModel Main,
    ActiveExchangeService Exchanges,
    MainWorld.FakeMarketDataAdapter Adapter,
    ISettingsService Settings,
    Subject<SettingsChange> Changes,
    ILocalizationManager Localization,
    FakeDetachedPanelCoordinator Detacher)
{
    /// <summary>
    /// Compose the world off the shared settings substitute and the one
    /// offline adapter: a fresh profile's shape — no stored choices, no
    /// marks, an empty plugin catalog — with every stream open. The headless
    /// world stands first (its localization singleton feeds the composed
    /// ViewModels). The optional rail (T-177) swaps the chart's rail strip
    /// for a subject-fed one the facts drive directly.
    /// </summary>
    public static async Task<MainWorld> CreateAsync(SignalRailViewModel? rail = null)
    {
        await HeadlessWorld.EnsureAppAsync();
        await HeadlessWorld.EnsureHealthyThemeSingletonAsync();

        var settings = Substitute.For<ISettingsService>();
        var changes = new Subject<SettingsChange>();
        settings.Changes.Returns(changes);
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
        settings.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);

        // The external MCP client manager stub the settings ViewModel's
        // constructor subscribes to (the settings tests' ClientManagerStub
        // recipe): a telemetry-less substitute.
        var mcpClients = Substitute.For<IMcpClientManager>();
        mcpClients.StatesChanged.Returns(Observable.Empty<IReadOnlyList<McpClientState>>());

        var adapter = new FakeMarketDataAdapter("demo", "Demo", "BTC/USDT", "ETH/USDT");
        var activeExchanges = new ActiveExchangeService(
            new IMarketDataAdapter[] { adapter },
            settings,
            NullLogger<ActiveExchangeService>.Instance);

        var localization = LocalizationManager.Instance;

        var chart = CreateChart(activeExchanges, settings, rail);
        var palette = new SymbolPaletteViewModel(
            activeExchanges,
            new SymbolPaletteSource(activeExchanges, NullLogger<SymbolPaletteSource>.Instance),
            settings,
            chart,
            localization,
            NullLogger<SymbolPaletteViewModel>.Instance);
        var chat = ChatTests.CreateAnonymousChat();
        var hud = HudTests.CreateAnonymousHud();
        var hotkeys = HotkeyKits.CreateAnonymousHotkeys();

        var tradingMode = new TradingModeService(
            TradedAdapter(),
            new ExchangeEndpointRegistry(settings, NullLogger<ExchangeEndpointRegistry>.Instance),
            Substitute.For<IConfirmationService>(),
            NullLogger<TradingModeService>.Instance);
        var statusBar = new StatusBarViewModel(
            localization,
            activeExchanges,
            tradingMode,
            Observable.Never<Notification>(),
            Observable.Never<IReadOnlyList<AdapterHealth>>(),
            logger: NullLogger<StatusBarViewModel>.Instance);

        var selections = Observable.Never<Symbol>();
        var orderBook = new OrderBookViewModel(
            adapter,
            selections,
            NullLogger<OrderBookViewModel>.Instance);
        var tape = new TradeTapeViewModel(
            adapter,
            selections,
            NullLogger<TradeTapeViewModel>.Instance);
        var watchlist = new WatchlistViewModel(
            new WatchlistFeedService(
                adapter,
                selections,
                NullLogger<WatchlistFeedService>.Instance),
            NullLogger<WatchlistViewModel>.Instance);
        var heatmap = new HeatmapViewModel(
            new HeatmapFeedService(activeExchanges, NullLogger<HeatmapFeedService>.Instance),
            chart,
            NullLogger<HeatmapViewModel>.Instance);
        var toast = new NotificationToastHostViewModel(
            new NotificationCenter(settings, NullLogger<NotificationCenter>.Instance),
            localization,
            NullLogger<NotificationToastHostViewModel>.Instance);
        var settingsViewModel = new SettingsViewModel(
            settings,
            Substitute.For<IThemeManager>(),
            localization,
            Substitute.For<IShellUtils>(),
            new McpHostService(
                settings,
                Substitute.For<IMcpServerHost>(),
                NullLogger<McpHostService>.Instance),
            activeExchanges,
            new SymbolListProvider(activeExchanges, NullLogger<SymbolListProvider>.Instance),
            new EmptyIndicatorCatalog(),
            mcpClients,
            NullLogger<SettingsViewModel>.Instance);
        // The detach runtime the mirror reflects: the T-103 fake with its
        // synchronous emits — the detach/attach lanes drive the real
        // window's geometry through it (hoisted so the layout ctor reads
        // the same instance the record exposes).
        var detacher = new FakeDetachedPanelCoordinator();

        var layout = new WorkspaceLayoutViewModel(
            settings,
            chat,
            detacher,
            NullLogger<WorkspaceLayoutViewModel>.Instance);
        var menu = new MenuViewModel(chat, hud, hotkeys);

        var main = new MainViewModel(
            menu,
            statusBar,
            chat,
            settingsViewModel,
            chart,
            orderBook,
            tape,
            watchlist,
            heatmap,
            layout,
            palette,
            toast,
            hud,
            hotkeys,
            activeExchanges,
            NullLogger<MainViewModel>.Instance);

        return new MainWorld(
            main,
            activeExchanges,
            adapter,
            settings,
            changes,
            localization,
            detacher);
    }

    /// <summary>
    /// Materialize the real main window against the world on the
    /// user-interface thread and land its open on the serialized
    /// dispatcher.
    /// </summary>
    public async Task<MainWindow> ShowWindowAsync()
    {
        var window = await HeadlessWorld.OnUi(() => new MainWindow(Main));
        await HeadlessWorld.ShowAndSettleAsync(window);
        return window;
    }

    /// <summary>
    /// The shipped paper-simulator surface: the trading lane resolves from
    /// the registry's built-in snapshot (the status-bar tests' recipe) — a
    /// bare substitute's empty identity is an error by design.
    /// </summary>
    private static ITradingAdapter TradedAdapter()
    {
        var traded = Substitute.For<ITradingAdapter>();
        traded.Id.Returns(PaperTradingAdapter.ExchangeId);
        return traded;
    }

    /// <summary>
    /// The chart recipe the palette facts ride (T-103's inert hosting
    /// pair included): a quiet signals stream, an empty drawing store and
    /// the honest cold cache — the chart pipeline stays bare. The optional
    /// rail (T-177) replaces the default one so a fact feeds rows through
    /// its own subject.
    /// </summary>
    internal static ChartViewModel CreateChart(
        ActiveExchangeService service,
        ISettingsService settings,
        SignalRailViewModel? rail = null)
    {
        var relay = new SignalSeriesRelay();
        var signals = new StrategySignalService(
            relay.Pages,
            Substitute.For<IStrategyCatalog>(),
            NullLoggerFactory.Instance);
        rail ??= new SignalRailViewModel(
            signals.Signals,
            Substitute.For<ILocalizationManager>(),
            settings,
            NullLogger<SignalRailViewModel>.Instance);

        var drawingStore = Substitute.For<IChartDrawingStore>();
        drawingStore.LoadAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<TimeFrame>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ChartDrawing>>([]));

        return new(
            service,
            new SymbolListProvider(service, NullLogger<SymbolListProvider>.Instance),
            settings,
            new EmptyIndicatorCatalog(),
            Substitute.For<ICsvExportService>(),
            Substitute.For<ILocalizationManager>(),
            new EmptyCandleCache(),
            new CandleBackfillService(new EmptyCandleCache(), NullLogger<CandleBackfillService>.Instance),
            drawingStore,
            signals,
            rail,
            relay,
            // The T-122 seam rides silent in the window facts likewise.
            Observable.Empty<ChartFootprintSnapshot>(),
            NullLogger<ChartViewModel>.Instance);
    }

    /// <summary>One offline market-data fake: a fixed symbol catalog, silent feeds.</summary>
    public sealed class FakeMarketDataAdapter : IMarketDataAdapter
    {
        private readonly IReadOnlyList<Symbol> _symbols;

        public FakeMarketDataAdapter(string id, string name, params string[] symbols)
        {
            Id = id;
            Name = name;
            _symbols = symbols.Select(Symbol.Parse).ToList();
        }

        public string Id { get; }

        public string Name { get; }

        public bool IsConnected => true;

        public ExchangeCapabilities Capabilities => ExchangeCapabilities.MarketData;

        public async Task<IReadOnlyList<Symbol>> GetSymbolsAsync(CancellationToken ct = default)
        {
            await Task.CompletedTask;
            return _symbols;
        }

        public async Task<IReadOnlyList<Bar>> GetBarsAsync(
            Symbol symbol,
            TimeFrame timeFrame,
            DateTimeOffset from,
            DateTimeOffset? to = null,
            int limit = 500,
            CancellationToken ct = default)
        {
            await Task.CompletedTask;
            return [];
        }

        public IObservable<Ticker> SubscribeTicker(Symbol symbol) => Observable.Never<Ticker>();

        public IObservable<OrderBook> SubscribeOrderBook(Symbol symbol, int depth = 20) =>
            Observable.Never<OrderBook>();

        public IObservable<MarketTrade> SubscribeTrades(Symbol symbol) => Observable.Never<MarketTrade>();

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>The no-indicator catalog stub: no plugin resolves, the chart pipeline stays bare.</summary>
    private sealed class EmptyIndicatorCatalog : IIndicatorCatalog
    {
        public IReadOnlyCollection<string> LoadedNames { get; } = [];

        public IIndicator? Create(string name, int period) => null;
    }

    /// <summary>The honest cold-cache stub: empty answers everywhere.</summary>
    private sealed class EmptyCandleCache : ICandleCache
    {
        public Task<IReadOnlyList<Bar>> GetLatestAsync(
            string adapterId,
            Symbol symbol,
            TimeFrame timeFrame,
            int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Bar>>([]);

        public Task<IReadOnlyList<Bar>> GetOlderThanAsync(
            string adapterId,
            Symbol symbol,
            TimeFrame timeFrame,
            DateTimeOffset before,
            int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Bar>>([]);

        public Task WriteThroughAsync(
            string adapterId,
            Symbol symbol,
            TimeFrame timeFrame,
            IReadOnlyList<Bar> bars,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
