namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Tests for the symbol list cache: the active adapter must be hit exactly
/// once across one exchange's lifetime, a failed fetch must never be cached
/// (the next dialog open retries), the warm-up path keeps startup
/// non-blocking, and an adapter change flushes the cache so the next fetch
/// targets the exchange the selector picked — a fetch racing a switch
/// returns its stale data but never poisons the cache with it.
/// </summary>
public sealed class SymbolListProviderTests
{
    private readonly IMarketDataAdapter _binance = Substitute.For<IMarketDataAdapter>();
    private readonly IMarketDataAdapter _demo = Substitute.For<IMarketDataAdapter>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();

    public SymbolListProviderTests()
    {
        _binance.Id.Returns("binance");
        _binance.Name.Returns("Binance");
        _demo.Id.Returns("demo");
        _demo.Name.Returns("Demo");

        StubSymbols(_binance, "BTC/USDT", "ETH/BTC");
        StubSymbols(_demo, "SOL/USDC");
    }

    private static void StubSymbols(IMarketDataAdapter adapter, params string[] names) =>
        adapter.GetSymbolsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<Symbol>>(
                names.Select(Symbol.Parse).ToList()));

    private ActiveExchangeService CreateService() => new(
        [_binance, _demo],
        _settings,
        NullLogger<ActiveExchangeService>.Instance);

    private SymbolListProvider CreateProvider(ActiveExchangeService service) =>
        new(service, NullLogger<SymbolListProvider>.Instance);

    [Fact]
    public async Task GetSymbolsAsync_Twice_FetchesOnceAndReturnsTheCachedList()
    {
        // Arrange
        var provider = CreateProvider(CreateService());

        // Act
        var first = await provider.GetSymbolsAsync(CancellationToken.None);
        var second = await provider.GetSymbolsAsync(CancellationToken.None);

        // Assert
        await _binance.Received(1).GetSymbolsAsync(Arg.Any<CancellationToken>());
        await _demo.DidNotReceive().GetSymbolsAsync(Arg.Any<CancellationToken>());
        second.Should().BeEquivalentTo(first, "the cache answers the second open");
        first.Should().ContainInOrder(new[] { "BTC/USDT", "ETH/BTC" });
    }

    [Fact]
    public async Task GetSymbolsAsync_WhenTheFirstFetchFails_TheRetryRefetches()
    {
        // Arrange: offline on the first call, available on the second — a
        // failed list must never be cached, otherwise an unlucky warm-up
        // would pin an empty autocomplete for the whole session.
        var provider = CreateProvider(CreateService());
        _binance.GetSymbolsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IReadOnlyList<Symbol>>(new HttpRequestException("offline")));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.GetSymbolsAsync(CancellationToken.None));
        StubSymbols(_binance, "BTC/USDT");

        // Act
        var result = await provider.GetSymbolsAsync(CancellationToken.None);

        // Assert
        await _binance.Received(2).GetSymbolsAsync(Arg.Any<CancellationToken>());
        result.Should().ContainSingle("the provider caches only successes");
    }

    [Fact]
    public async Task InitializeAsync_ReturnsWithoutBlockingAndThenTheCacheAnswers()
    {
        // Arrange: the composition root awaits InitializeAsync linearly, so a
        // warm-up that blocked startup would violate the < 3 s cold-start
        // target. The fetch is scheduled; the background and the first
        // dialog open share one fetch through the provider's semaphore
        // (whichever runs first), so exactly one adapter hit remains.
        var service = CreateService();
        var provider = CreateProvider(service);

        // Act
        var initialization = provider.InitializeAsync();
        var cached = await provider.GetSymbolsAsync(CancellationToken.None);

        // Assert
        initialization.IsCompleted.Should().BeTrue("startup must not await the network");
        cached.Should().ContainInOrder(
            new[] { "BTC/USDT", "ETH/BTC" },
            "the warm-up and the dialog open share one fetch");
        await _binance.Received(1).GetSymbolsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdapterChange_FlushesTheCacheSoTheNextFetchTargetsTheNewAdapter()
    {
        // Arrange
        var service = CreateService();
        var provider = CreateProvider(service);

        var before = await provider.GetSymbolsAsync(CancellationToken.None);
        before.Should().ContainInOrder(
            new[] { "BTC/USDT", "ETH/BTC" },
            "the first fetch answers the active exchange");

        // Act: the user switches to the second exchange.
        await service.SwitchToAsync("demo");

        var after = await provider.GetSymbolsAsync(CancellationToken.None);

        // Assert
        await _binance.Received(1).GetSymbolsAsync(Arg.Any<CancellationToken>());
        await _demo.Received(1).GetSymbolsAsync(Arg.Any<CancellationToken>());
        after.Should().ContainSingle("the cache answers the new exchange, not the old list");
    }

    [Fact]
    public async Task FetchFinishingAfterASwitch_ReturnsStaleDataButNeverCachesIt()
    {
        // Arrange: the fetch of the previous exchange hangs until after the
        // switch resolves — its list belongs to the wrong exchange.
        var pending = new TaskCompletionSource<IReadOnlyList<Symbol>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _binance.GetSymbolsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => pending.Task);

        var service = CreateService();
        var provider = CreateProvider(service);

        var racingFetch = provider.GetSymbolsAsync(CancellationToken.None);

        // Act: the switch lands while the fetch is in flight.
        await service.SwitchToAsync("demo");
        pending.SetResult([Symbol.Parse("BTC/USDT"), Symbol.Parse("ETH/BTC")]);

        var stale = await racingFetch;
        var fresh = await provider.GetSymbolsAsync(CancellationToken.None);

        // Assert
        stale.Should().Contain(
            "BTC/USDT",
            "the caller still receives the data it requested (the next call repairs it)");
        fresh.Should().ContainSingle("the cache re-fetched from the new adapter");
        await _binance.Received(1).GetSymbolsAsync(Arg.Any<CancellationToken>());
        // The retry after the switch targets the active exchange.
        await _demo.Received(1).GetSymbolsAsync(Arg.Any<CancellationToken>());
    }
}
