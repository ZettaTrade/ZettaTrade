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
/// The leverage brackets' front's pins (T-243): a venue without the bracket
/// capability answers the honest no-table (null, no network), a spot symbol
/// answers before the provider is ever consulted, a capable venue's fetch
/// stores and serves from the cache (a fresh symbol answers without a
/// second round-trip), the freshness window's expiry refetches, a full
/// cache evicts its oldest fetch, and the venue's own refusal surfaces to
/// the caller — never a fabricated table.
/// </summary>
public sealed class LeverageBracketsServiceTests
{
    private static readonly Symbol BtcPerp = Symbol.Parse("BTC/USDT:USDT");

    private static readonly DateTimeOffset Base = new(2026, 3, 2, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Base;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>The capable market-data fake: the bracket table is scripted, the fetches are counted.</summary>
    private sealed class BracketCapableMarketData : IMarketDataAdapter, ILeverageBracketsProvider
    {
        public string Id => "demo";

        public string Name => "demo";

        public bool IsConnected => true;

        public ExchangeCapabilities Capabilities => ExchangeCapabilities.MarketData;

        public int Fetches { get; private set; }

        public List<Symbol> RequestedSymbols { get; } = new();

        public Func<Symbol, IReadOnlyList<LeverageBracket>> TablePlan { get; set; } =
            _ => [new LeverageBracket(0m, 50_000m, 50m, 0.005m)];

        public Func<Symbol, Task<IReadOnlyList<LeverageBracket>>>? RefusalPlan { get; set; }

        public async Task<IReadOnlyList<LeverageBracket>> GetLeverageBracketsAsync(
            Symbol symbol,
            CancellationToken ct = default)
        {
            await Task.CompletedTask;
            Fetches++;
            RequestedSymbols.Add(symbol);
            if (RefusalPlan is { } refusal)
            {
                return await refusal(symbol);
            }

            return TablePlan(symbol);
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

    private static (LeverageBracketsService Service, BracketCapableMarketData Adapter, FakeClock Clock) BuildCapableScene()
    {
        var adapter = new BracketCapableMarketData();
        var settings = Substitute.For<ISettingsService>();
        settings.Changes.Returns(Observable.Empty<SettingsChange>());
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
        var exchanges = new ActiveExchangeService(
            [adapter],
            settings,
            NullLogger<ActiveExchangeService>.Instance);
        var clock = new FakeClock();
        return (new LeverageBracketsService(exchanges, clock), adapter, clock);
    }

    /// <summary>A venue without the capability answers the honest no-table: null, and the provider is never consulted.</summary>
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
        var service = new LeverageBracketsService(exchanges, new FakeClock());

        var answer = await service.GetAsync(BtcPerp);

        answer.Should().BeNull("no capability is the honest no-table, never an empty stand-in");
    }

    /// <summary>A spot symbol has no risk table at any venue: the front answers the no-table before the provider is ever consulted.</summary>
    [Fact]
    public async Task ASpotSymbol_AnswersTheHonestNoTable_WithoutConsultingTheProvider()
    {
        var (service, adapter, _) = BuildCapableScene();

        var answer = await service.GetAsync(Symbol.Parse("BTC/USDT"));

        answer.Should().BeNull("a spot symbol has no leverage brackets, on a capable venue too");
        adapter.Fetches.Should().Be(0, "the spot guard answers before the provider call");
    }

    /// <summary>A capable venue's first fetch stores the table; a second fresh call answers from the cache.</summary>
    [Fact]
    public async Task AStoredFetch_ServesTheFreshSymbol_FromTheCache()
    {
        var (service, adapter, _) = BuildCapableScene();

        var first = await service.GetAsync(BtcPerp);
        var second = await service.GetAsync(BtcPerp);

        adapter.Fetches.Should().Be(1, "the fresh symbol answers from the cache");
        first.Should().NotBeNull();
        second.Should().BeSameAs(first, "the cached table IS the served one");
    }

    /// <summary>The symbol keys into the cache entry: two symbols are two tables, both served.</summary>
    [Fact]
    public async Task TheSymbolKeysIntoTheCache()
    {
        var (service, adapter, _) = BuildCapableScene();

        var btc = await service.GetAsync(BtcPerp);
        var eth = await service.GetAsync(Symbol.Parse("ETH/USDT:USDT"));

        adapter.Fetches.Should().Be(2, "the symbol is part of the entry's identity");
        btc.Should().NotBeSameAs(eth, "two symbols are two tables, never a shared one");
        adapter.RequestedSymbols.Should().Equal([BtcPerp, Symbol.Parse("ETH/USDT:USDT")], "the symbol reaches the provider call");
        var again = await service.GetAsync(BtcPerp);
        adapter.Fetches.Should().Be(2, "the first symbol answers from the cache on the re-ask");
        again.Should().BeSameAs(btc);
    }

    /// <summary>The freshness window's expiry refetches: the tables move at the venue's risk-review cadence, the front follows.</summary>
    [Fact]
    public async Task AnExpiredFreshnessWindow_Refetches()
    {
        var (service, adapter, clock) = BuildCapableScene();

        await service.GetAsync(BtcPerp);
        clock.Now = Base.AddMinutes(16);

        await service.GetAsync(BtcPerp);

        adapter.Fetches.Should().Be(2, "the stale entry does not answer");
    }

    /// <summary>A full cache evicts its oldest fetch: the entry bound is real, never unbounded growth.</summary>
    [Fact]
    public async Task AFullCache_EvictsItsOldestFetch()
    {
        var (service, adapter, clock) = BuildCapableScene();

        // Fill the cache beyond its bound: one entry per
        // (exchange|symbol) key, so the eviction reads on distinct symbols.
        var symbols = Enumerable.Range(0, LeverageBracketsService.MaxCacheEntries + 1)
            .Select(number => Symbol.Parse($"P{number}/USDT:USDT"))
            .ToList();
        foreach (var symbol in symbols)
        {
            clock.Now = clock.Now.AddMinutes(1);
            await service.GetAsync(symbol);
        }

        var filled = adapter.Fetches;

        // The first entry (the oldest fetch) was evicted: its symbol misses
        // the cache and refetches.
        clock.Now = clock.Now.AddMinutes(1);
        await service.GetAsync(symbols[0]);
        adapter.Fetches.Should().Be(filled + 1, "only the evicted symbol refetched");
        adapter.RequestedSymbols[^1].Should().Be(symbols[0], "the re-ask rode the provider call");

        // The newest entry still answers from the cache: the table front
        // has no window to outgrow, the freshness window alone governs.
        var before = adapter.Fetches;
        await service.GetAsync(symbols[^1]);

        adapter.Fetches.Should().Be(before, "the newest entry stayed cached and fresh");
    }

    /// <summary>The venue's own refusal surfaces to the caller: the estimate's degrade lane, never a fabricated table.</summary>
    [Fact]
    public async Task TheVenuesRefusal_SurfacesToTheCaller()
    {
        var (service, adapter, _) = BuildCapableScene();
        adapter.RefusalPlan = symbol => Task.FromException<IReadOnlyList<LeverageBracket>>(
            new InvalidOperationException("the venue refused"));

        var act = () => service.GetAsync(BtcPerp);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "the venue refused the request; the caller's degrade lane logs it");
    }
}
