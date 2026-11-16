namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
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
/// Trade tape ViewModel behavior on a fake market data adapter: the default
/// symbol streams from the first frame, Rx Switch cancels the previous symbol
/// on change, an unparsable input keeps the current stream alive instead of
/// guessing, and a stream failure ends one symbol's data without killing the
/// pipeline for later symbols. The whale filter (T-245) rides the same rig:
/// the persisted state loads at initialization, a positive threshold passes
/// only the prints strictly above it, a user edit applies live and rides the
/// debounced persist (the first edit on a default-riding symbol writes the
/// absolute default too, an edit on an overridden symbol writes only the map
/// entry), an unparsable or pre-bootstrap edit stays session-local, and the
/// store's own changes reload the resolved state.
/// </summary>
public sealed class TradeTapeViewModelTests
{
    #region Fake Adapter

    /// <summary>
    /// Market data fake with per-symbol live trade feeds and subscription
    /// counters (mimics <see cref="ExchangeAdapterBase"/>-shaped subjects so
    /// the switch semantics are observable from the tests).
    /// </summary>
    private sealed class FakeMarketData : IMarketDataAdapter
    {
        private readonly Dictionary<string, TradeFeed> _feeds = new();

        public string Id => "fake";

        public string Name => "Fake";

        public bool IsConnected => true;

        public ExchangeCapabilities Capabilities => ExchangeCapabilities.MarketData;

        public Dictionary<string, TradeFeed> Feeds => _feeds;

        public IObservable<MarketTrade> SubscribeTrades(Symbol symbol)
        {
            var feed = GetFeed(symbol);

            return Observable.Create<MarketTrade>(observer =>
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

        public TradeFeed GetFeed(Symbol symbol)
        {
            var key = symbol.Normalized;
            if (!_feeds.TryGetValue(key, out var feed))
            {
                feed = new TradeFeed();
                _feeds[key] = feed;
            }

            return feed;
        }

        #region Unused Market Data Surface

        public Task<IReadOnlyList<Symbol>> GetSymbolsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Symbol>>([]);

        public IObservable<Ticker> SubscribeTicker(Symbol symbol) => Observable.Never<Ticker>();

        public IObservable<OrderBook> SubscribeOrderBook(Symbol symbol, int depth = 20) => Observable.Never<OrderBook>();

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

    private sealed class TradeFeed
    {
        public Subject<MarketTrade> Subject { get; } = new();

        public int Subscriptions;

        public int Unsubscriptions;
    }

    /// <summary>Synthetic trade on the feed's symbol with an ascending price.</summary>
    private static MarketTrade Trade(Symbol symbol, int index) =>
        new()
        {
            Id = $"t{index}",
            Symbol = symbol,
            Side = index % 2 == 0 ? OrderSide.Buy : OrderSide.Sell,
            Price = new Price(1000.5m - index),
            Quantity = new Quantity(index + 1),
            Timestamp = DateTimeOffset.UtcNow,
        };

    /// <summary>A trade with explicit served facts, so the notional tests name their own arithmetic.</summary>
    private static MarketTrade Trade(Symbol symbol, decimal price, decimal quantity) =>
        new()
        {
            Id = $"t{price}-{quantity}",
            Symbol = symbol,
            Side = OrderSide.Buy,
            Price = new Price(price),
            Quantity = new Quantity(quantity),
            Timestamp = DateTimeOffset.UtcNow,
        };

    #endregion

    #region Fake Settings

    /// <summary>
    /// In-memory settings fake (the NotificationCenter tests' shape): the
    /// store keys carry typed values directly, every write echoes the
    /// change stream — the T-053 canon the VM reloads on — and the
    /// persistence tests read the raw store after the debounced window.
    /// </summary>
    private sealed class FakeSettings : ISettingsService
    {
        private readonly Dictionary<string, object?> _store = new(StringComparer.Ordinal);
        private readonly Subject<SettingsChange> _changes = new();

        public IObservable<SettingsChange> Changes => _changes;

        public Task<T> GetAsync<T>(string section, string key, T defaultValue = default!)
        {
            if (_store.TryGetValue($"{section}\u0001{key}", out var value) && value is T typed)
            {
                return Task.FromResult(typed);
            }

            return Task.FromResult(defaultValue);
        }

        public Task SetAsync<T>(string section, string key, T value)
        {
            var storageKey = $"{section}\u0001{key}";
            var old = _store.TryGetValue(storageKey, out var previous) ? previous : null;
            _store[storageKey] = value;
            _changes.OnNext(new SettingsChange(section, key, old, value));
            return Task.CompletedTask;
        }

        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;

        public T Peek<T>(string key, T fallback)
        {
            if (_store.TryGetValue($"{ZettaTrade.App.TapeSettingsKeys.Section}\u0001{key}", out var value) &&
                value is T typed)
            {
                return typed;
            }

            return fallback;
        }

        public bool HasKey(string key) =>
            _store.ContainsKey($"{ZettaTrade.App.TapeSettingsKeys.Section}\u0001{key}");
    }

    /// <summary>A settled pause longer than the persist debounce (the writes land inside it).</summary>
    private static readonly TimeSpan DebounceWait = TimeSpan.FromMilliseconds(
        ZettaTrade.App.TapeSettingsKeys.WhalePersistDebounceMs + 600);

    #endregion

    #region Tests

    [Fact]
    public void Constructor_DoesNotSubscribeUntilTheStreamIsConsumed()
    {
        var marketData = new FakeMarketData();

        var vm = new TradeTapeViewModel(
            marketData,
            Observable.Never<Symbol>(),
            new FakeSettings(),
            NullLogger<TradeTapeViewModel>.Instance);

        vm.SymbolInput.Should().Be(TradeTapeViewModel.DefaultSymbolInput);
        marketData.Feeds.Should().BeEmpty(
            "the tape's stream ships raw (the widget consumes it on attach), so nothing is subscribed until then");

        using var consumed = vm.Updates.Subscribe(_ => { });

        marketData.Feeds.Keys.Should().ContainSingle().Which.Should().Be("BTC/USDT",
            "consumption subscribes the default symbol from the first frame of the panel");
    }

    [Fact]
    public void Publish_ReachesTheUpdatesStream()
    {
        var marketData = new FakeMarketData();
        var vm = new TradeTapeViewModel(
            marketData,
            Observable.Never<Symbol>(),
            new FakeSettings(),
            NullLogger<TradeTapeViewModel>.Instance);
        var seen = new List<MarketTrade>();

        using var subscription = vm.Updates.Subscribe(seen.Add);
        marketData.GetFeed(Symbol.Parse("BTC/USDT")).Subject.OnNext(Trade(Symbol.Parse("BTC/USDT"), 1));

        seen.Should().ContainSingle(t => t.Symbol.Normalized == "BTC/USDT");
    }

    [Fact]
    public void SymbolSwitch_CancelsTheOldStream()
    {
        var marketData = new FakeMarketData();
        var vm = new TradeTapeViewModel(
            marketData,
            Observable.Never<Symbol>(),
            new FakeSettings(),
            NullLogger<TradeTapeViewModel>.Instance);
        var seen = new List<MarketTrade>();

        using var subscription = vm.Updates.Subscribe(seen.Add);
        marketData.GetFeed(Symbol.Parse("BTC/USDT")).Subject.OnNext(Trade(Symbol.Parse("BTC/USDT"), 1));
        var before = seen.Count;

        vm.SymbolInput = "ETH/BTC";

        marketData.Feeds["BTC/USDT"].Unsubscriptions.Should().BeGreaterThan(0);
        marketData.Feeds.Should().ContainKey("ETH/BTC");

        marketData.GetFeed(Symbol.Parse("BTC/USDT")).Subject.OnNext(Trade(Symbol.Parse("BTC/USDT"), 2));
        seen.Count.Should().Be(before, "publishes to the old symbol must not arrive after the switch");

        marketData.GetFeed(Symbol.Parse("ETH/BTC")).Subject.OnNext(Trade(Symbol.Parse("ETH/BTC"), 3));
        seen.Count.Should().Be(before + 1);
    }

    [Fact]
    public void UnparsableInput_KeepsThePreviousStreamAlive()
    {
        var marketData = new FakeMarketData();
        var vm = new TradeTapeViewModel(
            marketData,
            Observable.Never<Symbol>(),
            new FakeSettings(),
            NullLogger<TradeTapeViewModel>.Instance);
        var seen = new List<MarketTrade>();

        using var subscription = vm.Updates.Subscribe(seen.Add);

        vm.SymbolInput = "Not A Symbol";

        marketData.Feeds.Keys.Should().ContainSingle("no stream is subscribed for unexpected input");

        marketData.GetFeed(Symbol.Parse("BTC/USDT")).Subject.OnNext(Trade(Symbol.Parse("BTC/USDT"), 1));
        seen.Should().ContainSingle("the current tape keeps streaming");
    }

    [Fact]
    public void StreamFailure_KeepsThePipelineAliveForNewSymbols()
    {
        var marketData = new FakeMarketData();
        var vm = new TradeTapeViewModel(
            marketData,
            Observable.Never<Symbol>(),
            new FakeSettings(),
            NullLogger<TradeTapeViewModel>.Instance);
        var seen = new List<MarketTrade>();

        using var subscription = vm.Updates.Subscribe(seen.Add);

        marketData.GetFeed(Symbol.Parse("BTC/USDT")).Subject.OnError(new InvalidOperationException("boom"));
        seen.Should().BeEmpty("a failed stream publishes no trade instead of a guessed one");

        vm.SymbolInput = "ETH/BTC";
        marketData.GetFeed(Symbol.Parse("ETH/BTC")).Subject.OnNext(Trade(Symbol.Parse("ETH/BTC"), 1));
        seen.Should().ContainSingle("later symbols keep working after a failure");
    }

    [Fact]
    public void WorkspaceSelection_RewritesTheInputAndSwitchesTheFeed()
    {
        var marketData = new FakeMarketData();
        var workspaceSelections = new Subject<Symbol>();
        var vm = new TradeTapeViewModel(
            marketData,
            workspaceSelections,
            new FakeSettings(),
            NullLogger<TradeTapeViewModel>.Instance);
        var seen = new List<MarketTrade>();

        using var subscription = vm.Updates.Subscribe(seen.Add);

        workspaceSelections.OnNext(Symbol.Parse("ETH/BTC"));

        vm.SymbolInput.Should().Be("ETH/BTC", "the workspace combo owns the symbol");
        marketData.Feeds.Should().ContainKey("ETH/BTC");

        marketData.GetFeed(Symbol.Parse("ETH/BTC")).Subject.OnNext(Trade(Symbol.Parse("ETH/BTC"), 1));
        seen.Should().ContainSingle(t => t.Symbol.Normalized == "ETH/BTC");
    }

    [Fact]
    public void WorkspaceSelectionWithoutChange_DoesNotTearTheFeed()
    {
        var marketData = new FakeMarketData();
        var workspaceSelections = new Subject<Symbol>();
        var vm = new TradeTapeViewModel(
            marketData,
            workspaceSelections,
            new FakeSettings(),
            NullLogger<TradeTapeViewModel>.Instance);

        using var subscription = vm.Updates.Subscribe(_ => { });

        workspaceSelections.OnNext(Symbol.Parse("BTC/USDT"));

        marketData.Feeds["BTC/USDT"].Unsubscriptions.Should().Be(0, "identical selection must not churn the feed");
    }

    [Fact]
    public void WorkspaceSelectionFailure_KeepsTheTypedInputWorking()
    {
        var marketData = new FakeMarketData();
        var workspaceSelections = new Subject<Symbol>();
        var vm = new TradeTapeViewModel(
            marketData,
            workspaceSelections,
            new FakeSettings(),
            NullLogger<TradeTapeViewModel>.Instance);

        using var subscription = vm.Updates.Subscribe(_ => { });

        workspaceSelections.OnError(new InvalidOperationException("selection source died"));

        vm.SymbolInput = "SOL/USDC";
        marketData.Feeds.Should().ContainKey("SOL/USDC", "the typed input stays the offline override after the selection source dies");
    }

    #endregion

    #region Whale Filter (T-245)

    [Fact]
    public async Task WhaleGate_WithAPersistedDefault_PassesOnlyThePrintsStrictlyAboveIt()
    {
        var marketData = new FakeMarketData();
        var settings = new FakeSettings();
        await settings.SetAsync(ZettaTrade.App.TapeSettingsKeys.Section, ZettaTrade.App.TapeSettingsKeys.WhaleThreshold, 500m);
        var vm = new TradeTapeViewModel(
            marketData,
            Observable.Never<Symbol>(),
            settings,
            NullLogger<TradeTapeViewModel>.Instance);
        await vm.InitializeAsync();
        var seen = new List<MarketTrade>();

        using var subscription = vm.Updates.Subscribe(seen.Add);
        var btc = Symbol.Parse("BTC/USDT");

        // Exactly at the threshold: dropped (strictly above is the edge).
        marketData.GetFeed(btc).Subject.OnNext(Trade(btc, 50m, 10m));
        // Below: dropped.
        marketData.GetFeed(btc).Subject.OnNext(Trade(btc, 50m, 9.99m));
        // Above: passes.
        marketData.GetFeed(btc).Subject.OnNext(Trade(btc, 50m, 10.01m));

        seen.Should().ContainSingle("the gate keeps only the notional strictly above the threshold")
            .Which.Quantity!.Value.Should().Be(10.01m);
        vm.WhaleThreshold.Should().Be(500m);
        vm.WhaleThresholdText.Should().Be("500");
    }

    [Fact]
    public async Task WhaleGate_ThresholdZero_PassesEverything()
    {
        var marketData = new FakeMarketData();
        var vm = new TradeTapeViewModel(
            marketData,
            Observable.Never<Symbol>(),
            new FakeSettings(),
            NullLogger<TradeTapeViewModel>.Instance);
        await vm.InitializeAsync();
        var seen = new List<MarketTrade>();

        using var subscription = vm.Updates.Subscribe(seen.Add);
        var btc = Symbol.Parse("BTC/USDT");
        marketData.GetFeed(btc).Subject.OnNext(Trade(btc, 1m, 0.01m));

        seen.Should().ContainSingle("an off filter (zero) passes every print");
        vm.WhaleThreshold.Should().Be(0m);
    }

    [Fact]
    public async Task WhaleThresholdEdit_AppliesLive()
    {
        var marketData = new FakeMarketData();
        var vm = new TradeTapeViewModel(
            marketData,
            Observable.Never<Symbol>(),
            new FakeSettings(),
            NullLogger<TradeTapeViewModel>.Instance);
        var seen = new List<MarketTrade>();

        using var subscription = vm.Updates.Subscribe(seen.Add);
        var btc = Symbol.Parse("BTC/USDT");

        vm.WhaleThresholdText = "1000";
        vm.WhaleThreshold.Should().Be(1000m, "the edit applies to the live filter immediately");

        marketData.GetFeed(btc).Subject.OnNext(Trade(btc, 100m, 9.99m));
        marketData.GetFeed(btc).Subject.OnNext(Trade(btc, 100m, 10.01m));

        seen.Should().ContainSingle("a settled edit gates the stream from the next print on")
            .Which.Quantity!.Value.Should().Be(10.01m);
    }

    [Fact]
    public async Task WhaleThresholdEdit_AnUnparsableOrNegativeSpelling_EditsNothing()
    {
        var marketData = new FakeMarketData();
        var settings = new FakeSettings();
        var vm = new TradeTapeViewModel(
            marketData,
            Observable.Never<Symbol>(),
            settings,
            NullLogger<TradeTapeViewModel>.Instance);

        vm.WhaleThresholdText = "abc";
        vm.WhaleThreshold.Should().Be(0m, "unparsable input edits nothing, never coerces to zero");
        vm.WhaleThresholdText = "-5";
        vm.WhaleThreshold.Should().Be(0m, "a negative names no threshold, never coerces to zero");

        await Task.Delay(DebounceWait);
        settings.HasKey(ZettaTrade.App.TapeSettingsKeys.WhaleThreshold)
            .Should().BeFalse("nothing persists behind a refused spelling");
        settings.HasKey(ZettaTrade.App.TapeSettingsKeys.WhaleThresholds)
            .Should().BeFalse("the override map stays empty behind a refused spelling too");
    }

    [Fact]
    public async Task WhaleThresholdEdit_PersistsDebounced_AndTheFirstEditWritesTheDefaultToo()
    {
        var marketData = new FakeMarketData();
        var settings = new FakeSettings();
        var vm = new TradeTapeViewModel(
            marketData,
            Observable.Never<Symbol>(),
            settings,
            NullLogger<TradeTapeViewModel>.Instance);
        await vm.InitializeAsync();

        vm.WhaleThresholdText = "750";
        vm.WhaleThreshold.Should().Be(750m, "the live apply never waits for the store");

        // The debounced write (600 ms) lands; the settled snapshot carries
        // both shapes: the absolute default AND the shown symbol's entry
        // (the first edit on a symbol that rode the default writes both).
        await Task.Delay(DebounceWait);

        settings.Peek(ZettaTrade.App.TapeSettingsKeys.WhaleThreshold, 0m).Should().Be(750m);
        var overrides = settings.Peek(
            ZettaTrade.App.TapeSettingsKeys.WhaleThresholds,
            new Dictionary<string, decimal>());
        overrides.Should().ContainKey("BTC/USDT").WhoseValue.Should().Be(750m);
    }

    [Fact]
    public async Task WhaleThresholdEdit_OnAnOverriddenSymbol_WritesOnlyTheMapEntry()
    {
        var marketData = new FakeMarketData();
        var settings = new FakeSettings();
        await settings.SetAsync(
            ZettaTrade.App.TapeSettingsKeys.Section,
            ZettaTrade.App.TapeSettingsKeys.WhaleThresholds,
            new Dictionary<string, decimal> { ["ETH/BTC"] = 42m });
        var vm = new TradeTapeViewModel(
            marketData,
            Observable.Never<Symbol>(),
            settings,
            NullLogger<TradeTapeViewModel>.Instance);
        await vm.InitializeAsync();

        vm.SymbolInput = "ETH/BTC";
        vm.WhaleThreshold.Should().Be(42m, "the symbol's own override resolves first");
        vm.WhaleThresholdText.Should().Be("42");

        vm.WhaleThresholdText = "60";
        await Task.Delay(DebounceWait);

        // The persist lands the whole state (the debounced snapshot's own
        // shape), so the absolute default key materializes with ITS OWN
        // value: the override never leaks into it.
        settings.Peek(ZettaTrade.App.TapeSettingsKeys.WhaleThreshold, 0m).Should().Be(0m,
            "an edit on a symbol that already rode its own override never rewrites the absolute default");
        settings.Peek(
            ZettaTrade.App.TapeSettingsKeys.WhaleThresholds,
            new Dictionary<string, decimal>()).Should()
            .ContainKey("ETH/BTC").WhoseValue.Should().Be(60m);
    }

    [Fact]
    public async Task TheShownSymbol_ChangeResolvesTheOverrideOrTheDefault()
    {
        var marketData = new FakeMarketData();
        var settings = new FakeSettings();
        await settings.SetAsync(ZettaTrade.App.TapeSettingsKeys.Section, ZettaTrade.App.TapeSettingsKeys.WhaleThreshold, 500m);
        await settings.SetAsync(
            ZettaTrade.App.TapeSettingsKeys.Section,
            ZettaTrade.App.TapeSettingsKeys.WhaleThresholds,
            new Dictionary<string, decimal> { ["ETH/BTC"] = 42m });
        var vm = new TradeTapeViewModel(
            marketData,
            Observable.Never<Symbol>(),
            settings,
            NullLogger<TradeTapeViewModel>.Instance);
        await vm.InitializeAsync();

        vm.WhaleThreshold.Should().Be(500m, "the shown symbol without an override rides the absolute default");
        vm.WhaleThresholdText.Should().Be("500");

        vm.SymbolInput = "ETH/BTC";
        vm.WhaleThreshold.Should().Be(42m, "the override wins on its own symbol");
        vm.WhaleThresholdText.Should().Be("42");

        vm.SymbolInput = "BTC/USDT";
        vm.WhaleThreshold.Should().Be(500m);
        vm.WhaleThresholdText.Should().Be("500");
    }

    [Fact]
    public async Task AnExternalSettingsChange_ReloadsTheResolvedState()
    {
        var marketData = new FakeMarketData();
        var settings = new FakeSettings();
        var vm = new TradeTapeViewModel(
            marketData,
            Observable.Never<Symbol>(),
            settings,
            NullLogger<TradeTapeViewModel>.Instance);
        await vm.InitializeAsync();
        vm.WhaleThreshold.Should().Be(0m, "an honest empty store is the off state");

        // An external edit (an import, another surface, a factory reset):
        // the store's change stream reloads the resolved state. The reload
        // lane rides ObserveOnUiThread, so the facts pump the dispatcher.
        await settings.SetAsync(ZettaTrade.App.TapeSettingsKeys.Section, ZettaTrade.App.TapeSettingsKeys.WhaleThreshold, 300m);
        await AvaloniaDispatcherPump.DrainAsync();

        vm.WhaleThreshold.Should().Be(300m, "the change stream reloads the persisted state (the T-053 canon)");
        vm.WhaleThresholdText.Should().Be("300");

        await settings.SetAsync(
            ZettaTrade.App.TapeSettingsKeys.Section,
            ZettaTrade.App.TapeSettingsKeys.WhaleThresholds,
            new Dictionary<string, decimal> { ["ETH/BTC"] = 9m });
        await AvaloniaDispatcherPump.DrainAsync();
        vm.SymbolInput = "ETH/BTC";
        vm.WhaleThreshold.Should().Be(9m, "the reloaded map reaches the shown symbol's resolution too");
    }

    [Fact]
    public async Task AnEditBeforeThePersistedStateLands_StaysSessionLocal()
    {
        var marketData = new FakeMarketData();
        var settings = new FakeSettings();
        var vm = new TradeTapeViewModel(
            marketData,
            Observable.Never<Symbol>(),
            settings,
            NullLogger<TradeTapeViewModel>.Instance);

        vm.WhaleThresholdText = "123";
        vm.WhaleThreshold.Should().Be(123m, "the live filter carries the edit even before the load");

        await Task.Delay(DebounceWait);
        settings.HasKey(ZettaTrade.App.TapeSettingsKeys.WhaleThreshold)
            .Should().BeFalse("the not-yet-loaded map must never persist over the stored overrides it has not seen");

        await vm.InitializeAsync();
        vm.WhaleThreshold.Should().Be(0m, "the load lands the store's own (empty) state over the session-local edit");
        vm.WhaleThresholdText.Should().Be("0");
    }

    #endregion
}
