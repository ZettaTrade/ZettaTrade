namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Livery.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ReactiveUI.Primitives;
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

/// <summary>
/// The screener panel ViewModel's pins (T-195): an applied page moves the
/// rows and the state flags as ONE panel state, the presets filter and
/// sort (gainers the positive change descending, losers the negative one
/// ascending, top volume descending), a column press retires the preset to
/// All and flips direction on a repeat press, the sort rebuilds from the
/// held page immediately, ties resolve by the ordinal symbol text, and a
/// parsable row press writes the workspace symbol while an unparsable one
/// is refused without a substitute. The panel's pulse rides an
/// <c>Observable.Never</c> seam, so each fact drives the public
/// <see cref="ScreenerViewModel.ApplySnapshot"/> directly - the snap-in
/// subscription's threading stays the launch smoke's territory.
/// </summary>
public sealed class ScreenerViewModelTests
{
    #region Scene kit

    /// <summary>The panel test's whole scene: the facade, the feed, the chart, the trend computer, the panel.</summary>
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
            Feed = new ScreenerFeedService(
                Exchange,
                new SymbolListProvider(Exchange, NullLogger<SymbolListProvider>.Instance),
                NullLogger<ScreenerFeedService>.Instance,
                Observable.Never<long>());
            Chart = CreateChart(Exchange, settings);
            Localization = Substitute.For<ILocalizationManager>();
            // The words echo their keys, so the facts stay culture-free.
            Localization.GetString(Arg.Any<string>())
                .Returns(static call => call.ArgAt<string>(0));
            Trends = new(
                Feed,
                new EmptyCandleCache(),
                Exchange,
                new CandleBackfillService(new EmptyCandleCache(), NullLogger<CandleBackfillService>.Instance),
                settings,
                NullLogger<MtfTrendService>.Instance);
            Panel = new(
                Feed,
                Chart,
                Trends,
                Localization,
                NullLogger<ScreenerViewModel>.Instance);
        }

        public ActiveExchangeService Exchange { get; }

        public ScreenerFeedService Feed { get; }

        public ChartViewModel Chart { get; }

        public MtfTrendService Trends { get; }

        public ILocalizationManager Localization { get; }

        public ScreenerViewModel Panel { get; }

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
        // screener facts' chart expectations untouched.
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
            // The T-122 seam rides silent in the screener facts likewise.
            Observable.Empty<ChartFootprintSnapshot>(),
            // The funding pane rides the honest no-pane here too: the void
            // adapter carries no funding-history capability.
            new FundingHistoryService(service, TimeProvider.System),
            NullLogger<ChartViewModel>.Instance);
    }

    /// <summary>The market-data void: stands for the masked single-adapter registration the facade requires; every stream it offers is bottomless.</summary>
    internal sealed class VoidMarketData : IMarketDataAdapter
    {
        public string Id => "demo";

        public string Name => "demo";

        public bool IsConnected => true;

        public ExchangeCapabilities Capabilities => ExchangeCapabilities.MarketData;

        public async Task<IReadOnlyList<Symbol>> GetSymbolsAsync(CancellationToken ct = default)
        {
            await Task.CompletedTask;
            return [];
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

        public IObservable<OrderBook> SubscribeOrderBook(Symbol symbol, int depth = 20) => Observable.Never<OrderBook>();

        public IObservable<MarketTrade> SubscribeTrades(Symbol symbol) => Observable.Never<MarketTrade>();

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>One feed row shorthand for the facts (symbol, price, volume, change).</summary>
    private static ScreenerRow Row(string symbolText, decimal lastPrice, decimal volume24h, decimal change24hPercent) =>
        new(symbolText, lastPrice, volume24h, change24hPercent);

    /// <summary>The warming cell the absent trend table formats (the key-echo words' culture-free pin).</summary>
    private static ScreenerTrendCellViewModel WarmingCell() => ScreenerTrendCellViewModel.Plain("Screener.Trend.Warming");

    /// <summary>One display row shorthand for the route facts: the text is the route's only real payload.</summary>
    private static ScreenerRowViewModel DisplayRow(string symbolText) => new()
    {
        Symbol = symbolText,
        Price = string.Empty,
        Change = string.Empty,
        Volume = string.Empty,
        Funding = string.Empty,
        IsChangeUp = false,
        IsChangeDown = false,
        TrendM15 = WarmingCell(),
        TrendH1 = WarmingCell(),
        TrendH4 = WarmingCell(),
        TrendD1 = WarmingCell(),
    };

    /// <summary>The facts' standing mixed page: an adapter-order trio with both change signs.</summary>
    private static ScreenerSnapshot MixedPage() => new(
        [
            Row("AAA/USDT", 100m, 10m, 2m),
            Row("BBB/USDT", 50m, 30m, -1m),
            Row("CCC/USDT", 25m, 20m, 5m),
        ],
        ScreenerFeedState.Live);

    #endregion Scene kit

    #region Snapshot apply

    /// <summary>An applied page moves the rows and the state flags as one panel state.</summary>
    [Fact]
    public void AnAppliedPage_MovesTheRowsAndTheState_AsOneState()
    {
        var scene = new Scene();

        scene.Panel.HasRows.Should().BeFalse("a fresh panel owns no page yet");
        scene.Panel.IsCatalogUnavailable.Should().BeTrue("the honest pre-warm-up state is the reason line");

        scene.Panel.ApplySnapshot(MixedPage());

        scene.Panel.HasRows.Should().BeTrue();
        scene.Panel.IsLive.Should().BeTrue();
        scene.Panel.IsWaitingTicks.Should().BeFalse();
        scene.Panel.Rows.Select(static row => row.Symbol)
            .Should().Equal(
                (IEnumerable<string>)["AAA/USDT", "BBB/USDT", "CCC/USDT"],
                "the All preset keeps the feed's adapter order untouched");

        // The display strings ride the shared helpers under the panel's own
        // culture call — the expected text is computed the same way, so the
        // fact stays culture-stable.
        scene.Panel.Rows[0].Price.Should().Be(
            WatchlistRowPresentation.FormatPrice(100m, CultureInfo.CurrentCulture));
        scene.Panel.Rows[0].Change.Should().Be(
            WatchlistRowPresentation.FormatSignedPercent(2m, CultureInfo.CurrentCulture));
        scene.Panel.Rows[0].Volume.Should().Be(
            WatchlistRowPresentation.FormatVolume(10m, CultureInfo.CurrentCulture));
        scene.Panel.Rows[0].IsChangeUp.Should().BeTrue();
        scene.Panel.Rows[1].IsChangeDown.Should().BeTrue();
        scene.Panel.Rows[1].IsChangeUp.Should().BeFalse(
            "a flat or negative change carries exactly one tint flag — the T-159 pair");

        scene.Panel.ApplySnapshot(new ScreenerSnapshot([], ScreenerFeedState.NoTicksYet));

        scene.Panel.Rows.Should().BeEmpty("an empty page empties the table instead of a stale surface");
        scene.Panel.HasRows.Should().BeFalse();
        scene.Panel.IsWaitingTicks.Should().BeTrue();
        scene.Panel.IsLive.Should().BeFalse();
    }

    /// <summary>
    /// The funding cell formats through the shared helper at the panel's
    /// own culture (T-197): a derivative row shows the wire fraction as a
    /// signed percent, a spot row leaves the cell empty — the honest null,
    /// never a fabricated zero.
    /// </summary>
    [Fact]
    public void TheFundingCell_FormatsTheFraction_AndKeepsTheSpotNullEmpty()
    {
        var scene = new Scene();

        scene.Panel.ApplySnapshot(new ScreenerSnapshot(
            [
                new ScreenerRow("PPP/USDT:USDT", 100m, 10m, 2m, 0.0001m),
                new ScreenerRow("SSS/USDT", 25m, 20m, 5m),
            ],
            ScreenerFeedState.Live));

        scene.Panel.Rows[0].Funding.Should().Be(
            WatchlistRowPresentation.FormatFundingRate(0.0001m, CultureInfo.CurrentCulture),
            "the row formats the fraction through the shared helper at the panel's culture");
        scene.Panel.Rows[0].Funding.Should().StartWith("+", "a positive rate keeps its explicit plus");
        scene.Panel.Rows[1].Funding.Should().BeEmpty("a spot row's absent funding is never a fabricated zero");
    }

    /// <summary>The catalog-unavailable page owns the reason line, never the waiting one.</summary>
    [Fact]
    public void TheCatalogUnavailablePage_OwnsTheReasonLine()
    {
        var scene = new Scene();

        scene.Panel.ApplySnapshot(new ScreenerSnapshot([], ScreenerFeedState.CatalogUnavailable));

        scene.Panel.IsCatalogUnavailable.Should().BeTrue();
        scene.Panel.IsWaitingTicks.Should().BeFalse(
            "the two honest states are exclusive: the reason line never shows over a served catalog");
        scene.Panel.HasRows.Should().BeFalse();
    }

    #endregion Snapshot apply

    #region Presets

    /// <summary>The gainers preset keeps only the positive change, descending.</summary>
    [Fact]
    public void TheGainersPreset_KeepsThePositiveChangeDescending()
    {
        var scene = new Scene();
        scene.Panel.ApplySnapshot(MixedPage());

        scene.Panel.SelectedPresetIndex = 1;

        scene.Panel.Rows.Select(static row => row.Symbol)
            .Should().Equal(
                (IEnumerable<string>)["CCC/USDT", "AAA/USDT"],
                "only the positive changes survive, strongest first");
    }

    /// <summary>The losers preset keeps only the negative change, ascending.</summary>
    [Fact]
    public void TheLosersPreset_KeepsTheNegativeChangeAscending()
    {
        var scene = new Scene();
        scene.Panel.ApplySnapshot(MixedPage());

        scene.Panel.SelectedPresetIndex = 2;

        scene.Panel.Rows.Select(static row => row.Symbol)
            .Should().Equal(
                (IEnumerable<string>)["BBB/USDT"],
                "only the negative changes survive, deepest first");
    }

    /// <summary>The volume preset sorts by the 24h volume descending.</summary>
    [Fact]
    public void TheVolumePreset_SortsByThe24hVolumeDescending()
    {
        var scene = new Scene();
        scene.Panel.ApplySnapshot(MixedPage());

        scene.Panel.SelectedPresetIndex = 3;

        scene.Panel.Rows.Select(static row => row.Symbol)
            .Should().Equal(
                (IEnumerable<string>)["BBB/USDT", "CCC/USDT", "AAA/USDT"],
                "the whole table rides the volume order, biggest first");
    }

    /// <summary>The All preset restores the adapter's own order.</summary>
    [Fact]
    public void TheAllPreset_RestoresTheAdaptersOrder()
    {
        var scene = new Scene();
        scene.Panel.ApplySnapshot(MixedPage());
        scene.Panel.SelectedPresetIndex = 3;

        scene.Panel.SelectedPresetIndex = 0;

        scene.Panel.Rows.Select(static row => row.Symbol)
            .Should().Equal(
                (IEnumerable<string>)["AAA/USDT", "BBB/USDT", "CCC/USDT"],
                "All is the feed's adapter order, never a leftover sort");
    }

    #endregion Presets

    #region Column sorts

    /// <summary>A column press retires the preset to All and sorts manually.</summary>
    [Fact]
    public async Task AColumnPress_RetiresThePresetAndSortsManually()
    {
        var scene = new Scene();
        scene.Panel.ApplySnapshot(MixedPage());
        scene.Panel.SelectedPresetIndex = 1;

        await scene.Panel.SortByChangeCommand.Execute(RxVoid.Default);

        scene.Panel.SelectedPresetIndex.Should().Be(0,
            "a manual sort owns the whole table — the preset retires to All");
        scene.Panel.Rows.Select(static row => row.Symbol)
            .Should().Equal(
                (IEnumerable<string>)["BBB/USDT", "AAA/USDT", "CCC/USDT"],
                "the preset already rode the descending change, so the press flips it: the full table rides the ascending change (the negative row is back in)");

        await scene.Panel.SortByChangeCommand.Execute(RxVoid.Default);

        scene.Panel.Rows.Select(static row => row.Symbol)
            .Should().Equal(
                (IEnumerable<string>)["CCC/USDT", "AAA/USDT", "BBB/USDT"],
                "a repeat press on the same column flips the direction back");
    }

    /// <summary>A new column takes the descending default; ties resolve by the ordinal symbol text.</summary>
    [Fact]
    public async Task ANewColumn_TakesTheDescendingDefault_AndTiesResolveByTheOrdinalText()
    {
        var scene = new Scene();
        scene.Panel.ApplySnapshot(new ScreenerSnapshot(
            [
                Row("BBB/USDT", 100m, 10m, 0m),
                Row("AAA/USDT", 50m, 10m, 0m),
            ],
            ScreenerFeedState.Live));

        await scene.Panel.SortByVolumeCommand.Execute(RxVoid.Default);

        scene.Panel.Rows.Select(static row => row.Symbol)
            .Should().Equal(
                (IEnumerable<string>)["AAA/USDT", "BBB/USDT"],
                "equal volumes ride the ordinal text order (the tie-break ascends the text), never the map's internal order");

        // A different column starts fresh at its own descending default —
        // the flipped volume direction does not leak into it.
        await scene.Panel.SortByPriceCommand.Execute(RxVoid.Default);

        scene.Panel.Rows.Select(static row => row.Symbol)
            .Should().Equal(
                (IEnumerable<string>)["BBB/USDT", "AAA/USDT"],
                "the price column's first press is the descending default");
    }

    /// <summary>The sort rebuilds from the held page immediately — no new page needed.</summary>
    [Fact]
    public async Task TheSortRebuilds_FromTheHeldPageImmediately()
    {
        var scene = new Scene();
        scene.Panel.ApplySnapshot(MixedPage());

        // One page in; the sorts re-plan locally from the held page.
        await scene.Panel.SortByVolumeCommand.Execute(RxVoid.Default);
        await scene.Panel.SortByPriceCommand.Execute(RxVoid.Default);

        scene.Panel.Rows.Select(static row => row.Symbol)
            .Should().Equal(
                (IEnumerable<string>)["AAA/USDT", "BBB/USDT", "CCC/USDT"],
                "the price sort re-planned the held page immediately, strongest price first");
    }

    #endregion Column sorts

    #region Row press route

    /// <summary>A parsable row press writes the workspace symbol (no substitutions).</summary>
    [Fact]
    public async Task AParsableRowPress_WritesTheWorkspaceSymbol()
    {
        var scene = new Scene();
        scene.Chart.SelectedSymbol = Symbol.Parse("ETH/USDT");

        await scene.Panel.RouteRowAsync(DisplayRow("BTC/USDT"));

        scene.Chart.SelectedSymbol.Should().Be(Symbol.Parse("BTC/USDT"),
            "the pressed row's text IS the new workspace symbol");
    }

    /// <summary>An unparsable press is refused: the workspace symbol stays exactly what it was.</summary>
    [Fact]
    public async Task AnUnparsableRowPress_IsRefusedWithoutASubstitute()
    {
        var scene = new Scene();
        scene.Chart.SelectedSymbol = Symbol.Parse("ETH/USDT");

        await scene.Panel.RouteRowAsync(DisplayRow(string.Empty));
        await scene.Panel.RouteRowAsync(DisplayRow("   "));

        scene.Chart.SelectedSymbol.Should().Be(Symbol.Parse("ETH/USDT"),
            "an empty row text owns no route and never invents a substitute symbol");
    }

    #endregion Row press route

    #region Trend cells and routes

    /// <summary>
    /// The absent trend table formats every cell the honest warming state:
    /// no cached depth is never dressed up as a verdict (T-239).
    /// </summary>
    [Fact]
    public void TheAbsentTrendTable_FormatsEveryCellTheHonestWarmingState()
    {
        var scene = new Scene();
        scene.Panel.ApplySnapshot(MixedPage());

        foreach (var row in scene.Panel.Rows)
        {
            row.TrendM15.Should().Be(WarmingCell());
            row.TrendH1.Should().Be(WarmingCell());
            row.TrendH4.Should().Be(WarmingCell());
            row.TrendD1.Should().Be(WarmingCell());
        }
    }

    /// <summary>
    /// A published trend table reformats only its own symbols' cells —
    /// a bull cell carries the up tint, a bear cell the down tint, warming
    /// and range the plain ink — while symbols the table does not name
    /// stay warming and the standing preset and sort survive untouched.
    /// </summary>
    [Fact]
    public void AnAppliedTrendTable_ReformatsOnlyItsSymbols_Cells()
    {
        var scene = new Scene();
        scene.Panel.ApplySnapshot(MixedPage());

        scene.Panel.ApplyTrends(new MtfTrendSnapshot(
            DateTimeOffset.UtcNow,
            new Dictionary<string, MtfSymbolTrend>(StringComparer.Ordinal)
            {
                ["AAA/USDT"] = new(
                    "AAA/USDT",
                    MtfTrendState.Bull,
                    MtfTrendState.Bear,
                    MtfTrendState.Range,
                    MtfTrendState.Warming),
            }));

        var aaa = scene.Panel.Rows[0];
        aaa.TrendM15.Should().Be(new ScreenerTrendCellViewModel("Screener.Trend.Bull", true, false),
            "a bull verdict formats the up-tinted word");
        aaa.TrendH1.Should().Be(new ScreenerTrendCellViewModel("Screener.Trend.Bear", false, true),
            "a bear verdict formats the down-tinted word");
        aaa.TrendH4.Should().Be(ScreenerTrendCellViewModel.Plain("Screener.Trend.Range"),
            "a range verdict keeps the plain ink");
        aaa.TrendD1.Should().Be(ScreenerTrendCellViewModel.Plain("Screener.Trend.Warming"));
        scene.Panel.Rows[1].TrendM15.Should().Be(WarmingCell(),
            "a symbol the table does not name stays the honest warming cell");
        scene.Panel.Rows.Select(static row => row.Symbol)
            .Should().Equal(
                (IEnumerable<string>)["AAA/USDT", "BBB/USDT", "CCC/USDT"],
                "the trend re-format rides the standing preset and sort");
    }

    /// <summary>A trend-cell press routes the row's symbol into the workspace AT the cell's own frame.</summary>
    [Fact]
    public async Task ATrendCellPress_WritesTheSymbolAndTheFrame()
    {
        var scene = new Scene();
        scene.Chart.SelectedSymbol = Symbol.Parse("ETH/USDT");
        scene.Chart.SelectedTimeFrame = TimeFrame.M15;

        await scene.Panel.RouteRowAsync(DisplayRow("BTC/USDT"), TimeFrame.H4);

        scene.Chart.SelectedSymbol.Should().Be(Symbol.Parse("BTC/USDT"),
            "the pressed cell's row text IS the new workspace symbol");
        scene.Chart.SelectedTimeFrame.Should().Be(TimeFrame.H4,
            "the pressed cell's frame rides after the symbol write");
    }

    /// <summary>An unparsable trend-cell press is refused: neither the symbol nor the frame moves.</summary>
    [Fact]
    public async Task AnUnparsableTrendCellPress_IsRefusedWithoutSubstitutes()
    {
        var scene = new Scene();
        scene.Chart.SelectedSymbol = Symbol.Parse("ETH/USDT");
        scene.Chart.SelectedTimeFrame = TimeFrame.M15;

        await scene.Panel.RouteRowAsync(DisplayRow("   "), TimeFrame.D1);

        scene.Chart.SelectedSymbol.Should().Be(Symbol.Parse("ETH/USDT"),
            "an unroutable row text invents no substitute symbol");
        scene.Chart.SelectedTimeFrame.Should().Be(TimeFrame.M15,
            "a refused press never writes the frame the route would have carried");
    }

    #endregion Trend cells and routes
}
