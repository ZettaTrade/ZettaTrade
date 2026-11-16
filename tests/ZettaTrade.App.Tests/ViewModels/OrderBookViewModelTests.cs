namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Order book ViewModel behavior on a fake market data adapter: the default
/// symbol streams from the first frame, Rx Switch cancels the previous symbol
/// on change, an unparsable input keeps the current stream alive instead of
/// guessing, and a stream failure ends one symbol's data without killing the
/// pipeline for later symbols — and the T-246 imbalance chip publishes the
/// window's ratio on the marshaled lane (a knob change re-weighs the
/// standing book, a symbol switch resets the carried read).
/// </summary>
/// <remarks>
/// Rides <see cref="AvaloniaDispatcherSerialCollection"/> (T-138): the class
/// drains through <see cref="AvaloniaDispatcherPump"/> - a drain running
/// concurrently with any other dispatcher user's window poisons the shared
/// queue for everyone, so every dispatcher-contact class lives in the one
/// serial collection.
/// </remarks>
[Collection("AvaloniaDispatcherSerial")]
public sealed class OrderBookViewModelTests
{
    /// <summary>The marshal path posts to the shared dispatcher; install the deterministic pump world once.</summary>
    static OrderBookViewModelTests() => AvaloniaDispatcherPump.EnsureInstalled();

    #region Fake Adapter

    /// <summary>
    /// Market data fake with per-symbol live feeds and subscription counters
    /// (mimics <see cref="ExchangeAdapterBase"/>-shaped subjects so the switch
    /// semantics are observable from the tests).
    /// </summary>
    private sealed class FakeMarketData : IMarketDataAdapter
    {
        private readonly Dictionary<string, OrderBookFeed> _feeds = new();

        public string Id => "fake";

        public string Name => "Fake";

        public bool IsConnected => true;

        public ExchangeCapabilities Capabilities => ExchangeCapabilities.MarketData;

        public Dictionary<string, OrderBookFeed> Feeds => _feeds;

        public IObservable<OrderBook> SubscribeOrderBook(Symbol symbol, int depth = 20)
        {
            var feed = GetFeed(symbol);

            return Observable.Create<OrderBook>(observer =>
            {
                feed.Subscriptions++;
                var subscription = feed.Subject.Subscribe(observer);

                return Disposable.Create(() =>
                {
                    feed.Unsubscriptions++;
                    subscription.Dispose();
                });
            });
        }

        public OrderBookFeed GetFeed(Symbol symbol)
        {
            var key = symbol.Normalized;
            if (!_feeds.TryGetValue(key, out var feed))
            {
                feed = new OrderBookFeed();
                _feeds[key] = feed;
            }

            return feed;
        }

        #region Unused Market Data Surface

        public Task<IReadOnlyList<Symbol>> GetSymbolsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Symbol>>([]);

        public IObservable<Ticker> SubscribeTicker(Symbol symbol) => Observable.Never<Ticker>();

        public IObservable<MarketTrade> SubscribeTrades(Symbol symbol) => Observable.Never<MarketTrade>();

        public Task<IReadOnlyList<Bar>> GetBarsAsync(
            Symbol symbol,
            TimeFrame timeFrame,
            DateTimeOffset from,
            DateTimeOffset? to = null,
            int limit = 500,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Bar>>([]);

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        #endregion
    }

    private sealed class OrderBookFeed
    {
        public Subject<OrderBook> Subject { get; } = new();

        public int Subscriptions;

        public int Unsubscriptions;
    }

    /// <summary>Synthetic book: levels count per side, prices 100..(100-n).</summary>
    private static OrderBook Book(string symbol, int levels) =>
        new()
        {
            Symbol = Symbol.Parse(symbol),
            Bids = Enumerable.Range(0, levels)
                .Select(i => new PriceLevel(100m - i, i + 1))
                .ToList(),
            Asks = Enumerable.Range(0, levels)
                .Select(i => new PriceLevel(100.01m + i, i + 1))
                .ToList(),
            Timestamp = DateTimeOffset.UtcNow,
        };

    /// <summary>A two-sided lean book: the given quantities per side, unit prices — the window's read is hand-computable.</summary>
    private static OrderBook LeanBook(string symbol, decimal[] bidQuantities, decimal[] askQuantities)
    {
        var bids = new PriceLevel[bidQuantities.Length];
        for (var i = 0; i < bidQuantities.Length; i++)
        {
            bids[i] = new PriceLevel(100m - i, bidQuantities[i]);
        }

        var asks = new PriceLevel[askQuantities.Length];
        for (var i = 0; i < askQuantities.Length; i++)
        {
            asks[i] = new PriceLevel(100.01m + i, askQuantities[i]);
        }

        return new OrderBook
        {
            Symbol = Symbol.Parse(symbol),
            Bids = bids,
            Asks = asks,
            Timestamp = DateTimeOffset.UtcNow,
        };
    }

    #endregion

    #region Tests

    [Fact]
    public void Constructor_SubscribesTheDefaultSymbol()
    {
        var marketData = new FakeMarketData();

        var vm = new OrderBookViewModel(
            marketData,
            Observable.Never<Symbol>(),
            NullLogger<OrderBookViewModel>.Instance);

        vm.SymbolInput.Should().Be(OrderBookViewModel.DefaultSymbolInput);
        marketData.Feeds.Keys.Should().ContainSingle().Which.Should().Be("BTC/USDT");
    }

    [Fact]
    public void Publish_ReachesTheUpdatesStream()
    {
        var marketData = new FakeMarketData();
        var vm = new OrderBookViewModel(
            marketData,
            Observable.Never<Symbol>(),
            NullLogger<OrderBookViewModel>.Instance);
        var seen = new List<OrderBook>();

        using var subscription = vm.Updates.Subscribe(seen.Add);
        marketData.GetFeed(Symbol.Parse("BTC/USDT")).Subject.OnNext(Book("BTC/USDT", 3));

        seen.Should().ContainSingle();
    }

    [Fact]
    public void SymbolSwitch_CancelsTheOldStream()
    {
        var marketData = new FakeMarketData();
        var vm = new OrderBookViewModel(
            marketData,
            Observable.Never<Symbol>(),
            NullLogger<OrderBookViewModel>.Instance);
        var seen = new List<OrderBook>();

        using var subscription = vm.Updates.Subscribe(seen.Add);
        marketData.GetFeed(Symbol.Parse("BTC/USDT")).Subject.OnNext(Book("BTC/USDT", 1));
        var before = seen.Count;

        vm.SymbolInput = "ETH/BTC";

        marketData.Feeds["BTC/USDT"].Unsubscriptions.Should().BeGreaterThan(0);
        marketData.Feeds.Should().ContainKey("ETH/BTC");

        marketData.GetFeed(Symbol.Parse("BTC/USDT")).Subject.OnNext(Book("BTC/USDT", 2));
        seen.Count.Should().Be(before, "publishes to the old symbol must not arrive after the switch");

        marketData.GetFeed(Symbol.Parse("ETH/BTC")).Subject.OnNext(Book("ETH/BTC", 2));
        seen.Count.Should().Be(before + 1);
    }

    [Fact]
    public void UnparsableInput_KeepsThePreviousStreamAlive()
    {
        var marketData = new FakeMarketData();
        var vm = new OrderBookViewModel(
            marketData,
            Observable.Never<Symbol>(),
            NullLogger<OrderBookViewModel>.Instance);
        var seen = new List<OrderBook>();

        using var subscription = vm.Updates.Subscribe(seen.Add);

        vm.SymbolInput = "Not A Symbol";

        marketData.Feeds.Keys.Should().ContainSingle("no stream is subscribed for unexpected input");

        marketData.GetFeed(Symbol.Parse("BTC/USDT")).Subject.OnNext(Book("BTC/USDT", 1));
        seen.Should().ContainSingle("the current book keeps streaming");
    }

    [Fact]
    public void StreamFailure_KeepsThePipelineAliveForNewSymbols()
    {
        var marketData = new FakeMarketData();
        var vm = new OrderBookViewModel(
            marketData,
            Observable.Never<Symbol>(),
            NullLogger<OrderBookViewModel>.Instance);
        var seen = new List<OrderBook>();

        using var subscription = vm.Updates.Subscribe(seen.Add);

        marketData.GetFeed(Symbol.Parse("BTC/USDT")).Subject.OnError(new InvalidOperationException("boom"));
        seen.Should().BeEmpty("a failed stream publishes no book instead of a guessed one");

        vm.SymbolInput = "ETH/BTC";
        marketData.GetFeed(Symbol.Parse("ETH/BTC")).Subject.OnNext(Book("ETH/BTC", 1));
        seen.Should().ContainSingle("later symbols keep working after a failure");
    }

    [Fact]
    public void WorkspaceSelection_RewritesTheInputAndSwitchesTheFeed()
    {
        var marketData = new FakeMarketData();
        var workspaceSelections = new Subject<Symbol>();
        var vm = new OrderBookViewModel(
            marketData,
            workspaceSelections,
            NullLogger<OrderBookViewModel>.Instance);
        var seen = new List<OrderBook>();

        using var subscription = vm.Updates.Subscribe(seen.Add);

        workspaceSelections.OnNext(Symbol.Parse("ETH/BTC"));

        vm.SymbolInput.Should().Be("ETH/BTC", "the workspace combo owns the symbol");
        marketData.Feeds.Should().ContainKey("ETH/BTC");

        marketData.GetFeed(Symbol.Parse("ETH/BTC")).Subject.OnNext(Book("ETH/BTC", 2));
        seen.Should().ContainSingle(b => b.Symbol.Normalized == "ETH/BTC");
    }

    [Fact]
    public void WorkspaceSelectionWithoutChange_DoesNotTearTheFeed()
    {
        var marketData = new FakeMarketData();
        var workspaceSelections = new Subject<Symbol>();
        var vm = new OrderBookViewModel(
            marketData,
            workspaceSelections,
            NullLogger<OrderBookViewModel>.Instance);

        using var subscription = vm.Updates.Subscribe(_ => { });

        workspaceSelections.OnNext(Symbol.Parse("BTC/USDT"));

        marketData.Feeds["BTC/USDT"].Unsubscriptions.Should().Be(0, "identical selection must not churn the feed");
    }

    [Fact]
    public void WorkspaceSelectionFailure_KeepsTheTypedInputWorking()
    {
        var marketData = new FakeMarketData();
        var workspaceSelections = new Subject<Symbol>();
        var vm = new OrderBookViewModel(
            marketData,
            workspaceSelections,
            NullLogger<OrderBookViewModel>.Instance);

        using var subscription = vm.Updates.Subscribe(_ => { });

        workspaceSelections.OnError(new InvalidOperationException("selection source died"));

        vm.SymbolInput = "SOL/USDC";
        marketData.Feeds.Should().ContainKey("SOL/USDC", "the typed input stays the offline override after the selection source dies");
    }

    [Fact]
    public void TheChip_IsEmptyUntilTheFirstBookLands()
    {
        var marketData = new FakeMarketData();
        var vm = new OrderBookViewModel(
            marketData,
            Observable.Never<Symbol>(),
            NullLogger<OrderBookViewModel>.Instance);

        vm.Imbalance.Should().Be(0m, "no book, no read");
        vm.ImbalanceText.Should().BeEmpty("the chip publishes nothing before the first snapshot");
    }

    [Fact]
    public async Task ABookUpdate_PublishesTheRatioAndItsSignedSpelling()
    {
        var marketData = new FakeMarketData();
        var vm = new OrderBookViewModel(
            marketData,
            Observable.Never<Symbol>(),
            NullLogger<OrderBookViewModel>.Instance);
        using var subscription = vm.Updates.Subscribe(_ => { });

        // bids 4+4 = 8, asks 1+3 = 4 → (8-4)/12 = +1/3 → the two-decimal chip spelling.
        marketData.GetFeed(Symbol.Parse("BTC/USDT")).Subject.OnNext(
            LeanBook("BTC/USDT", [4m, 4m], [1m, 3m]));
        await AvaloniaDispatcherPump.DrainAsync();

        vm.Imbalance.Should().Be(1m / 3m);
        vm.ImbalanceText.Should().Be("+0.33", "the sign never swallows — the lean's direction is the read's whole point");

        marketData.GetFeed(Symbol.Parse("BTC/USDT")).Subject.OnNext(
            LeanBook("BTC/USDT", [1m, 1m], [4m, 4m]));
        await AvaloniaDispatcherPump.DrainAsync();

        vm.ImbalanceText.Should().Be("-0.60", "(2-8)/10 — the ask lean spells with its own sign");
    }

    [Fact]
    public async Task TheWindowKnob_ReweighsTheStandingBook()
    {
        var marketData = new FakeMarketData();
        var vm = new OrderBookViewModel(
            marketData,
            Observable.Never<Symbol>(),
            NullLogger<OrderBookViewModel>.Instance);
        using var subscription = vm.Updates.Subscribe(_ => { });

        // bids 4+4 = 8, asks 1+3 = 4 → the window-2 read is +0.5.
        marketData.GetFeed(Symbol.Parse("BTC/USDT")).Subject.OnNext(
            LeanBook("BTC/USDT", [4m, 4m], [1m, 3m]));
        await AvaloniaDispatcherPump.DrainAsync();

        vm.ImbalanceWindow = 2;
        vm.Imbalance.Should().Be(1m / 3m, "the knob change re-weighs the standing book live: (8-4)/12");
        vm.ImbalanceText.Should().Be("+0.33");

        // The window-1 read weighs only the first level: 4 vs 1 → +0.6.
        vm.ImbalanceWindow = 1;
        vm.Imbalance.Should().Be(0.6m);
        vm.ImbalanceText.Should().Be("+0.60");
    }

    [Fact]
    public async Task TheSmoothingKnob_BlendsTheReads_AndReenablingCarriesTheHonestLastRead()
    {
        var marketData = new FakeMarketData();
        var vm = new OrderBookViewModel(
            marketData,
            Observable.Never<Symbol>(),
            NullLogger<OrderBookViewModel>.Instance);
        using var subscription = vm.Updates.Subscribe(_ => { });

        // The off smoothing passes every read raw (bids 7 asks 3 → +0.4).
        marketData.GetFeed(Symbol.Parse("BTC/USDT")).Subject.OnNext(
            LeanBook("BTC/USDT", [7m], [3m]));
        await AvaloniaDispatcherPump.DrainAsync();
        vm.Imbalance.Should().Be(0.4m);

        // Enabling re-weighs the standing book: the first read passes raw
        // (nothing to blend from) and the carried state starts there.
        vm.ImbalanceSmoothed = true;
        vm.Imbalance.Should().Be(0.4m, "the EMA's first read is the raw one");
        vm.ImbalanceText.Should().Be("+0.40");

        // The next read blends with the fixed alpha: 0.4 + 0.25 * (0.8 - 0.4).
        marketData.GetFeed(Symbol.Parse("BTC/USDT")).Subject.OnNext(
            LeanBook("BTC/USDT", [9m], [1m]));
        await AvaloniaDispatcherPump.DrainAsync();
        vm.Imbalance.Should().Be(0.5m, "0.4 + 0.25 * (0.8 - 0.4) = 0.5");
        vm.ImbalanceText.Should().Be("+0.50");
    }

    [Fact]
    public async Task ASymbolSwitch_BlanksTheChip_AndTheNewSymbolReadsRaw()
    {
        var marketData = new FakeMarketData();
        var vm = new OrderBookViewModel(
            marketData,
            Observable.Never<Symbol>(),
            NullLogger<OrderBookViewModel>.Instance);
        using var subscription = vm.Updates.Subscribe(_ => { });
        vm.ImbalanceSmoothed = true;

        marketData.GetFeed(Symbol.Parse("BTC/USDT")).Subject.OnNext(
            LeanBook("BTC/USDT", [7m], [3m]));
        await AvaloniaDispatcherPump.DrainAsync();
        vm.ImbalanceText.Should().Be("+0.40", "the first read passes raw (the enabled EMA starts there)");

        vm.SymbolInput = "ETH/BTC";
        vm.ImbalanceText.Should().BeEmpty("the carried read belongs to the old symbol — the chip blanks on the switch");

        // The new symbol's first book passes through raw: +0.8, never a
        // blend of the old symbol's carried 0.4 (which would read 0.575).
        marketData.GetFeed(Symbol.Parse("ETH/BTC")).Subject.OnNext(
            LeanBook("ETH/BTC", [9m], [1m]));
        await AvaloniaDispatcherPump.DrainAsync();
        vm.Imbalance.Should().Be(0.8m, "the reset dropped the carried read — the new symbol's first book reads raw");
        vm.ImbalanceText.Should().Be("+0.80");
    }

    #endregion
}
