namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Livery.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Export;
using ZettaTrade.Infrastructure.Persistence;
using ZettaTrade.Plugins.Abstractions;
using ZettaTrade.Strategies;
using ZettaTrade.UI.Charting;

/// <summary>
/// The heatmap panel ViewModel's pins (T-121): an applied snapshot moves the
/// widget's tiles and the empty-state flag as ONE panel state, the metric
/// switch's index shim maps both ways onto the feed's metric (never a
/// third value), a parsable tile press writes the workspace symbol, and an
/// unparsable one is refused without a substitute. The panel's pulse rides
/// an <c>Observable.Never</c> seam, so each fact drives the public
/// <see cref="HeatmapViewModel.ApplySnapshot"/> directly - the snap-in
/// subscription's threading stays the launch smoke's territory.
/// </summary>
public sealed class HeatmapViewModelTests
{
    #region Scene kit

    /// <summary>The panel test's whole scene: the facade, the feed, the chart, the panel.</summary>
    private sealed class Scene
    {
        public Scene()
        {
            var settings = Substitute.For<ISettingsService>();
            settings.Changes.Returns(Observable.Empty<SettingsChange>());
            settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>())
                .Returns(string.Empty);
            Exchange = new ActiveExchangeService(
                [new VoidMarketData()],
                settings,
                NullLogger<ActiveExchangeService>.Instance);

            // The Never pulse: the panel's own ctor subscription never fires;
            // the facts drive ApplySnapshot directly - no clock, no races.
            Feed = new HeatmapFeedService(
                Exchange,
                new SymbolListProvider(Exchange, NullLogger<SymbolListProvider>.Instance),
                NullLogger<HeatmapFeedService>.Instance,
                Observable.Never<long>());
            Chart = CreateChart(Exchange, settings);
            Panel = new(
                Feed,
                Chart,
                NullLogger<HeatmapViewModel>.Instance);
        }

        public ActiveExchangeService Exchange { get; }

        public HeatmapFeedService Feed { get; }

        public ChartViewModel Chart { get; }

        public HeatmapViewModel Panel { get; }

        /// <summary>The no-indicator catalog stub: no plugin resolves, the chart pipeline stays bare.</summary>
        internal sealed class EmptyIndicatorCatalog : IIndicatorCatalog
        {
            public IReadOnlyCollection<string> LoadedNames { get; } = [];

            public IIndicator? Create(string name, int period) => null;
        }

        /// <summary>The honest cold-cache stub: empty answers everywhere.</summary>
        internal sealed class EmptyCandleCache : ICandleCache
        {
            public Task<IReadOnlyList<Bar>> GetLatestAsync(
                string adapterId,
                Symbol symbol,
                TimeFrame timeFrame,
                int limit,
                CancellationToken cancellationToken = default) =>
                BarsAsync();

            public Task<IReadOnlyList<Bar>> GetOlderThanAsync(
                string adapterId,
                Symbol symbol,
                TimeFrame timeFrame,
                DateTimeOffset before,
                int limit,
                CancellationToken cancellationToken = default) =>
                BarsAsync();

            public Task WriteThroughAsync(
                string adapterId,
                Symbol symbol,
                TimeFrame timeFrame,
                IReadOnlyList<Bar> bars,
                CancellationToken cancellationToken = default) =>
                DoneAsync();

            private static async Task<IReadOnlyList<Bar>> BarsAsync()
            {
                await Task.CompletedTask;
                return [];
            }

            private static async Task DoneAsync()
            {
                await Task.CompletedTask;
            }
        }
    }

    private static ChartViewModel CreateChart(ActiveExchangeService service, ISettingsService settings)
    {
        // The T-112 seam's inert hosting pair: a quiet signals stream, the
        // heatmap facts' chart expectations untouched.
        var relay = new SignalSeriesRelay();
        var signals = new StrategySignalService(
            relay.Pages,
            Substitute.For<IStrategyCatalog>(),
            NullLoggerFactory.Instance);
        var rail = new SignalRailViewModel(
            signals.Signals,
            Substitute.For<ILocalizationManager>(),
            Substitute.For<ISettingsService>(),
            NullLogger<SignalRailViewModel>.Instance);

        // The drawings store rides silent in these facts: an empty layer
        // answers every load, so the page deliveries stay untouched.
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
            new Scene.EmptyIndicatorCatalog(),
            Substitute.For<ICsvExportService>(),
            Substitute.For<ILocalizationManager>(),
            new Scene.EmptyCandleCache(),
            new CandleBackfillService(new Scene.EmptyCandleCache(), NullLogger<CandleBackfillService>.Instance),
            drawingStore,
            signals,
            rail,
            relay,
            // The T-122 seam rides silent in the heatmap facts likewise.
            Observable.Empty<ChartFootprintSnapshot>(),
            NullLogger<ChartViewModel>.Instance);
    }

    /// <summary>The market-data void: stands for the masked single-adapter registration the facade requires; every stream it offers is bottomless.</summary>
    internal sealed class VoidMarketData : IMarketDataAdapter
    {
        public string Id => "demo";

        public string Name => "demo";

        public bool IsConnected => true;

        public ExchangeCapabilities Capabilities => ExchangeCapabilities.MarketData;

        public Task<IReadOnlyList<Symbol>> GetSymbolsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Symbol>>([]);

        public Task<IReadOnlyList<Bar>> GetBarsAsync(
            Symbol symbol,
            TimeFrame timeFrame,
            DateTimeOffset from,
            DateTimeOffset? to = null,
            int limit = 500,
            CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Bar>>([]);

        public IObservable<Ticker> SubscribeTicker(Symbol symbol) => Observable.Never<Ticker>();

        public IObservable<OrderBook> SubscribeOrderBook(Symbol symbol, int depth = 20) => Observable.Never<OrderBook>();

        public IObservable<MarketTrade> SubscribeTrades(Symbol symbol) => Observable.Never<MarketTrade>();

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    #endregion Scene kit

    #region Snapshot apply

    /// <summary>An applied snapshot moves the tiles and the empty flag as one panel state.</summary>
    [Fact]
    public void AnAppliedSnapshot_MovesTheTilesAndTheEmptyFlag_AsOneState()
    {
        var scene = new Scene();

        scene.Panel.HasData.Should().BeFalse("a fresh panel owns no ranked page yet");

        scene.Panel.ApplySnapshot(
            [new HeatmapTile("BTC/USDT", 2m, 400m), new HeatmapTile("ETH/USDT", -1m, 150m)]);

        scene.Panel.HasData.Should().BeTrue();
        scene.Panel.Tiles.Select(static tile => tile.SymbolText)
            .Should().Equal(
                (IEnumerable<string>)["BTC/USDT", "ETH/USDT"],
                "the feed's rank order reaches the widget untouched");

        scene.Panel.ApplySnapshot([]);

        scene.Panel.Tiles.Should().BeEmpty("an empty page empties the widget instead of a stale surface");
        scene.Panel.HasData.Should().BeFalse();
    }

    #endregion Snapshot apply

    #region Metric shim

    /// <summary>The index shim maps both ways onto the feed's metric, never a third value.</summary>
    [Fact]
    public void TheMetricIndexShim_MapsTheFeedBothWays()
    {
        var scene = new Scene();

        scene.Panel.SelectedMetricIndex.Should().Be(0, "the feed's standing metric is the quote volume");

        scene.Panel.SelectedMetricIndex = 1;

        scene.Feed.Metric.Should().Be(HeatmapMetric.Change24hPercent,
            "index 1 maps the change metric on the feed itself");
        scene.Panel.SelectedMetricIndex.Should().Be(1);

        scene.Panel.SelectedMetricIndex = 0;

        scene.Feed.Metric.Should().Be(HeatmapMetric.QuoteVolume24h);
        scene.Panel.SelectedMetricIndex.Should().Be(0);
    }

    /// <summary>The shim reflects the feed's metric whatever wrote it.</summary>
    [Fact]
    public void TheShim_ReflectsTheFeedWhateverWroteTheMetric()
    {
        var scene = new Scene();

        scene.Feed.Metric = HeatmapMetric.Change24hPercent;

        scene.Panel.SelectedMetricIndex.Should().Be(1,
            "the shim reads the feed's standing metric, not its own shadow");
    }

    #endregion Metric shim

    #region Tile press route

    /// <summary>A parsable tile press writes the workspace symbol (no substitutions).</summary>
    [Fact]
    public async Task AParsableTilePress_WritesTheWorkspaceSymbol()
    {
        var scene = new Scene();
        scene.Chart.SelectedSymbol = Symbol.Parse("ETH/USDT");

        await scene.Panel.RouteTileAsync(new HeatmapTileClick("BTC/USDT"));

        scene.Chart.SelectedSymbol.Should().Be(Symbol.Parse("BTC/USDT"),
            "the pressed tile's text IS the new workspace symbol");
    }

    /// <summary>An unparsable press is refused: the workspace symbol stays exactly what it was.</summary>
    [Fact]
    public async Task AnUnparsableTilePress_IsRefusedWithoutASubstitute()
    {
        var scene = new Scene();
        scene.Chart.SelectedSymbol = Symbol.Parse("ETH/USDT");

        await scene.Panel.RouteTileAsync(new HeatmapTileClick(string.Empty));
        await scene.Panel.RouteTileAsync(new HeatmapTileClick("   "));

        scene.Chart.SelectedSymbol.Should().Be(Symbol.Parse("ETH/USDT"),
            "an empty tile text owns no route and never invents a substitute symbol");
    }

    #endregion Tile press route
}
