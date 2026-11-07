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
using ReactiveUI;
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

/// <summary>The half-day-agnostic status byte ReactiveUI.Primitives names RxVoid.</summary>
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// The global symbol palette's ladder behavior (T-095): the Ctrl+K toggle
/// opens instantly over the snapshot standing (loading / empty / no-matches
/// are the three honest rows states), the blank page leads with the
/// session's recents, then the favorites, then the natural order, a typed
/// query re-ranks across adapters and re-points the keyboard selection to
/// the head, Enter applies the highlighted row - a cross-exchange pair
/// switches the adapter FIRST and writes the chart SECOND - and the focus
/// enters and leaves through the interactions the window hosts.
/// </summary>
/// <remarks>
/// Runs inside <see cref="AvaloniaDispatcherSerialCollection"/> (the
/// process-wide Avalonia dispatcher pin must stay exclusive): the palette's
/// snapshot rides <c>ObserveOnUiThread</c>, so a refresh lands and every
/// throttled re-rank only reach the test through
/// <see cref="AvaloniaDispatcherPump.DrainAsync"/>.
/// </remarks>
[Collection("AvaloniaDispatcherSerial")]
public sealed class SymbolPaletteViewModelTests
{
    /// <summary>
    /// The process-wide dispatcher ownership is claimed ONCE by
    /// <see cref="AvaloniaDispatcherPump"/> - every ObserveOnUiThread ride
    /// posts to it, and only a DrainAsync lands what was queued before it.
    /// </summary>
    static SymbolPaletteViewModelTests() => AvaloniaDispatcherPump.EnsureInstalled();

    #region Fake adapter

    /// <summary>
    /// Market data fake with a mutable symbol catalog: every GetSymbolsAsync
    /// serves the list standing at call time, so a fact that replaces it
    /// drives the source's next composition directly.
    /// </summary>
    private sealed class FakeMarketData : IMarketDataAdapter
    {
        private IReadOnlyList<Symbol> _symbols;

        public FakeMarketData(string id, string name, params string[] symbols)
        {
            Id = id;
            Name = name;
            _symbols = symbols.Select(Symbol.Parse).ToList();
        }

        public string Id { get; }

        public string Name { get; }

        public bool IsConnected => true;

        public ExchangeCapabilities Capabilities => ExchangeCapabilities.MarketData;

        public int SymbolFetches { get; private set; }

        /// <summary>Replace the served catalog; the next fetch composes it.</summary>
        public void ReplaceSymbols(params string[] symbols) =>
            _symbols = symbols.Select(Symbol.Parse).ToList();

        public async Task<IReadOnlyList<Symbol>> GetSymbolsAsync(CancellationToken ct = default)
        {
            SymbolFetches++;
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

    #endregion Fake adapter

    #region Scene kit

    /// <summary>The throttle fires on the default scheduler; the drain lands it. A comfortable margin over the threshold.</summary>
    private const int ThrottleSettleMarginMilliseconds = 80;

    /// <summary>One scene: the facade, the chart, the source and the palette over fake adapters.</summary>
    private sealed class Scene
    {
        public Scene(params FakeMarketData[] adapters)
        {
            Adapters = adapters;
            Changes = new Subject<SettingsChange>();
            Settings = Substitute.For<ISettingsService>();
            Settings.Changes.Returns(Changes);
            Settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(string.Empty);
            Settings.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(Task.CompletedTask);

            Exchange = new ActiveExchangeService(
                adapters,
                Settings,
                NullLogger<ActiveExchangeService>.Instance);
            Chart = CreateChart(Exchange, Settings);
            Source = new SymbolPaletteSource(
                Exchange,
                NullLogger<SymbolPaletteSource>.Instance);
            Palette = new SymbolPaletteViewModel(
                Exchange,
                Source,
                Settings,
                Chart,
                NullLogger<SymbolPaletteViewModel>.Instance);

            Palette.FocusQueryRequested.RegisterHandler(context =>
            {
                FocusRequests++;
                context.SetOutput(RxVoid.Default);
                return Task.CompletedTask;
            });
            Palette.FocusReturnRequested.RegisterHandler(context =>
            {
                FocusReturns++;
                context.SetOutput(RxVoid.Default);
                return Task.CompletedTask;
            });
        }

        public FakeMarketData[] Adapters { get; }

        public ISettingsService Settings { get; }

        public Subject<SettingsChange> Changes { get; }

        public ActiveExchangeService Exchange { get; }

        public ChartViewModel Chart { get; }

        public SymbolPaletteSource Source { get; }

        public SymbolPaletteViewModel Palette { get; }

        /// <summary>How often the open asked the window to focus the query box.</summary>
        public int FocusRequests { get; private set; }

        /// <summary>How often a close landed the focus return.</summary>
        public int FocusReturns { get; private set; }

        /// <summary>Pin a favorites document the palette's open-time read serves.</summary>
        public void WithFavorites(string favoritesJson) =>
            Settings.GetAsync<string>(
                    UiSettingsKeys.WorkspaceSection,
                    UiSettingsKeys.WorkspaceFavoriteSymbols,
                    Arg.Any<string>())
                .Returns(favoritesJson);

        /// <summary>Compose the source once and land the marshaled snapshot.</summary>
        public async Task SettleAsync()
        {
            await Source.RefreshOnceAsync(CancellationToken.None);
            await AvaloniaDispatcherPump.DrainAsync();
        }

        /// <summary>Type a query, let the throttle settle and drain the marshal.</summary>
        public async Task TypeQueryAsync(string query)
        {
            Palette.Query = query;
            await Task.Delay(
                TimeSpan.FromMilliseconds(
                    SymbolPaletteViewModel.QueryThrottleMilliseconds +
                    ThrottleSettleMarginMilliseconds));
            await AvaloniaDispatcherPump.DrainAsync();
        }

        /// <summary>Ctrl+K: open the closed palette or close the open one.</summary>
        public async Task ToggleAsync() => await Palette.ToggleCommand.Execute(RxVoid.Default);

        /// <summary>The published page as "exchange:symbol" names.</summary>
        public string[] RowNames() =>
            Palette.Rows.Select(row => $"{row.ExchangeId}:{row.Symbol.Normalized}").ToArray();

        /// <summary>The page position of one pair, or -1.</summary>
        public int IndexOf(string exchangeId, string normalized)
        {
            var row = Palette.Rows.FirstOrDefault(item =>
                item.ExchangeId == exchangeId && item.Symbol.Normalized == normalized);
            return row is null ? -1 : Palette.Rows.IndexOf(row);
        }

        /// <summary>Apply the pair at the given page position through Enter's command.</summary>
        public async Task ApplyAtAsync(int index)
        {
            Palette.SelectedIndex = index;
            await Palette.ApplySelectionCommand.Execute(RxVoid.Default);
        }
    }

    /// <summary>
    /// Read a ReactiveCommand's gate: the <see cref="ReactiveCommand{RxVoid, RxVoid}.CanExecute"/>
    /// stream's current value, sampled fresh at the fact's call.
    /// </summary>
    private static async Task<bool> GatedAsync(ReactiveCommand<RxVoid, RxVoid> command) =>
        await command.CanExecute.FirstAsync();

    private static ChartViewModel CreateChart(ActiveExchangeService service, ISettingsService settings) => new(
        service,
        new SymbolListProvider(service, NullLogger<SymbolListProvider>.Instance),
        settings,
        new EmptyIndicatorCatalog(),
        Substitute.For<ICsvExportService>(),
        Substitute.For<ILocalizationManager>(),
        new EmptyCandleCache(),
        new CandleBackfillService(new EmptyCandleCache(), NullLogger<CandleBackfillService>.Instance),
        NullLogger<ChartViewModel>.Instance);

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

    #endregion Scene kit

    #region Open and close

    [Fact]
    public async Task TheFirstToggle_OpensTheOverlay_FocusesTheQuery_AndPublishesTheBlankPage()
    {
        var scene = new Scene(
            new FakeMarketData("beta", "Beta", "BTC/USDT", "ETH/BTC"),
            new FakeMarketData("alpha", "Alpha", "SOL/USDT"));
        await scene.SettleAsync();

        await scene.ToggleAsync();

        scene.Palette.IsOpen.Should().BeTrue("Ctrl+K across a closed palette opens it");
        scene.FocusRequests.Should().Be(1, "the open asks the window to focus the query box");
        scene.Palette.Query.Should().BeEmpty("a fresh open starts from a clean query");
        scene.Palette.SelectedIndex.Should().Be(0, "the selection stands on the page's head");
        scene.Palette.HasNoMatches.Should().BeFalse();
        scene.Palette.IsSourceWaiting.Should().BeFalse();
        scene.RowNames().Should().HaveCount(
            3,
            "the blank query pages the registration order across all adapters").And
            .Equal(new[] { "beta:BTC/USDT", "beta:ETH/BTC", "alpha:SOL/USDT" });
    }

    [Fact]
    public async Task TheSecondToggle_ClosesTheOverlay_AndReturnsTheFocus()
    {
        var scene = new Scene(new FakeMarketData("beta", "Beta", "BTC/USDT"));
        await scene.ToggleAsync();

        await scene.ToggleAsync();

        scene.Palette.IsOpen.Should().BeFalse("Ctrl+K across an open palette closes it");
        scene.FocusReturns.Should().Be(1, "the close returns the pre-open focus");
    }

    [Fact]
    public async Task TheOverlay_Reopens_WithAResetQueryAndAFreshFocusAsk()
    {
        var scene = new Scene(new FakeMarketData("beta", "Beta", "BTC/USDT", "ETH/BTC"));
        await scene.SettleAsync();
        await scene.ToggleAsync();
        scene.Palette.Query = "eth";

        await scene.ToggleAsync(); // close (Ctrl+K again)
        await scene.ToggleAsync(); // reopen

        scene.Palette.IsOpen.Should().BeTrue();
        scene.Palette.Query.Should().BeEmpty("every open resets the query: a stale search never filters the next open");
        scene.FocusRequests.Should().Be(2, "the focus ask rides every open");
        scene.FocusReturns.Should().Be(1, "the focus return rides only the one close");
        scene.RowNames().Should().HaveCount(
            2,
            "the reopened page is the blank one again, not the stale query's").And
            .Equal(new[] { "beta:BTC/USDT", "beta:ETH/BTC" });
    }

    #endregion Open and close

    #region The honest states

    [Fact]
    public async Task AnUnrefreshedSource_ShowsTheLoadingState()
    {
        var scene = new Scene(new FakeMarketData("beta", "Beta", "BTC/USDT"));
        await scene.ToggleAsync();

        scene.Palette.HasRows.Should().BeFalse();
        scene.Palette.IsSourceWaiting.Should().BeTrue(
            "the first fetch never finished: the palette does not lie about loading");
        scene.Palette.HasNoMatches.Should().BeFalse();
        scene.Palette.HasNoInstruments.Should().BeFalse();
    }

    [Fact]
    public async Task AnEmptySettledSource_ShowsTheEmptyState()
    {
        var scene = new Scene(
            new FakeMarketData("beta", "Beta"),
            new FakeMarketData("alpha", "Alpha"));
        await scene.SettleAsync();
        await scene.ToggleAsync();

        scene.Palette.IsSourceWaiting.Should().BeFalse();
        scene.Palette.HasNoInstruments.Should().BeTrue("every registered adapter settled on an empty catalog");
        scene.Palette.HasNoMatches.Should().BeFalse("the empty state belongs to the source, not to the query");
    }

    [Fact]
    public async Task AQueryMiss_OverANonEmptySource_ShowsTheNoMatchesState()
    {
        var scene = new Scene(
            new FakeMarketData("beta", "Beta", "BTC/USDT", "ETH/BTC"),
            new FakeMarketData("alpha", "Alpha", "SOL/USDT"));
        await scene.SettleAsync();
        await scene.ToggleAsync();

        await scene.TypeQueryAsync("XR");

        scene.Palette.HasRows.Should().BeFalse("neither the clean nor the single-typo alignment matches");
        scene.Palette.HasNoMatches.Should().BeTrue("the source served instruments, the query only found none");
        scene.Palette.IsSourceWaiting.Should().BeFalse();
        scene.Palette.HasNoInstruments.Should().BeFalse();
    }

    #endregion The honest states

    #region The ranking

    [Fact]
    public async Task AQuery_RanksAcrossAdapters_TheExactPrefixAboveTheWordStart()
    {
        var scene = new Scene(
            new FakeMarketData("beta", "Beta", "BTC/USDT", "ETH/BTC"),
            new FakeMarketData("alpha", "Alpha", "USDT/USD", "ADA/BTC"));
        await scene.SettleAsync();
        await scene.ToggleAsync();

        await scene.TypeQueryAsync("USDT");

        scene.RowNames().Should().HaveCount(
            2,
            "the exact prefix ranks first and the pair-slashed word start follows, across adapters").And
            .Equal(new[] { "alpha:USDT/USD", "beta:BTC/USDT" });
        scene.Palette.SelectedIndex.Should().Be(0, "every publish re-points the keyboard selection to the head");
    }

    [Fact]
    public async Task TheBlankPage_LeadsWithRecentsThenFavoritesThenNaturalOrder()
    {
        var scene = new Scene(
            new FakeMarketData("beta", "Beta", "BTC/USDT", "ETH/BTC"),
            new FakeMarketData("alpha", "Alpha", "SOL/USDT"));
        scene.WithFavorites("""{ "alpha": ["SOL/USDT"] }""");
        await scene.SettleAsync();

        // One apply: the active exchange serves beta, so the beta pair never
        // switches anything and lands straight into the session's recents.
        await scene.ToggleAsync();
        await scene.ApplyAtAsync(scene.IndexOf("beta", "BTC/USDT"));
        await scene.ToggleAsync();

        var rows = scene.Palette.Rows;
        rows.Select(row => $"{row.ExchangeId}:{row.Symbol.Normalized}").Should().HaveCount(
            3,
            "recents first, favorites second, the natural order fills the rest").And
            .Equal(new[] { "beta:BTC/USDT", "alpha:SOL/USDT", "beta:ETH/BTC" });
        rows[0].IsRecent.Should().BeTrue();
        rows[0].IsFavorite.Should().BeFalse();
        rows[1].IsFavorite.Should().BeTrue("the marks the settings carry star the row");
        rows[1].IsRecent.Should().BeFalse();
        rows[2].IsFavorite.Should().BeFalse();
        rows[2].IsRecent.Should().BeFalse();
    }

    [Fact]
    public async Task TheRecentsList_IsBounded_Deduped_AndNewestFirst()
    {
        var scene = new Scene(
            new FakeMarketData(
                "beta",
                "Beta",
                "BTC/USDT", "ETH/BTC", "SOL/BTC", "XRP/BTC", "DOGE/BTC", "LTC/BTC", "ADA/BTC"),
            new FakeMarketData("alpha", "Alpha", "BTC/FDT", "ETH/FDT"));
        await scene.SettleAsync();

        // Nine distinct pairs, then the first one again: the list re-inserts
        // the repeat at its new head instead of duplicating, and never grows
        // past the bound.
        var applied = new[]
        {
            ("beta", "BTC/USDT"), ("beta", "ETH/BTC"), ("beta", "SOL/BTC"),
            ("beta", "XRP/BTC"), ("beta", "DOGE/BTC"), ("beta", "LTC/BTC"),
            ("beta", "ADA/BTC"), ("alpha", "BTC/FDT"), ("alpha", "ETH/FDT"),
            ("beta", "BTC/USDT"),
        };
        foreach (var (exchangeId, normalized) in applied)
        {
            await scene.ToggleAsync();
            await scene.ApplyAtAsync(scene.IndexOf(exchangeId, normalized));
        }

        await scene.ToggleAsync();

        scene.Palette.Rows.Should().HaveCount(
            SymbolPaletteViewModel.MaxRecents + 1,
            "the page caps the recents and fills the one leftover naturally");
        scene.Palette.Rows.Take(SymbolPaletteViewModel.MaxRecents).Should().OnlyContain(
            row => row.IsRecent, "the head is this session's applied pairs, newest first");
        scene.Palette.Rows[0].ExchangeId.Should().Be("beta");
        scene.Palette.Rows[0].Symbol.Normalized.Should().Be("BTC/USDT", "the last applied pair sits at the head");
        scene.Palette.Rows[1].Symbol.Normalized.Should().Be("ETH/FDT", "the repeat re-inserted; it never duplicated");
        scene.Palette.Rows[SymbolPaletteViewModel.MaxRecents].IsRecent.Should().BeFalse(
            "the leftover rows come from the natural order, not the recents");
    }

    [Fact]
    public async Task AFavoritesChange_WhileOpen_ReranksStraightAway()
    {
        var scene = new Scene(
            new FakeMarketData("beta", "Beta", "BTC/USDT", "ETH/BTC"),
            new FakeMarketData("alpha", "Alpha", "SOL/USDT"));
        await scene.SettleAsync();
        await scene.ToggleAsync();

        // The settings window saves a star for alpha's pair: the change
        // stream mirrors it into the palette without waiting for a reopen.
        scene.Changes.OnNext(new SettingsChange(
            UiSettingsKeys.WorkspaceSection,
            UiSettingsKeys.WorkspaceFavoriteSymbols,
            null,
            """{ "alpha": ["SOL/USDT"] }"""));

        var row = scene.Palette.Rows.Single(item =>
            item.ExchangeId == "alpha" && item.Symbol.Normalized == "SOL/USDT");
        row.IsFavorite.Should().BeTrue("a save while the palette stands open lands in the open page");
        scene.Palette.IsOpen.Should().BeTrue("the mirror re-ranks, it never closes anything");
    }

    #endregion The ranking

    #region The apply seam

    [Fact]
    public async Task ACrossExchangeApply_CarriesThePairIntoThePreference_SwitchesFirst_AndLandsItOnTheChart()
    {
        var scene = new Scene(
            new FakeMarketData("beta", "Beta", "BTC/USDT", "ETH/BTC"),
            new FakeMarketData("alpha", "Alpha", "SOL/USDT"));
        await scene.SettleAsync();
        await scene.ToggleAsync();

        // The T-017 discipline: the applied pair is placed into the SAME
        // workspace symbol preference the chart's own setter maintains
        // BEFORE the switch (so the switch-kicked bootstrap reads it as the
        // preference), the exchange activation follows and the delivered
        // selection raises LAST — while the new exchange is already the
        // current one. Both records ride the same (test) thread inside the
        // command's Execute, so the relative order is real.
        var workspaceWrites = new List<(string Section, string Key, string Value)>();
        scene.Settings
            .WhenForAnyArgs(settings => settings.SetAsync<string>(null!, null!, null!))
            .Do(call => workspaceWrites.Add((
                call.ArgAt<string>(0),
                call.ArgAt<string>(1),
                call.ArgAt<string>(2)!)));

        var exchangeTrace = new List<string>();
        using var exchangeSeen = scene.Exchange.AdapterChanges.Skip(1).Subscribe(
            adapter => exchangeTrace.Add(adapter.Id));
        var chartTrace = new List<string>();
        using var chartSeen = scene.Chart.SymbolSelections.Subscribe(
            symbol => chartTrace.Add(symbol.Normalized));

        await scene.ApplyAtAsync(scene.IndexOf("alpha", "SOL/USDT"));
        await AvaloniaDispatcherPump.DrainAsync();

        workspaceWrites.Should().HaveCount(
            2,
            "the applied pair and the exchange id are the only writes the apply makes").And
            .Equal(("Workspace", "SymbolId", "SOL/USDT"), ("Workspace", "ExchangeId", "alpha"));
        exchangeTrace.Should().Equal(
            ["alpha"],
            "the activation emission is the only adapter change the cross apply causes");
        chartTrace.Last().Should().Be(
            "SOL/USDT",
            "the delivered selection is the applied pair; no rebase interrupts it");
        chartTrace.IndexOf("SOL/USDT").Should().BeGreaterThan(
            exchangeTrace.IndexOf("alpha"),
            "the symbol raises while its exchange is already the active one");
        scene.Exchange.Current.Id.Should().Be("alpha");
        scene.Chart.SelectedSymbol.Should().Be(Symbol.Parse("SOL/USDT"));
        scene.Palette.IsOpen.Should().BeFalse("the apply closes the palette");
        scene.FocusReturns.Should().Be(1, "the close lands the focus return");
        scene.Palette.Query.Should().BeEmpty();
    }

    [Fact]
    public async Task AnApplyOnTheCurrentAdapter_NeverSwitches()
    {
        var scene = new Scene(
            new FakeMarketData("beta", "Beta", "BTC/USDT", "ETH/BTC"),
            new FakeMarketData("alpha", "Alpha", "SOL/USDT"));
        await scene.SettleAsync();
        await scene.ToggleAsync();

        var trace = new List<string>();
        using var exchangeSeen = scene.Exchange.AdapterChanges.Skip(1).Subscribe(
            adapter => trace.Add(adapter.Id));

        await scene.ApplyAtAsync(scene.IndexOf("beta", "ETH/BTC"));

        trace.Should().BeEmpty("the row's exchange is the active one: no switch, rewrite or emission");
        scene.Exchange.Current.Id.Should().Be("beta");
        scene.Chart.SelectedSymbol.Should().Be(Symbol.Parse("ETH/BTC"));
    }

    #endregion The apply seam

    #region The keyboard selection

    [Fact]
    public async Task TheKeyboardSelection_MovesAndClampsToTheRows()
    {
        var scene = new Scene(
            new FakeMarketData("beta", "Beta", "BTC/USDT", "ETH/BTC", "SOL/BTC"),
            new FakeMarketData("alpha", "Alpha", "ADA/BTC"));
        await scene.SettleAsync();
        await scene.ToggleAsync();

        scene.Palette.SelectedIndex.Should().Be(0);
        (await GatedAsync(scene.Palette.MoveSelectionDownCommand)).Should().BeTrue();
        await scene.Palette.MoveSelectionDownCommand.Execute(RxVoid.Default);
        scene.Palette.SelectedIndex.Should().Be(1);

        // One page-step never leaves the rows: a four-row page clamps the +10
        // to the tail.
        await scene.Palette.MoveSelectionPageDownCommand.Execute(RxVoid.Default);
        scene.Palette.SelectedIndex.Should().Be(3, "the page-step clamps to the tail row");

        (await GatedAsync(scene.Palette.MoveSelectionDownCommand)).Should()
            .BeFalse("there is no row after the tail");
        (await GatedAsync(scene.Palette.MoveSelectionPageDownCommand)).Should().BeFalse();

        await scene.Palette.MoveSelectionUpCommand.Execute(RxVoid.Default);
        scene.Palette.SelectedIndex.Should().Be(2);

        await scene.Palette.MoveSelectionPageUpCommand.Execute(RxVoid.Default);
        scene.Palette.SelectedIndex.Should().Be(0, "the page-up clamps to the head row");

        (await GatedAsync(scene.Palette.MoveSelectionUpCommand)).Should()
            .BeFalse("there is no row before the head");
        (await GatedAsync(scene.Palette.MoveSelectionPageUpCommand)).Should().BeFalse();
    }

    [Fact]
    public async Task TheApplyRoute_IsGatedOnAnOpenPaletteWithASelectableRow()
    {
        var scene = new Scene(new FakeMarketData("beta", "Beta", "BTC/USDT"));

        (await GatedAsync(scene.Palette.ApplySelectionCommand)).Should()
            .BeFalse("a closed palette applies nothing: Enter is swallowed by the route");
        (await GatedAsync(scene.Palette.CloseCommand)).Should()
            .BeFalse("Esc on the closed palette is gated too");
        (await GatedAsync(scene.Palette.MoveSelectionUpCommand)).Should()
            .BeFalse("the row keys are gated on the open palette as well");
        (await GatedAsync(scene.Palette.MoveSelectionPageDownCommand)).Should().BeFalse();

        await scene.SettleAsync();
        await scene.ToggleAsync();

        (await GatedAsync(scene.Palette.ApplySelectionCommand)).Should()
            .BeTrue("the head row is selectable");
        (await GatedAsync(scene.Palette.CloseCommand)).Should().BeTrue();
    }

    [Fact]
    public async Task TheRecentsFollowOnlyThePairsTheSnapshotStillServes()
    {
        var scene = new Scene(new FakeMarketData("beta", "Beta", "BTC/USDT", "ETH/BTC"));
        await scene.SettleAsync();
        await scene.ToggleAsync();
        await scene.ApplyAtAsync(scene.IndexOf("beta", "ETH/BTC"));

        scene.Adapters[0].ReplaceSymbols("BTC/USDT");
        await scene.SettleAsync();
        await scene.ToggleAsync();

        var rows = scene.Palette.Rows;
        rows.Should().HaveCount(1, "the exchange re-cataloged: the applied pair is served no more");
        rows[0].IsRecent.Should().BeFalse(
            "a dropped exchange's pair cannot re-apply, so the page drops it from the recents");
        scene.RowNames().Should().Equal("beta:BTC/USDT");
    }

    #endregion The keyboard selection
}
