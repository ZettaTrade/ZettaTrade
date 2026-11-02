namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Chart ViewModel behavior on fake market data adapters without any
/// activation path (the workspace fact: WhenActivated never fires for
/// nested-panel DataContexts): the bootstrap runs at construction, the
/// selection drives the raw bars machine (Switch cancels the previous load)
/// and an exchange switch re-targets both the symbols bootstrap and the bars
/// load at the new adapter, cancelling the in-flight load of the previous
/// one. UI-thread hopping is not exercised — the assignments land through
/// ObserveOnUiThread, which a test process does not pump; the preselection
/// DECISION is pinned in its pure form via
/// <see cref="ChartViewModel.Preselect"/>.
/// </summary>
public sealed class ChartViewModelTests
{
    #region Fake Adapter

    /// <summary>
    /// Market data fake with a configurable identity, symbol list and
    /// per-symbol bars. Bars loads delay by <see cref="_barDelayMs"/> and
    /// honour the token, so the Rx Switch cancellation of a previous load is
    /// observable.
    /// </summary>
    private sealed class FakeMarketData : IMarketDataAdapter
    {
        private readonly int _barDelayMs;
        private readonly List<Symbol> _failingSymbols;
        private readonly IReadOnlyList<Symbol> _symbolList;
        private readonly decimal _btcClose;

        public FakeMarketData(
            int barDelayMs = 0,
            IEnumerable<string>? failingSymbols = null,
            string id = "fake",
            string name = "Fake",
            IReadOnlyList<string>? symbolNames = null,
            decimal btcClose = 100m)
        {
            _barDelayMs = barDelayMs;
            _failingSymbols = (failingSymbols ?? []).Select(Symbol.Parse).ToList();
            _symbolList = symbolNames is null
                ? [Symbol.Parse("BTC/USDT"), Symbol.Parse("ETH/BTC"), Symbol.Parse("SOL/USDC")]
                : symbolNames.Select(Symbol.Parse).ToList();
            _btcClose = btcClose;
            Id = id;
            Name = name;
        }

        public List<Symbol> SymbolsRequested { get; } = new();

        public List<Symbol> BarsRequested { get; } = new();

        public List<TimeFrame> BarsTimeFrames { get; } = new();

        public List<CancellationToken> BarsCancellationTokens { get; } = new();

        public string Id { get; }

        public string Name { get; }

        public bool IsConnected => true;

        public ExchangeCapabilities Capabilities => ExchangeCapabilities.MarketData;

        public Task<IReadOnlyList<Symbol>> GetSymbolsAsync(CancellationToken ct = default)
        {
            SymbolsRequested.Add(Symbol.Parse("BTC/USDT"));

            return Task.FromResult(_symbolList);
        }

        public async Task<IReadOnlyList<Bar>> GetBarsAsync(
            Symbol symbol,
            TimeFrame timeFrame,
            DateTimeOffset from,
            DateTimeOffset? to = null,
            int limit = 500,
            CancellationToken ct = default)
        {
            BarsRequested.Add(symbol);
            BarsTimeFrames.Add(timeFrame);
            BarsCancellationTokens.Add(ct);

            if (_failingSymbols.Contains(symbol))
            {
                throw new InvalidOperationException("no bars for this symbol");
            }

            if (_barDelayMs > 0)
            {
                await Task.Delay(_barDelayMs, ct);
            }

            // The symbol is marked in the close so delivered bars are verified
            // by value, not by timing.
            var close = CloseFor(symbol);

            return [new Bar(from, close - 1, close + 1, close - 2, close, 3)];
        }

        private decimal CloseFor(Symbol symbol) =>
            symbol.Normalized switch
            {
                "BTC/USDT" => _btcClose,
                "ETH/BTC" => 200m,
                _ => 300m,
            };

        #region Unused Market Data Surface

        public IObservable<Ticker> SubscribeTicker(Symbol symbol) => Observable.Never<Ticker>();

        public IObservable<MarketTrade> SubscribeTrades(Symbol symbol) => Observable.Never<MarketTrade>();

        public IObservable<OrderBook> SubscribeOrderBook(Symbol symbol, int depth = 20) =>
            Observable.Never<OrderBook>();

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        #endregion
    }

    #endregion

    #region Composition

    private static ActiveExchangeService CreateService(FakeMarketData first, FakeMarketData second)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
        return new ActiveExchangeService(
            [first, second],
            settings,
            NullLogger<ActiveExchangeService>.Instance);
    }

    private static ChartViewModel CreateChart(
        ActiveExchangeService service,
        ISettingsService? settings = null)
    {
        var timeFrameSettings = settings;
        if (timeFrameSettings is null)
        {
            timeFrameSettings = Substitute.For<ISettingsService>();
            timeFrameSettings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(string.Empty);
            timeFrameSettings.SetAsync(
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<string>())
                .Returns(Task.CompletedTask);
        }

        return new(
            service,
            new SymbolListProvider(service, NullLogger<SymbolListProvider>.Instance),
            timeFrameSettings,
            NullLogger<ChartViewModel>.Instance);
    }

    #endregion

    #region Tests

    [Fact]
    public void Ctor_StartsTheSymbolsLoadWithoutActivation()
    {
        var marketData = new FakeMarketData();
        var chart = CreateChart(CreateService(marketData, new FakeMarketData(id: "fake2", name: "Fake2")));

        marketData.SymbolsRequested.Should().ContainSingle(
            "the bootstrap runs in the constructor, no WhenActivated involved");
    }

    [Fact]
    public void Selections_FlowThroughTheRawStream()
    {
        var chart = CreateChart(CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")));
        var selections = new List<Symbol>();

        using var subscription = chart.SymbolSelections.Subscribe(selections.Add);

        chart.SelectedSymbol = Symbol.Parse("ETH/BTC");
        chart.SelectedSymbol = Symbol.Parse("SOL/USDC");

        selections.Should().BeEquivalentTo(
            [Symbol.Parse("ETH/BTC"), Symbol.Parse("SOL/USDC")],
            options => options.WithStrictOrdering());
    }

    [Fact]
    public void BarsUpdates_DeliverTheLatestSelection()
    {
        var marketData = new FakeMarketData();
        var chart = CreateChart(CreateService(marketData, new FakeMarketData(id: "fake2", name: "Fake2")));
        var delivered = new List<IReadOnlyList<Bar>>();

        using var subscription = chart.BarsUpdates.Subscribe(delivered.Add);

        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");

        marketData.BarsRequested.Should().ContainSingle().Which.Should().Be(Symbol.Parse("BTC/USDT"));
        delivered.Should().ContainSingle();
        delivered[0].Should().ContainSingle().Which.Close.Should().Be(100m);
    }

    [Fact]
    public async Task FastSwitch_CancelsThePreviousBarsLoad()
    {
        var marketData = new FakeMarketData(barDelayMs: 30);
        var chart = CreateChart(CreateService(marketData, new FakeMarketData(id: "fake2", name: "Fake2")));
        var latest = new TaskCompletionSource<IReadOnlyList<Bar>>(TaskCreationOptions.RunContinuationsAsynchronously);

        // At delivery time the winning load's lease must still be alive; a
        // completed load ends its own token later (Rx detaches on completion).
        var winnerCancelledAtDelivery = default(bool?);
        using var subscription = chart.BarsUpdates.Subscribe(
            bars =>
            {
                winnerCancelledAtDelivery = marketData.BarsCancellationTokens[1].IsCancellationRequested;
                latest.TrySetResult(bars);
            });

        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        chart.SelectedSymbol = Symbol.Parse("ETH/BTC");

        var winnerBars = await latest.Task.WaitAsync(TimeSpan.FromSeconds(5));

        winnerBars.Should().ContainSingle().Which.Close.Should().Be(200m);

        // The Switch dropped the BTC load: its token is cancelled and its bars
        // never win the race.
        marketData.BarsRequested.Should().HaveCount(2);
        marketData.BarsCancellationTokens[0].IsCancellationRequested.Should().BeTrue(
            "the previous load is cancelled with the switch");
        winnerCancelledAtDelivery.Should().BeFalse(
            "the winning load is still alive when its bars arrive");
    }

    [Fact]
    public void BarsLoadFailure_KeepsTheMachineAlive()
    {
        var marketData = new FakeMarketData(failingSymbols: ["BTC/USDT"]);
        var chart = CreateChart(CreateService(marketData, new FakeMarketData(id: "fake2", name: "Fake2")));
        var delivered = new List<IReadOnlyList<Bar>>();

        using var subscription = chart.BarsUpdates.Subscribe(delivered.Add);

        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        chart.SelectedSymbol = Symbol.Parse("ETH/BTC");

        marketData.BarsRequested.Should().HaveCount(2, "both selections attempted a load");
        delivered.Should().ContainSingle(
            "only the healthy symbol delivers bars; the failure must not end the machine");
        delivered[0].Should().ContainSingle().Which.Close.Should().Be(200m);
    }

    [Fact]
    public async Task ExchangeSwitch_MidLoad_CancelsThePreviousAdapterAndDeliversTheNewOne()
    {
        var slow = new FakeMarketData(barDelayMs: 60);
        var fast = new FakeMarketData(barDelayMs: 0, id: "fake2", name: "Fake2", btcClose: 777m);
        var service = CreateService(slow, fast);
        var chart = CreateChart(service);
        var delivered = new TaskCompletionSource<IReadOnlyList<Bar>>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = chart.BarsUpdates.Subscribe(bars => delivered.TrySetResult(bars));

        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        await Task.Delay(20); // let the slow load actually start

        // Act: switch to the second exchange while the slow load is in flight.
        await service.SwitchToAsync("fake2");

        var bars = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        bars.Should().ContainSingle().Which.Close.Should().Be(
            777m,
            "the delivered bars belong to the new exchange");
        slow.BarsRequested.Should().ContainSingle().Which.Should().Be(Symbol.Parse("BTC/USDT"));
        fast.BarsRequested.Should().ContainSingle().Which.Should().Be(Symbol.Parse("BTC/USDT"));
        slow.BarsCancellationTokens[0].IsCancellationRequested.Should().BeTrue(
            "the Switch dropped the in-flight load of the previous exchange");
    }

    [Fact]
    public async Task ExchangeSwitch_ReloadsSymbolsAndKeepsTheListedSelection()
    {
        var first = new FakeMarketData();
        var second = new FakeMarketData(id: "fake2", name: "Fake2");
        var service = CreateService(first, second);
        var chart = CreateChart(service);

        second.SymbolsRequested.Should().BeEmpty("the bootstrap belongs to the active exchange before the switch");

        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");

        // Act
        await service.SwitchToAsync("fake2");

        // Assert
        second.SymbolsRequested.Should().ContainSingle("the symbol bootstrap re-runs per exchange change");
        second.BarsRequested.Should().ContainSingle().Which.Should().Be(
            Symbol.Parse("BTC/USDT"),
            "the listed selection survives and reloads bars on the new exchange");
    }

    [Fact]
    public void Preselect_DecidesTheSelectionForTheNextExchange()
    {
        var listed = new[] { Symbol.Parse("BTC/USDT"), Symbol.Parse("ETH/BTC") };
        var empty = Array.Empty<Symbol>();

        // Still listed: the user's context survives the switch.
        ChartViewModel.Preselect(Symbol.Parse("BTC/USDT"), listed)
            .Should().Be(Symbol.Parse("BTC/USDT"));
        ChartViewModel.Preselect(Symbol.Parse("ETH/BTC"), listed)
            .Should().Be(Symbol.Parse("ETH/BTC"));

        // Not listed or absent: the first entry owns the workspace default.
        ChartViewModel.Preselect(Symbol.Parse("DOGE/USDT"), listed)
            .Should().Be(Symbol.Parse("BTC/USDT"));
        ChartViewModel.Preselect(null, listed)
            .Should().Be(Symbol.Parse("BTC/USDT"));

        // An empty list clears the selection with no substitute.
        ChartViewModel.Preselect(Symbol.Parse("BTC/USDT"), empty)
            .Should().BeNull();
    }

    [Fact]
    public void SelectedTimeFrame_DefaultsToH1_WithThePickerCatalog()
    {
        var chart = CreateChart(CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")));

        chart.SelectedTimeFrame.Should().Be(
            TimeFrame.H1,
            "today's hardcoded behavior is the default until a restore applies");
        chart.TimeFrames.Should().Equal(
            TimeFrame.M1,
            TimeFrame.M5,
            TimeFrame.M15,
            TimeFrame.M30,
            TimeFrame.H1,
            TimeFrame.H4,
            TimeFrame.D1,
            TimeFrame.W1,
            TimeFrame.MN1);
    }

    /// <summary>
    /// The persisted <c>UI.ChartTimeFrame</c> choice applies at the startup
    /// walk, BEFORE the first load: the first bars request already carries
    /// the restored frame (no H1 flash). A restore never re-persists the
    /// value it just read.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_RestoresThePersistedTimeFrame_BeforeTheFirstLoad()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("M15");
        var marketData = new FakeMarketData();
        var chart = CreateChart(CreateService(marketData, new FakeMarketData(id: "fake2", name: "Fake2")), settings);
        var delivered = new List<IReadOnlyList<Bar>>();

        using var subscription = chart.BarsUpdates.Subscribe(delivered.Add);

        await chart.InitializeAsync();

        chart.SelectedTimeFrame.Should().Be(TimeFrame.M15);
        await settings.DidNotReceive().SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());

        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");

        marketData.BarsTimeFrames.Should().ContainSingle().Which.Should().Be(
            TimeFrame.M15,
            "the restored frame owns the first load");
        delivered.Should().ContainSingle();
    }

    /// <summary>
    /// Every non-name stored value (absent, blank, numeric, garbage) keeps
    /// the running default instead of substituting a time frame silently —
    /// the unrecognized value is logged, not applied.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("9")]
    [InlineData("25")]
    public async Task InitializeAsync_UnrecognizedPersistedChoice_KeepsTheDefaultH1(string? storedValue)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(storedValue ?? string.Empty);
        var chart = CreateChart(CreateService(new FakeMarketData(), new FakeMarketData(id: "fake2", name: "Fake2")), settings);

        await chart.InitializeAsync();

        chart.SelectedTimeFrame.Should().Be(TimeFrame.H1);
    }

    [Fact]
    public async Task SelectedTimeFrame_Change_ReloadsTheChartAndPersistsTheChoice()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
        settings.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);
        var marketData = new FakeMarketData();
        var chart = CreateChart(CreateService(marketData, new FakeMarketData(id: "fake2", name: "Fake2")), settings);
        var delivered = new List<IReadOnlyList<Bar>>();

        using var subscription = chart.BarsUpdates.Subscribe(delivered.Add);

        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        chart.SelectedTimeFrame = TimeFrame.M5;

        marketData.BarsRequested.Should().HaveCount(2, "a time frame change is a reload trigger, not a no-op");
        marketData.BarsTimeFrames.Should().Equal([TimeFrame.H1, TimeFrame.M5]);
        delivered.Should().HaveCount(2);
        delivered[1].Should().ContainSingle().Which.Close.Should().Be(100m);

        // The user flip persists the enum name into the UI section.
        await settings.Received().SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartTimeFrame,
            "M5");
    }

    [Fact]
    public async Task FastTimeFrameFlip_DropsTheInFlightLoadAndDeliversTheWinner()
    {
        var marketData = new FakeMarketData(barDelayMs: 30);
        var chart = CreateChart(CreateService(marketData, new FakeMarketData(id: "fake2", name: "Fake2")));
        var latest = new TaskCompletionSource<IReadOnlyList<Bar>>(TaskCreationOptions.RunContinuationsAsynchronously);

        // At delivery time the winning load's lease must still be alive (the
        // same receipt contract as the exchange-switch cancellation test);
        // the winner is the THIRD load: H1 initial, M5 superseded, D1 wins.
        var supersededCancelledAtDelivery = default(bool?);
        using var subscription = chart.BarsUpdates.Subscribe(bars =>
        {
            supersededCancelledAtDelivery = marketData.BarsCancellationTokens[2].IsCancellationRequested;
            latest.TrySetResult(bars);
        });

        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        chart.SelectedTimeFrame = TimeFrame.M5;
        chart.SelectedTimeFrame = TimeFrame.D1;

        var winnerBars = await latest.Task.WaitAsync(TimeSpan.FromSeconds(5));

        winnerBars.Should().ContainSingle().Which.Close.Should().Be(100m);
        marketData.BarsRequested.Should().HaveCount(3, "every flip requested its own reload");
        marketData.BarsTimeFrames.Should().Equal([TimeFrame.H1, TimeFrame.M5, TimeFrame.D1]);
        marketData.BarsCancellationTokens[1].IsCancellationRequested.Should().BeTrue(
            "the Switch dropped the in-flight M5 load; only the D1 winner runs");
        supersededCancelledAtDelivery.Should().BeFalse(
            "the winning load is still alive when its bars arrive");
    }

    /// <summary>
    /// The persisted-value parse contract: enum names (any case, trimmed)
    /// restore their time frame; absent, blank, numeric or unrecognized
    /// values produce null (the caller keeps the default and logs).
    /// </summary>
    [Theory]
    [InlineData("M1", TimeFrame.M1)]
    [InlineData("m15", TimeFrame.M15)]
    [InlineData("  H4  ", TimeFrame.H4)]
    [InlineData("MN1", TimeFrame.MN1)]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("9", null)]
    [InlineData("garbage", null)]
    public void ParseStoredTimeFrame_PinsTheRestoreContract(string? stored, TimeFrame? expected) =>
        ChartViewModel.ParseStoredTimeFrame(stored).Should().Be(expected);

    #endregion
}
