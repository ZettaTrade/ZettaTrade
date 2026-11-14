namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// Tests for the symbol list cache (T-228): each exchange's list is fetched
/// exactly once per session and every later ask answers from that cache, a
/// failed fetch is never cached (the next ask retries), the warm-up covers
/// every registered exchange without blocking startup, and a switch targets
/// the picked exchange's own entry — a late fetch still returns its data to
/// its asker and caches it under its own exchange id.
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

    [Fact]
    public async Task GetAdapterSymbolsAsync_Twice_FetchesOncePerExchange()
    {
        // The per-exchange surface the palette's warm-up and the
        // heatmap/screener universes ride: one ask per adapter per session.
        var provider = CreateProvider(CreateService());

        var first = await provider.GetAdapterSymbolsAsync(_binance, CancellationToken.None);
        var second = await provider.GetAdapterSymbolsAsync(_binance, CancellationToken.None);
        var demo = await provider.GetAdapterSymbolsAsync(_demo, CancellationToken.None);

        await _binance.Received(1).GetSymbolsAsync(Arg.Any<CancellationToken>());
        await _demo.Received(1).GetSymbolsAsync(Arg.Any<CancellationToken>());
        second.Should().BeSameAs(first, "the cache answers the second ask with the warmed list");
        first.Select(symbol => symbol.Normalized).Should().Equal("BTC/USDT", "ETH/BTC");
        demo.Select(symbol => symbol.Normalized).Should().Equal("SOL/USDC");
    }

    [Fact]
    public async Task InitializeAsync_WarmsEveryRegisteredExchange_InOneFetchEach()
    {
        // The warm-up covers the whole roster in parallel — every later
        // consumer (the palette, the heatmap, the screener, the
        // autocomplete) answers from these entries, so the venue is hit
        // exactly once per exchange per session (T-228's whole point).
        var provider = CreateProvider(CreateService());

        provider.InitializeAsync().IsCompleted.Should().BeTrue("startup must not await the network");

        // Join the warm-up: each ask shares the in-flight fetch or answers
        // from the cache already warmed.
        var binance = await provider.GetAdapterSymbolsAsync(_binance, CancellationToken.None);
        var demo = await provider.GetAdapterSymbolsAsync(_demo, CancellationToken.None);

        await _binance.Received(1).GetSymbolsAsync(Arg.Any<CancellationToken>());
        await _demo.Received(1).GetSymbolsAsync(Arg.Any<CancellationToken>());
        binance.Should().Contain(symbol => symbol.Normalized == "BTC/USDT");
        demo.Should().Contain(symbol => symbol.Normalized == "SOL/USDC");
    }

    [Fact]
    public async Task TheWarmUp_MissingCredentials_StayQuietOnTheDebugLevel()
    {
        // The T-227 canon: the adapter's no-key standing absence rides the
        // debug lane, never a warning family — a configuration state the
        // user chose must not paint the launch log red at every start.
        var logger = new SpyLogger();
        var provider = new SymbolListProvider(CreateService(), logger);
        _binance.GetSymbolsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IReadOnlyList<Symbol>>(
                new AdapterCredentialsMissingException("binance", "binanceApiKey")));

        var initialization = provider.InitializeAsync();
        initialization.IsCompleted.Should().BeTrue("startup must not await the network");

        // The ask after the warm-up re-fetches (a failed list is never
        // cached) and reports the standing absence to its own asker.
        var ask = () => provider.GetAdapterSymbolsAsync(_binance, CancellationToken.None);
        await ask.Should().ThrowAsync<AdapterCredentialsMissingException>();

        logger.Entries.Should().NotContain(
            entry => entry.Level == LogLevel.Warning,
            "the no-key standing absence never paints the log red");
        logger.Entries.Should().Contain(
            entry => entry.Level == LogLevel.Debug && entry.Message.Contains("runs without credentials"),
            "the quiet-absence lane is the warm-up's only trace");
    }

    /// <summary>
    /// A capturing logger: the warm-up's level families (the debug quiet
    /// absence, the warning for a real fault) stay assertable.
    /// </summary>
    private sealed class SpyLogger : ILogger<SymbolListProvider>
    {
        /// <summary>Every logged entry so far, in arrival order.</summary>
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        /// <inheritdoc/>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc/>
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc/>
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
