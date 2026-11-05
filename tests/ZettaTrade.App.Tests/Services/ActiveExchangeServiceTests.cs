namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// Tests for the active-exchange facade: the behavioral change stream, the
/// persisted-choice restore (available, missing and unregistered), the
/// user-facing switch (persist-then-activate, no-op on repeat, exceptions on
/// unknown ids), the atomic stream switching over adapter changes and the
/// book-depth resolution against the active adapter's served depths.
/// </summary>
public sealed class ActiveExchangeServiceTests
{
    private readonly IMarketDataAdapter _binance = Substitute.For<IMarketDataAdapter>();
    private readonly IMarketDataAdapter _demo = Substitute.For<IMarketDataAdapter>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();

    public ActiveExchangeServiceTests()
    {
        _binance.Id.Returns("binance");
        _binance.Name.Returns("Binance");
        _demo.Id.Returns("demo");
        _demo.Name.Returns("Demo");
    }

    private ActiveExchangeService CreateService() => new(
        [_binance, _demo],
        _settings,
        NullLogger<ActiveExchangeService>.Instance);

    private void NoPersistedChoice() =>
        _settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);

    private void PersistedChoiceIs(string exchangeId) =>
        _settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(exchangeId);

    private static IReadOnlyList<Symbol> SymbolList(params string[] names) =>
        names.Select(Symbol.Parse).ToList();

    [Fact]
    public void AdapterChanges_EmitsTheFirstAdapterOnSubscribe()
    {
        var service = CreateService();
        var seen = new List<IMarketDataAdapter>();
        service.AdapterChanges.Subscribe(seen.Add);

        seen.Should().Contain(_binance, "the behavioral stream bootstraps subscribers");
    }

    [Fact]
    public async Task InitializeAsync_WithoutPersistedChoice_StartsOnTheDefault()
    {
        NoPersistedChoice();
        var service = CreateService();
        using var fallbacks = service.UnavailablePersistedChoices.Subscribe(_ => { });

        await service.InitializeAsync();

        service.Current.Should().Be(_binance);
        await _settings.DidNotReceive().SetAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task InitializeAsync_RestoresThePersistedChoice_WithoutRewritingSettings()
    {
        PersistedChoiceIs("demo");
        var service = CreateService();

        await service.InitializeAsync();

        service.Current.Should().Be(_demo);
        // A restore is not a user action: the settings file must not be rewritten.
        await _settings.DidNotReceive().SetAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task InitializeAsync_UnregisteredPersistedChoice_FallsBackLoudly()
    {
        PersistedChoiceIs("gamma");
        var service = CreateService();
        var fallbacks = new List<string>();
        using var subscription = service.UnavailablePersistedChoices.Subscribe(fallbacks.Add);

        await service.InitializeAsync();

        service.Current.Should().Be(_binance, "the unregistered choice never replaces the default");
        fallbacks.Should().Contain(
            "gamma",
            "the fallback must be loud, not silent");
    }

    [Fact]
    public async Task UnavailablePersistedChoices_LateSubscriber_ReceivesTheLastNotice()
    {
        PersistedChoiceIs("gamma");
        var service = CreateService();

        // The restore emits during the startup walk, when nobody listens
        // (the status bar constructs afterwards) — the notice replays to it.
        await service.InitializeAsync();

        var notices = new List<string>();
        using var subscription = service.UnavailablePersistedChoices.Subscribe(notices.Add);

        notices.Should().Contain(
            "gamma",
            "the loud fallback must not be lost between the facade and its listener");
    }

    [Fact]
    public async Task SwitchToAsync_PersistsFirstAndThenActivates()
    {
        NoPersistedChoice();
        var service = CreateService();
        var seen = new List<IMarketDataAdapter>();
        using var changes = service.AdapterChanges.Subscribe(seen.Add);

        await service.SwitchToAsync("demo");

        await _settings.Received(1).SetAsync("Workspace", "ExchangeId", "demo");
        seen.Should().ContainInOrder(
            new[] { _binance, _demo },
            "the switch flips every consumer atomically");
        service.Current.Should().Be(_demo);
    }

    [Fact]
    public async Task SwitchToAsync_SameId_IsANoOp()
    {
        NoPersistedChoice();
        var service = CreateService();
        var seen = new List<IMarketDataAdapter>();
        using var changes = service.AdapterChanges.Subscribe(seen.Add);

        await service.SwitchToAsync("binance");

        await _settings.DidNotReceive().SetAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        seen.Should().ContainSingle("no duplicate emission for the already-active exchange");
    }

    [Fact]
    public async Task SwitchToAsync_UnknownId_ThrowsAndLeavesStateIntact()
    {
        NoPersistedChoice();
        var service = CreateService();
        var seen = new List<IMarketDataAdapter>();
        using var changes = service.AdapterChanges.Subscribe(seen.Add);

        var act = async () => await service.SwitchToAsync("gamma");

        await act.Should().ThrowAsync<ArgumentException>(
            "unknown exchange ids are an error, not a default");
        service.Current.Should().Be(_binance);
        seen.Should().ContainSingle();
    }

    [Fact]
    public async Task Facade_MembersAndDataCalls_MirrorTheCurrentAdapter()
    {
        NoPersistedChoice();
        _binance.GetSymbolsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(SymbolList("BTC/USDT")));
        _demo.GetSymbolsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(SymbolList("BTC/USDT", "ETH/USDT")));

        var service = CreateService();
        service.Name.Should().Be("Binance");

        await service.SwitchToAsync("demo");

        service.Id.Should().Be("demo");
        service.Name.Should().Be("Demo");
        // A switched facade never drains the previous adapter.
        await _binance.DidNotReceive().GetSymbolsAsync(Arg.Any<CancellationToken>());
        (await service.GetSymbolsAsync()).Should().HaveCount(2);
        await _demo.Received(1).GetSymbolsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Facade_SubscribeOrderBook_ForwardsToTheCurrentAdapterWithDepth()
    {
        var btcUsdt = Symbol.Parse("BTC/USDT");
        var book = new OrderBook
        {
            Symbol = btcUsdt,
            Bids = [new PriceLevel(67000m, 1m)],
            Asks = [new PriceLevel(67001m, 1m)],
            Timestamp = DateTimeOffset.UtcNow,
        };
        _demo.SubscribeOrderBook(btcUsdt, 25)
            .Returns(Observable.Return(book));
        NoPersistedChoice();

        var service = CreateService();
        await service.SwitchToAsync("demo");

        var received = await FirstWithin(
            service.SubscribeOrderBook(btcUsdt, 25),
            TimeSpan.FromSeconds(2));

        var receipt = _demo.Received(1).SubscribeOrderBook(btcUsdt, 25);
        receipt.Should().NotBeNull();
        received.Should().Be(book);
        service.IsConnected.Should().Be(_demo.IsConnected);
    }

    [Fact]
    public async Task Facade_SubscribeOrderBook_DefaultDepthRequest_ResolvesToTheServedDepth()
    {
        // The wire-adapter depth shape (Bybit serves 1 and 50 only): the
        // generic display default of 20 used to hit the adapter's explicit
        // rejection and leave the book widget empty on a stream error.
        var book = CannedBook(BtcUsdt);
        var stub = new StubDepthedAdapter(new HashSet<int> { 1, 50 }, book);
        NoPersistedChoice();
        var service = new ActiveExchangeService(
            [stub],
            _settings,
            NullLogger<ActiveExchangeService>.Instance);

        var received = await FirstWithin(
            service.SubscribeOrderBook(BtcUsdt),
            TimeSpan.FromSeconds(2));

        received.Should().BeSameAs(book, "the resolved request is served, not rejected");
        stub.Requests.Should().ContainSingle();
        stub.Requests[0].Depth.Should().Be(
            50,
            "the generic default of 20 resolves to the smallest served deep-enough depth");
    }

    [Fact]
    public async Task Facade_SubscribeOrderBook_DeclaredServedDepth_RidesUnchanged()
    {
        // A request inside the served set (Binance's stepped shape has 25)
        // forwards untouched: resolution only escapes the impossible ones.
        var book = CannedBook(BtcUsdt);
        var stub = new StubDepthedAdapter(new HashSet<int> { 5, 10, 20, 25 }, book);
        NoPersistedChoice();
        var service = new ActiveExchangeService(
            [stub],
            _settings,
            NullLogger<ActiveExchangeService>.Instance);

        await FirstWithin(
            service.SubscribeOrderBook(BtcUsdt, 25),
            TimeSpan.FromSeconds(2));

        stub.Requests.Should().ContainSingle();
        stub.Requests[0].Depth.Should().Be(
            25,
            "a declared served depth must ride unchanged, not snapped");
    }

    [Fact]
    public async Task StreamFollowsSwitch_TheSubscriptionFlipsToTheNewAdapter()
    {
        var btcUsdt = Symbol.Parse("BTC/USDT");
        var binanceTicker = new Ticker(
            btcUsdt, 1m, 0.99m, 1.01m, 0m, 0m, DateTimeOffset.UtcNow);
        var demoTicker = new Ticker(
            btcUsdt, 2m, 1.99m, 2.01m, 0m, 0m, DateTimeOffset.UtcNow);
        var subject = new Subject<Ticker>();
        _binance.SubscribeTicker(btcUsdt).Returns(subject);
        _demo.SubscribeTicker(btcUsdt).Returns(Observable.Return(demoTicker));
        NoPersistedChoice();

        var service = CreateService();
        var received = new List<Ticker>();
        using var feed = service.SubscribeTicker(btcUsdt).Subscribe(received.Add);
        subject.OnNext(binanceTicker);

        await service.SwitchToAsync("demo");

        received.Should().HaveCount(2);
        received[0].LastPrice.Should().Be(1m);
        received[1].LastPrice.Should().Be(2m, "the switch delivers the new exchange's stream in-flight, atomically");
    }

    /// <summary>
    /// Receive the first emission within the bounded wait: stream snapshots
    /// are asserted to arrive promptly instead of pinning an exact cadence.
    /// </summary>
    private static async Task<TSnapshot> FirstWithin<TSnapshot>(IObservable<TSnapshot> source, TimeSpan wait)
    {
        var received = new TaskCompletionSource<TSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = source.Subscribe(received.SetResult);

        var winner = await Task.WhenAny(received.Task, Task.Delay(wait));
        winner.Should().Be(received.Task, $"the first snapshot must arrive within {wait}");
        return await received.Task;
    }

    /// <summary>One stable BTC/USDT value object for the stub tests.</summary>
    private static Symbol BtcUsdt { get; } = Symbol.Parse("BTC/USDT");

    private static OrderBook CannedBook(Symbol symbol) =>
        new()
        {
            Symbol = symbol,
            Bids = [new PriceLevel(67000m, 1m)],
            Asks = [new PriceLevel(67001m, 1m)],
            Timestamp = DateTimeOffset.UtcNow,
        };

    #region Test Stub

    /// <summary>One recorded book subscription request.</summary>
    private sealed record StubBookRequest(Symbol Symbol, int Depth);

    /// <summary>
    /// A depth-locked adapter derived from the exchange base the way the
    /// real wire adapters are, so the facade's depth resolution is exercised
    /// through the base-class declaration path: a canned immediate snapshot
    /// for the subscriber and recorded requests for the receipts.
    /// </summary>
    private sealed class StubDepthedAdapter : ExchangeAdapterBase
    {
        private const string StubId = "stubbook";

        private const string StubName = "StubBook";

        private readonly List<StubBookRequest> _requests = [];
        private readonly IReadOnlySet<int> _servedBookDepths;
        private readonly OrderBook _book;

        public StubDepthedAdapter(IReadOnlySet<int> servedBookDepths, OrderBook book)
            : base(NullLogger.Instance)
        {
            _servedBookDepths = servedBookDepths;
            _book = book;
        }

        public IReadOnlyList<StubBookRequest> Requests => _requests;

        public override string Id => StubId;

        public override string Name => StubName;

        public override ExchangeCapabilities Capabilities => ExchangeCapabilities.MarketData;

        public override Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public override Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public override Task<IReadOnlyList<Symbol>> GetSymbolsAsync(CancellationToken ct = default) => EmptySymbolsAsync();

        /// <summary>An empty catalog, without a sync-result wrapper.</summary>
        private static async Task<IReadOnlyList<Symbol>> EmptySymbolsAsync() => [];

        public override async Task<IReadOnlyList<Bar>> GetBarsAsync(
            Symbol symbol,
            TimeFrame timeFrame,
            DateTimeOffset from,
            DateTimeOffset? to = null,
            int limit = 500,
            CancellationToken ct = default) => [];

        protected override IReadOnlySet<int> SupportedBookDepths => _servedBookDepths;

        public override IObservable<OrderBook> SubscribeOrderBook(Symbol symbol, int depth = 20)
        {
            _requests.Add(new StubBookRequest(symbol, depth));
            return Observable.Return(_book);
        }
    }

    #endregion Test Stub
}
