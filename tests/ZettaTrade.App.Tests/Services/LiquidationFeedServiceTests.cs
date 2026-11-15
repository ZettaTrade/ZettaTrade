namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
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
/// The liquidation front's pins (T-242): an adapter without the forced-order
/// capability answers the honest absence (the unavailable flag, an empty
/// window state — never a dead panel or a fabricated fact), a capable venue's
/// raw events stream passes the wire events through untouched and releases
/// its lane on disposal, the window lane aggregates per pulse tick (the
/// bounded ring's look-back and capacity both trim honestly) and an adapter
/// switch atomically rebuilds the lanes (the facade's Attach rule).
/// </summary>
public sealed class LiquidationFeedServiceTests
{
    #region Constants

    /// <summary>Unit-scene completion budget: a focused wait, not a load budget.</summary>
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static readonly Symbol BtcUsdt = Symbol.Parse("BTC/USDT");

    private static readonly DateTimeOffset Base = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    #endregion

    #region Fakes

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Base;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>One contract's tracked stream: the shared subject and the open subscribers' count.</summary>
    private sealed class TrackedStream
    {
        public Subject<LiquidationEvent> Events { get; } = new();

        public int RefCount;
    }

    /// <summary>The capable market-data fake: the liquidation capability rides it, the starts and releases are counted.</summary>
    private sealed class LiquidationCapableMarketData : IMarketDataAdapter, ILiquidationFeedProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Symbol, TrackedStream> _streams = new();

        public string Id => "demo";

        public string Name => "demo";

        public bool IsConnected => true;

        public ExchangeCapabilities Capabilities => ExchangeCapabilities.MarketData;

        public int Starts;

        public int Releases;

        public IObservable<LiquidationEvent> SubscribeLiquidations(Symbol symbol, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Starts);
            var stream = _streams.GetOrAdd(symbol, _ => new TrackedStream());
            stream.RefCount++;
            return Observable.Create<LiquidationEvent>(observer =>
            {
                var subscription = stream.Events.Subscribe(observer);
                return Disposable.Create(() =>
                {
                    subscription.Dispose();
                    stream.RefCount--;
                    Interlocked.Increment(ref Releases);
                });
            });
        }

        /// <summary>Push one event onto the symbol's stream (the wire's arrival, any thread).</summary>
        public void Publish(Symbol symbol, LiquidationEvent ev) => _streams[symbol].Events.OnNext(ev);

        /// <summary>The symbol's currently open subscriber count (the lane's lifecycle evidence).</summary>
        public int Subscribers(Symbol symbol) => _streams.TryGetValue(symbol, out var stream) ? stream.RefCount : 0;

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

        public async Task<IReadOnlyList<Symbol>> GetSymbolsAsync(CancellationToken ct = default)
        {
            await Task.CompletedTask;
            return [BtcUsdt];
        }

        public IObservable<Ticker> SubscribeTicker(Symbol symbol) => Observable.Never<Ticker>();

        public IObservable<OrderBook> SubscribeOrderBook(Symbol symbol, int depth = 20) => Observable.Never<OrderBook>();

        public IObservable<MarketTrade> SubscribeTrades(Symbol symbol) => Observable.Never<MarketTrade>();

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>The capability void: a plain adapter, the honest no-capability venue.</summary>
    private sealed class VoidMarketData : IMarketDataAdapter
    {
        public string Id => "void";

        public string Name => "void";

        public bool IsConnected => true;

        public ExchangeCapabilities Capabilities => ExchangeCapabilities.MarketData;

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

        public async Task<IReadOnlyList<Symbol>> GetSymbolsAsync(CancellationToken ct = default)
        {
            await Task.CompletedTask;
            return [BtcUsdt];
        }

        public IObservable<Ticker> SubscribeTicker(Symbol symbol) => Observable.Never<Ticker>();

        public IObservable<OrderBook> SubscribeOrderBook(Symbol symbol, int depth = 20) => Observable.Never<OrderBook>();

        public IObservable<MarketTrade> SubscribeTrades(Symbol symbol) => Observable.Never<MarketTrade>();

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    #endregion

    #region Scenes

    private static ActiveExchangeService Facade(params IMarketDataAdapter[] adapters)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Changes.Returns(Observable.Empty<SettingsChange>());
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
        return new ActiveExchangeService(
            adapters,
            settings,
            NullLogger<ActiveExchangeService>.Instance);
    }

    private static (LiquidationFeedService Service, LiquidationCapableMarketData Adapter, FakeClock Clock, Subject<long> Pulse) BuildCapableScene()
    {
        var adapter = new LiquidationCapableMarketData();
        var clock = new FakeClock();
        var pulse = new Subject<long>();
        var service = new LiquidationFeedService(
            Facade(adapter),
            clock,
            NullLogger<LiquidationFeedService>.Instance,
            pulse);
        return (service, adapter, clock, pulse);
    }

    private static LiquidationEvent Ev(LiquidationSide side, decimal price, decimal quantity, DateTimeOffset? timestamp = null) => new()
    {
        Symbol = BtcUsdt,
        LiquidatedSide = side,
        Price = new Price(price),
        Quantity = new Quantity(quantity),
        Timestamp = timestamp ?? Base,
    };

    private static async Task PollUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - start > timeout)
            {
                throw new TimeoutException("The polled condition did not settle within the timeout.");
            }

            await Task.Delay(25);
        }
    }

    #endregion

    #region Capability

    [Fact]
    public async Task AnUncapableVenue_ReportsTheHonestAbsence()
    {
        var service = new LiquidationFeedService(
            Facade(new VoidMarketData()),
            new FakeClock(),
            NullLogger<LiquidationFeedService>.Instance);

        var availability = await service.Availability.FirstAsync().ToTask().WaitAsync(Wait);

        availability.Should().BeFalse("the venue's forced-order wire is absent — the honest unavailable flag");
    }

    [Fact]
    public async Task ACapableVenue_ReportsTheCapability()
    {
        var (service, _, _, _) = BuildCapableScene();

        var availability = await service.Availability.FirstAsync().ToTask().WaitAsync(Wait);

        availability.Should().BeTrue("the venue publishes forced-liquidation events");
    }

    [Fact]
    public async Task AnUncapableVenue_WindowLane_ServesTheEmptyStatePerPulse()
    {
        var pulse = new Subject<long>();
        var service = new LiquidationFeedService(
            Facade(new VoidMarketData()),
            new FakeClock(),
            NullLogger<LiquidationFeedService>.Instance,
            pulse);
        var snapshots = new List<LiquidationsWindowState>();
        using var subscription = service.SubscribeWindow(BtcUsdt).Subscribe(snapshots.Add);
        pulse.OnNext(1);

        await PollUntilAsync(() => snapshots.Count >= 1, Wait);

        var state = snapshots[^1];
        state.Recent.Should().BeEmpty("no capability is the honest empty state, never a fabricated event");
        state.RecentCount.Should().Be(0);
        state.RecentNotional.Should().Be(0m);
        state.OpenCascade.Should().BeNull();
    }

    #endregion

    #region Raw Events Stream

    [Fact]
    public async Task TheRawEventsStream_CarriesTheWireEvents()
    {
        var (service, adapter, _, _) = BuildCapableScene();
        var ev = Ev(LiquidationSide.Long, 9910.5m, 0.011m);

        var first = service.SubscribeEvents(BtcUsdt).FirstAsync().ToTask();
        adapter.Publish(BtcUsdt, ev);

        (await first.WaitAsync(Wait)).Should().BeSameAs(ev, "the raw stream is the wire's passthrough, not a re-shaped copy");
    }

    [Fact]
    public async Task DisposingTheRawSubscription_ReleasesTheLane()
    {
        var (service, adapter, _, _) = BuildCapableScene();

        var first = service.SubscribeEvents(BtcUsdt).FirstAsync().ToTask();
        adapter.Publish(BtcUsdt, Ev(LiquidationSide.Short, 1826.73m, 13.53m));
        await first.WaitAsync(Wait);

        adapter.Releases.Should().Be(1, "the consumed subscription released the symbol's lane");
        adapter.Subscribers(BtcUsdt).Should().Be(0);
    }

    [Fact]
    public async Task AnAdapterSwitch_RebuildsTheLanes()
    {
        var adapter = new LiquidationCapableMarketData();
        var facade = Facade(adapter, new VoidMarketData());
        var service = new LiquidationFeedService(
            facade,
            new FakeClock(),
            NullLogger<LiquidationFeedService>.Instance);
        var received = new List<LiquidationEvent>();
        var availability = new List<bool>();
        using var eventsSubscription = service.SubscribeEvents(BtcUsdt).Subscribe(received.Add);
        using var availabilitySubscription = service.Availability.Subscribe(availability.Add);

        adapter.Publish(BtcUsdt, Ev(LiquidationSide.Long, 50_000m, 6m));
        await PollUntilAsync(() => received.Count >= 1, Wait);
        await facade.SwitchToAsync("void");
        adapter.Publish(BtcUsdt, Ev(LiquidationSide.Long, 50_000m, 6m));

        received.Should().ContainSingle("the switch atomically cancelled the previous exchange's stream");
        adapter.Subscribers(BtcUsdt).Should().Be(0, "the switched-away lane is released, never left attached");
        availability.Should().Equal(new[] { true, false }, "the capability probe re-answers per adapter change");
    }

    #endregion

    #region Window Lane

    [Fact]
    public async Task TheWindowLane_PublishesTheAggregatedStatePerPulse()
    {
        var (service, adapter, _, pulse) = BuildCapableScene();
        var snapshots = new List<LiquidationsWindowState>();
        using var subscription = service.SubscribeWindow(BtcUsdt).Subscribe(snapshots.Add);

        adapter.Publish(BtcUsdt, Ev(LiquidationSide.Long, 50_000m, 6m));
        pulse.OnNext(1);
        await PollUntilAsync(() => snapshots.Count >= 1, Wait);

        var state = snapshots[^1];
        state.RecentCount.Should().Be(1, "the pulse publishes the ring's current state");
        state.RecentNotional.Should().Be(300_000m);
        state.OpenCascade.Should().NotBeNull("a fresh qualifying run badges the window");
        state.OpenCascade!.OrderCount.Should().Be(1);
    }

    [Fact]
    public async Task TheLookbackEvictsStaleEvents()
    {
        var (service, adapter, clock, pulse) = BuildCapableScene();
        var snapshots = new List<LiquidationsWindowState>();
        using var subscription = service.SubscribeWindow(BtcUsdt).Subscribe(snapshots.Add);

        adapter.Publish(BtcUsdt, Ev(LiquidationSide.Long, 50_000m, 6m, Base.AddMinutes(-3)));
        pulse.OnNext(1);
        await PollUntilAsync(() => snapshots.Count >= 1, Wait);

        var state = snapshots[^1];
        state.RecentCount.Should().Be(0, "an event older than the 2-minute look-back is out of the ring");
        state.RecentNotional.Should().Be(0m);
        state.OpenCascade.Should().BeNull();
    }

    [Fact]
    public async Task ARingOverTheCapacity_TrimsTheOldest()
    {
        var (service, adapter, _, pulse) = BuildCapableScene();
        var snapshots = new List<LiquidationsWindowState>();
        using var subscription = service.SubscribeWindow(BtcUsdt).Subscribe(snapshots.Add);

        // 300 small events, each a fresh 100 notional within the 2-minute
        // look-back: over the lane's 256 ring bound, under the cascade
        // threshold — the oldest 44 fall out.
        for (var i = 0; i < 300; i++)
        {
            adapter.Publish(BtcUsdt, Ev(LiquidationSide.Long, 100m, 1m, Base.AddMilliseconds(-i)));
        }

        pulse.OnNext(1);
        await PollUntilAsync(() => snapshots.Count >= 1, Wait);

        var state = snapshots[^1];
        state.RecentCount.Should().Be(256, "the ring's capacity bound is real, never unbounded growth");
        state.RecentNotional.Should().Be(25_600m, "the 256 freshest events' summed notional");
        state.OpenCascade.Should().BeNull("a stream of small forced orders never fabricates a cascade");
    }

    [Fact]
    public void ANullSymbol_IsRejected()
    {
        var (service, _, _, _) = BuildCapableScene();

        var act = () => service.SubscribeEvents(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    #endregion
}
