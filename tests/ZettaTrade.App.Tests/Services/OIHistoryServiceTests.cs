namespace ZettaTrade.App.Tests.Services;

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
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// The open-interest history's front's pins (T-241): a venue without the
/// OI-history capability answers the honest no-pane (null, no network), a
/// capable venue's fetch stores and serves from the cache (a covered fresh
/// window answers without a second round-trip, the bar interval keyed into
/// the entry), the freshness window's expiry and a wider window both
/// refetch, a full cache evicts its oldest fetch, and the venue's own
/// refusal surfaces to the caller — never a fabricated series.
/// </summary>
public sealed class OIHistoryServiceTests
{
    private static readonly Symbol BtcPerp = Symbol.Parse("BTC/USDT:USDT");

    private static readonly DateTimeOffset Base = new(2026, 3, 2, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Base;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>The capable market-data fake: the OI history is scripted, the fetches are counted.</summary>
    private sealed class OICapableMarketData : IMarketDataAdapter, IOIHistoryProvider
    {
        public string Id => "demo";

        public string Name => "demo";

        public bool IsConnected => true;

        public ExchangeCapabilities Capabilities => ExchangeCapabilities.MarketData;

        public int Fetches { get; private set; }

        public List<TimeFrame> RequestedIntervals { get; } = new();

        public Func<Symbol, IReadOnlyList<OpenInterestPoint>> HistoryPlan { get; set; } =
            _ => [new OpenInterestPoint(Base, 316131537.00000000m)];

        public Func<Symbol, Task<IReadOnlyList<OpenInterestPoint>>>? RefusalPlan { get; set; }

        public async Task<IReadOnlyList<OpenInterestPoint>> GetOpenInterestHistoryAsync(
            Symbol symbol,
            TimeFrame barInterval,
            DateTimeOffset from,
            DateTimeOffset? to = null,
            int limit = 200,
            CancellationToken ct = default)
        {
            await Task.CompletedTask;
            Fetches++;
            RequestedIntervals.Add(barInterval);
            if (RefusalPlan is { } refusal)
            {
                return await refusal(symbol);
            }

            return HistoryPlan(symbol);
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

        public async Task<IReadOnlyList<Symbol>> GetSymbolsAsync(CancellationToken ct = default)
        {
            await Task.CompletedTask;
            return [BtcPerp];
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
            return [BtcPerp];
        }

        public IObservable<Ticker> SubscribeTicker(Symbol symbol) => Observable.Never<Ticker>();

        public IObservable<OrderBook> SubscribeOrderBook(Symbol symbol, int depth = 20) => Observable.Never<OrderBook>();

        public IObservable<MarketTrade> SubscribeTrades(Symbol symbol) => Observable.Never<MarketTrade>();

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static (OIHistoryService Service, OICapableMarketData Adapter, FakeClock Clock) BuildCapableScene()
    {
        var adapter = new OICapableMarketData();
        var settings = Substitute.For<ISettingsService>();
        settings.Changes.Returns(Observable.Empty<SettingsChange>());
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
        var exchanges = new ActiveExchangeService(
            [adapter],
            settings,
            NullLogger<ActiveExchangeService>.Instance);
        var clock = new FakeClock();
        return (new OIHistoryService(exchanges, clock), adapter, clock);
    }

    /// <summary>A venue without the capability answers the honest no-pane: null, and the provider is never consulted.</summary>
    [Fact]
    public async Task AnUncapableVenue_AnswersTheHonestNull_WithoutANetworkCall()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Changes.Returns(Observable.Empty<SettingsChange>());
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
        var exchanges = new ActiveExchangeService(
            [new VoidMarketData()],
            settings,
            NullLogger<ActiveExchangeService>.Instance);
        var service = new OIHistoryService(exchanges, new FakeClock());

        var answer = await service.GetAsync(BtcPerp, TimeFrame.M15, Base);

        answer.Should().BeNull("no capability is the honest no-pane, never an empty stand-in");
    }

    /// <summary>A spot symbol has no open-interest history at any venue: the front answers the no-pane before the provider is ever consulted.</summary>
    [Fact]
    public async Task ASpotSymbol_AnswersTheHonestNoPane_WithoutConsultingTheProvider()
    {
        var (service, adapter, _) = BuildCapableScene();

        var answer = await service.GetAsync(Symbol.Parse("BTC/USDT"), TimeFrame.M15, Base);

        answer.Should().BeNull("a spot symbol has no open interest, on a capable venue too");
        adapter.Fetches.Should().Be(0, "the spot guard answers before the provider call");
    }

    /// <summary>A capable venue's first fetch stores the page; a second covered fresh call answers from the cache.</summary>
    [Fact]
    public async Task AStoredFetch_ServesTheCoveredFreshWindow_FromTheCache()
    {
        var (service, adapter, _) = BuildCapableScene();

        var first = await service.GetAsync(BtcPerp, TimeFrame.M15, Base);
        var second = await service.GetAsync(BtcPerp, TimeFrame.M15, Base);

        adapter.Fetches.Should().Be(1, "the covered fresh window answers from the cache");
        first.Should().NotBeNull();
        second.Should().BeSameAs(first, "the cached page IS the served one");
    }

    /// <summary>The bar interval keys into the cache entry: the same symbol at two intervals is two pages, both served.</summary>
    [Fact]
    public async Task TheIntervalKeysIntoTheCache()
    {
        var (service, adapter, _) = BuildCapableScene();

        var quarterHour = await service.GetAsync(BtcPerp, TimeFrame.M15, Base);
        var hour = await service.GetAsync(BtcPerp, TimeFrame.H1, Base);

        adapter.Fetches.Should().Be(2, "the interval is part of the entry's identity");
        quarterHour.Should().NotBeSameAs(hour, "two intervals are two pages, never a shared one");
        adapter.RequestedIntervals.Should().Equal([TimeFrame.M15, TimeFrame.H1], "the bar interval reaches the provider call");
        var again = await service.GetAsync(BtcPerp, TimeFrame.M15, Base);
        adapter.Fetches.Should().Be(2, "the quarter-hour page answers from the cache on the re-ask");
        again.Should().BeSameAs(quarterHour);
    }

    /// <summary>The freshness window's expiry refetches: the pane is an overview, not a frozen snapshot.</summary>
    [Fact]
    public async Task AnExpiredFreshnessWindow_Refetches()
    {
        var (service, adapter, clock) = BuildCapableScene();

        await service.GetAsync(BtcPerp, TimeFrame.M15, Base);
        clock.Now = Base.AddMinutes(16);

        await service.GetAsync(BtcPerp, TimeFrame.M15, Base);

        adapter.Fetches.Should().Be(2, "the covered-but-stale entry does not answer");
    }

    /// <summary>A wider window than the cached one refetches: the coverage is exact, never padded with a guess.</summary>
    [Fact]
    public async Task AWiderWindow_Refetches()
    {
        var (service, adapter, clock) = BuildCapableScene();

        await service.GetAsync(BtcPerp, TimeFrame.D1, Base);
        clock.Now = Base.AddMinutes(1);

        await service.GetAsync(BtcPerp, TimeFrame.D1, Base.AddDays(-2));

        adapter.Fetches.Should().Be(2, "the cached page does not cover the wider window");
    }

    /// <summary>A full cache evicts its oldest fetch: the entry bound is real, never unbounded growth.</summary>
    [Fact]
    public async Task AFullCache_EvictsItsOldestFetch()
    {
        var (service, adapter, clock) = BuildCapableScene();

        // Fill the cache beyond its bound: one entry per
        // (exchange|symbol|interval) key, so the eviction reads on distinct
        // symbols at one interval.
        var symbols = Enumerable.Range(0, OIHistoryService.MaxCacheEntries + 1)
            .Select(number => Symbol.Parse($"P{number}/USDT:USDT"))
            .ToList();
        foreach (var symbol in symbols)
        {
            clock.Now = clock.Now.AddMinutes(1);
            await service.GetAsync(symbol, TimeFrame.M15, Base);
        }

        // The first entry (the oldest fetch) was evicted: its window misses
        // the cache and refetches; the newest entry still answers from it.
        clock.Now = clock.Now.AddMinutes(1);
        await service.GetAsync(symbols[0], TimeFrame.M15, Base);
        var before = adapter.Fetches;
        await service.GetAsync(symbols[^1], TimeFrame.M15, Base);

        adapter.Fetches.Should().Be(before + 1, "only the evicted symbol refetched; the newest entry stayed cached");
    }

    /// <summary>The venue's own refusal surfaces to the caller: the pane's degrade lane, never a fabricated series.</summary>
    [Fact]
    public async Task TheVenuesRefusal_SurfacesToTheCaller()
    {
        var (service, adapter, _) = BuildCapableScene();
        adapter.RefusalPlan = symbol => Task.FromException<IReadOnlyList<OpenInterestPoint>>(
            new InvalidOperationException("the venue refused"));

        var act = () => service.GetAsync(BtcPerp, TimeFrame.M15, Base);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "the venue refused the request; the caller's degrade lane logs it");
    }
}
