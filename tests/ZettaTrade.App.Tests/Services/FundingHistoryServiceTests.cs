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
/// The funding-history front's pins (T-240): a venue without the
/// funding-history capability answers the honest no-pane (null, no
/// network), a capable venue's fetch stores and serves from the cache (a
/// covered fresh window answers without a second round-trip), the
/// freshness window's expiry and a wider window both refetch, a full
/// cache evicts its oldest fetch, and the venue's own refusal surfaces to
/// the caller — never a fabricated series.
/// </summary>
public sealed class FundingHistoryServiceTests
{
    private static readonly Symbol BtcPerp = Symbol.Parse("BTC/USDT:USDT");

    private static readonly DateTimeOffset Base = new(2026, 3, 2, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Base;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>The capable market-data fake: the funding history is scripted, the fetches are counted.</summary>
    private sealed class FundingCapableMarketData : IMarketDataAdapter, IFundingHistoryProvider
    {
        public string Id => "demo";

        public string Name => "demo";

        public bool IsConnected => true;

        public ExchangeCapabilities Capabilities => ExchangeCapabilities.MarketData;

        public int Fetches { get; private set; }

        public Func<Symbol, DateTimeOffset, IReadOnlyList<FundingRatePoint>> HistoryPlan { get; set; } =
            (_, _) => [new FundingRatePoint(Base, 0.0001m)];

        public Func<Symbol, DateTimeOffset, Task<IReadOnlyList<FundingRatePoint>>>? RefusalPlan { get; set; }

        public async Task<IReadOnlyList<FundingRatePoint>> GetFundingRateHistoryAsync(
            Symbol symbol,
            DateTimeOffset from,
            DateTimeOffset? to = null,
            int limit = 200,
            CancellationToken ct = default)
        {
            await Task.CompletedTask;
            Fetches++;
            if (RefusalPlan is { } refusal)
            {
                return await refusal(symbol, from);
            }

            return HistoryPlan(symbol, from);
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

    private static (FundingHistoryService Service, FundingCapableMarketData Adapter, FakeClock Clock) BuildCapableScene()
    {
        var adapter = new FundingCapableMarketData();
        var settings = Substitute.For<ISettingsService>();
        settings.Changes.Returns(Observable.Empty<SettingsChange>());
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
        var exchanges = new ActiveExchangeService(
            [adapter],
            settings,
            NullLogger<ActiveExchangeService>.Instance);
        var clock = new FakeClock();
        return (new FundingHistoryService(exchanges, clock), adapter, clock);
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
        var service = new FundingHistoryService(exchanges, new FakeClock());

        var answer = await service.GetAsync(BtcPerp, Base);

        answer.Should().BeNull("no capability is the honest no-pane, never an empty stand-in");
    }

    /// <summary>A spot symbol has no funding history at any venue: the front answers the no-pane before the provider is ever consulted.</summary>
    [Fact]
    public async Task ASpotSymbol_AnswersTheHonestNoPane_WithoutConsultingTheProvider()
    {
        var (service, adapter, _) = BuildCapableScene();

        var answer = await service.GetAsync(Symbol.Parse("BTC/USDT"), Base);

        answer.Should().BeNull("a spot symbol has no funding events, on a capable venue too");
        adapter.Fetches.Should().Be(0, "the spot guard answers before the provider call");
    }

    /// <summary>A capable venue's first fetch stores the page; a second covered fresh call answers from the cache.</summary>
    [Fact]
    public async Task AStoredFetch_ServesTheCoveredFreshWindow_FromTheCache()
    {
        var (service, adapter, _) = BuildCapableScene();

        var first = await service.GetAsync(BtcPerp, Base);
        var second = await service.GetAsync(BtcPerp, Base);

        adapter.Fetches.Should().Be(1, "the covered fresh window answers from the cache");
        first.Should().NotBeNull();
        second.Should().BeSameAs(first, "the cached page IS the served one");
    }

    /// <summary>The freshness window's expiry refetches: the pane is an overview, not a frozen snapshot.</summary>
    [Fact]
    public async Task AnExpiredFreshnessWindow_Refetches()
    {
        var (service, adapter, clock) = BuildCapableScene();

        await service.GetAsync(BtcPerp, Base);
        clock.Now = Base.AddMinutes(16);

        await service.GetAsync(BtcPerp, Base);

        adapter.Fetches.Should().Be(2, "the covered-but-stale entry does not answer");
    }

    /// <summary>A wider window than the cached one refetches: the coverage is exact, never padded with a guess.</summary>
    [Fact]
    public async Task AWiderWindow_Refetches()
    {
        var (service, adapter, clock) = BuildCapableScene();

        await service.GetAsync(BtcPerp, Base);
        clock.Now = Base.AddMinutes(1);

        await service.GetAsync(BtcPerp, Base.AddDays(-2));

        adapter.Fetches.Should().Be(2, "the cached page does not cover the wider window");
    }

    /// <summary>A full cache evicts its oldest fetch: the entry bound is real, never unbounded growth.</summary>
    [Fact]
    public async Task AFullCache_EvictsItsOldestFetch()
    {
        var (service, adapter, clock) = BuildCapableScene();

        // Fill the cache beyond its bound: one entry per (exchange|symbol)
        // key, so the eviction reads on distinct symbols.
        var symbols = Enumerable.Range(0, FundingHistoryService.MaxCacheEntries + 1)
            .Select(number => Symbol.Parse($"P{number}/USDT:USDT"))
            .ToList();
        foreach (var symbol in symbols)
        {
            clock.Now = clock.Now.AddMinutes(1);
            await service.GetAsync(symbol, Base);
        }

        // The first entry (the oldest fetch) was evicted: its window misses
        // the cache and refetches; the newest entry still answers from it.
        clock.Now = clock.Now.AddMinutes(1);
        await service.GetAsync(symbols[0], Base);
        var before = adapter.Fetches;
        await service.GetAsync(symbols[^1], Base);

        adapter.Fetches.Should().Be(before + 1, "only the evicted symbol refetched; the newest entry stayed cached");
    }

    /// <summary>The venue's own refusal surfaces to the caller: the pane's degrade lane, never a fabricated series.</summary>
    [Fact]
    public async Task TheVenuesRefusal_SurfacesToTheCaller()
    {
        var (service, adapter, _) = BuildCapableScene();
        adapter.RefusalPlan = (symbol, from) => Task.FromException<IReadOnlyList<FundingRatePoint>>(
            new InvalidOperationException("the venue refused"));

        var act = () => service.GetAsync(BtcPerp, Base);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "the venue refused the request; the caller's degrade lane logs it");
    }
}
