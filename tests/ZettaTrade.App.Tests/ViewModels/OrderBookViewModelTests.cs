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
/// pipeline for later symbols.
/// </summary>
public sealed class OrderBookViewModelTests
{
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

    #endregion
}
