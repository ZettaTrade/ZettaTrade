namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NSubstitute;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Tests for the symbol list cache: the adapter must be hit exactly once
/// across the app lifetime, a failed fetch must never be cached (the next
/// dialog open retries), and the warm-up path keeps startup non-blocking.
/// </summary>
public sealed class SymbolListProviderTests
{
    private readonly IMarketDataAdapter _marketData = Substitute.For<IMarketDataAdapter>();

    private SymbolListProvider CreateProvider() =>
        new(
            _marketData,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<SymbolListProvider>>());

    [Fact]
    public async Task GetSymbolsAsync_Twice_FetchesOnceAndReturnsTheCachedList()
    {
        // Arrange
        var provider = CreateProvider();
        _marketData.GetSymbolsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<Symbol>>(new List<Symbol>
            {
                Symbol.Parse("BTC/USDT"),
                Symbol.Parse("ETH/BTC"),
            }));

        // Act
        var first = await provider.GetSymbolsAsync(CancellationToken.None);
        var second = await provider.GetSymbolsAsync(CancellationToken.None);

        // Assert
        await _marketData.Received(1).GetSymbolsAsync(Arg.Any<CancellationToken>());
        second.Should().BeEquivalentTo(first, "the cache answers the second open");
        first.Should().ContainInOrder("BTC/USDT", "ETH/BTC");
    }

    [Fact]
    public async Task GetSymbolsAsync_WhenTheFirstFetchFails_TheRetryRefetches()
    {
        // Arrange: offline on the first call, available on the second — a
        // failed list must never be cached, otherwise an unlucky warm-up
        // would pin an empty autocomplete for the whole session.
        var provider = CreateProvider();
        _marketData.GetSymbolsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IReadOnlyList<Symbol>>(new HttpRequestException("offline")));
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetSymbolsAsync(CancellationToken.None));

        _marketData.GetSymbolsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<Symbol>>(new List<Symbol>
            {
                Symbol.Parse("BTC/USDT"),
            }));

        // Act
        var result = await provider.GetSymbolsAsync(CancellationToken.None);

        // Assert
        await _marketData.Received(2).GetSymbolsAsync(Arg.Any<CancellationToken>());
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
        var provider = CreateProvider();
        _marketData.GetSymbolsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<Symbol>>(new List<Symbol>
            {
                Symbol.Parse("BTC/USDT"),
            }));

        // Act
        var initialization = provider.InitializeAsync();
        var cached = await provider.GetSymbolsAsync(CancellationToken.None);

        // Assert
        initialization.IsCompleted.Should().BeTrue("startup must not await the network");
        cached.Should().ContainSingle("the warm-up and the dialog open share one fetch");
        await _marketData.Received(1).GetSymbolsAsync(Arg.Any<CancellationToken>());
    }
}
