namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Chart ViewModel behavior on a fake market data adapter without any
/// activation path (the workspace fact: WhenActivated never fires for
/// nested-panel DataContexts): the bootstrap runs in the constructor, the
/// selection drives the raw bars machine (Switch cancels the previous load)
/// and a failed bars load keeps the machine alive for later selections.
/// UI-thread hopping is not exercised — the constructor assignments land
/// through ObserveOnUiThread, which a test process does not pump.
/// </summary>
public sealed class ChartViewModelTests
{
    #region Fake Adapter

    /// <summary>
    /// Market data fake with a deterministic symbol list and per-symbol bars.
    /// Bars loads delay by <see cref="_barDelayMs"/> and honour the token, so
    /// the Rx Switch cancellation of a previous load is observable.
    /// </summary>
    private sealed class FakeMarketData : IMarketDataAdapter
    {
        private readonly int _barDelayMs;
        private readonly List<Symbol> _failingSymbols;

        public FakeMarketData(int barDelayMs = 0, IEnumerable<string>? failingSymbols = null)
        {
            _barDelayMs = barDelayMs;
            _failingSymbols = (failingSymbols ?? [])
                .Select(name => Symbol.Parse(name))
                .ToList();
        }

        public List<Symbol> SymbolsRequested { get; } = new();

        public List<Symbol> BarsRequested { get; } = new();

        public List<CancellationToken> BarsCancellationTokens { get; } = new();

        public string Id => "fake";

        public string Name => "Fake";

        public bool IsConnected => true;

        public ExchangeCapabilities Capabilities => ExchangeCapabilities.MarketData;

        public Task<IReadOnlyList<Symbol>> GetSymbolsAsync(CancellationToken ct = default)
        {
            SymbolsRequested.Add(Symbol.Parse("BTC/USDT"));

            return Task.FromResult<IReadOnlyList<Symbol>>([
                Symbol.Parse("BTC/USDT"),
                Symbol.Parse("ETH/BTC"),
                Symbol.Parse("SOL/USDC"),
            ]);
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

        private static decimal CloseFor(Symbol symbol) =>
            symbol.Normalized switch
            {
                "BTC/USDT" => 100m,
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

    #region Tests

    [Fact]
    public void Ctor_StartsTheSymbolsLoadWithoutActivation()
    {
        var marketData = new FakeMarketData();

        var chart = new ChartViewModel(
            marketData,
            new SymbolListProvider(marketData, NullLogger<SymbolListProvider>.Instance),
            NullLogger<ChartViewModel>.Instance);

        marketData.SymbolsRequested.Should().ContainSingle(
            "the bootstrap runs in the constructor, no WhenActivated involved");
    }

    [Fact]
    public void Selections_FlowThroughTheRawStream()
    {
        var marketData = new FakeMarketData();
        var chart = new ChartViewModel(
            marketData,
            new SymbolListProvider(marketData, NullLogger<SymbolListProvider>.Instance),
            NullLogger<ChartViewModel>.Instance);
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
        var chart = new ChartViewModel(
            marketData,
            new SymbolListProvider(marketData, NullLogger<SymbolListProvider>.Instance),
            NullLogger<ChartViewModel>.Instance);
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
        var chart = new ChartViewModel(
            marketData,
            new SymbolListProvider(marketData, NullLogger<SymbolListProvider>.Instance),
            NullLogger<ChartViewModel>.Instance);
        var latest = new TaskCompletionSource<IReadOnlyList<Bar>>();

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
        var chart = new ChartViewModel(
            marketData,
            new SymbolListProvider(marketData, NullLogger<SymbolListProvider>.Instance),
            NullLogger<ChartViewModel>.Instance);
        var delivered = new List<IReadOnlyList<Bar>>();

        using var subscription = chart.BarsUpdates.Subscribe(delivered.Add);

        chart.SelectedSymbol = Symbol.Parse("BTC/USDT");
        chart.SelectedSymbol = Symbol.Parse("ETH/BTC");

        marketData.BarsRequested.Should().HaveCount(2, "both selections attempted a load");
        delivered.Should().ContainSingle(
            "only the healthy symbol delivers bars; the failure must not end the machine");
        delivered[0].Should().ContainSingle().Which.Close.Should().Be(200m);
    }

    #endregion
}
