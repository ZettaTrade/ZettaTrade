namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Net.Http;
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
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Exchange;
using ZettaTrade.Infrastructure.Fees;

/// <summary>
/// Tests for the trading-mode service's surface-flip extension (T-127):
/// the optional delegating-surface parameter keeps every earlier constructor
/// call compiling (absent → the standing once-at-construction resolution
/// the T-123 facts pin), and a LIVE lane flip re-resolves through the same
/// static identity rules — the status-bar chip follows the surface without
/// a restart.
/// </summary>
public sealed class TradingModeServiceSurfaceTests
{
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly Subject<SettingsChange> _changes = new();
    private readonly IMarketDataAdapter _paperMarket = Substitute.For<IMarketDataAdapter>();
    private readonly IMarketDataAdapter _venueMarket = Substitute.For<IMarketDataAdapter>();
    private readonly IConfirmationService _confirmation = Substitute.For<IConfirmationService>();

    /// <summary>The registry fixture: the Bybit record carries the testnet flag.</summary>
    private async Task<ExchangeEndpointRegistry> CreateRegistryAsync(bool testnetFlag)
    {
        _settings
            .GetAsync<BybitEndpoints?>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<BybitEndpoints?>())
            .Returns(new BybitEndpoints("https://api.example/testnet", "wss://stream.example/testnet", testnetFlag));
        var registry = new ExchangeEndpointRegistry(
            _settings,
            NullLogger<ExchangeEndpointRegistry>.Instance);
        await registry.InitializeAsync(CancellationToken.None);
        return registry;
    }

    [Fact]
    public async Task WithoutTheSurfaceParameter_TheStandingLaneHolds()
    {
        var registry = await CreateRegistryAsync(true);
        var tradedAdapter = Substitute.For<ITradingAdapter>();
        tradedAdapter.Id.Returns(PaperTradingAdapter.ExchangeId);

        var service = new TradingModeService(
            tradedAdapter,
            registry,
            _confirmation,
            NullLogger<TradingModeService>.Instance);

        service.CurrentLane.Should().Be(TradingLane.Paper);
    }

    /// <summary>
    /// The bounded wait for the flip fact: the whole chain is synchronous
    /// (one OnNext rides subjects to the lane stream), so the budget only
    /// bounds a regression — a broken flip fails the fact instead of hanging
    /// the suite.
    /// </summary>
    private static readonly TimeSpan LaneFlipWait = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task ALiveSurfaceFlip_ReResolvesTheLaneWithoutARestart()
    {
        await using var surface = await CreateSurfaceAsync(toggleArmed: false);
        var registry = await CreateRegistryAsync(true);

        // The lane collector runs across the whole fact: the standing value
        // bootstraps on subscribe, the flip arrives without any restart.
        var service = new TradingModeService(
            surface,
            registry,
            _confirmation,
            NullLogger<TradingModeService>.Instance,
            surface);
        var lanesTask = service
            .Lane
            .Take(2)
            .ToList()
            .ToTask(TaskCreationOptions.RunContinuationsAsynchronously)
            .WaitAsync(LaneFlipWait);

        // The operator arms the toggle live (typed payload emission): the
        // surface flips to the venue lane, the mode service mirrors it.
        _changes.OnNext(new SettingsChange(
            TradingSurfaceService.SettingsSection,
            TradingSurfaceService.TestnetTradingSettingKey,
            false,
            true));

        var lanes = await lanesTask;
        lanes.Should().Equal(TradingLane.Paper, TradingLane.Testnet);
    }

    /// <summary>
    /// The real delegating surface, hand-built: the shipped defaults on the
    /// paper lane — the toolbar already stands on the Bybit market data and
    /// the registry's Bybit record carries the testnet flag, but the toggle
    /// is OFF, so the surface parks on paper and the flip fact arms it live
    /// mid-run.
    /// </summary>
    private async Task<TradingSurfaceService> CreateSurfaceAsync(bool toggleArmed)
    {
        _settings
            .GetAsync<bool>(TradingSurfaceService.SettingsSection, TradingSurfaceService.TestnetTradingSettingKey, Arg.Any<bool>())
            .Returns(toggleArmed);
        _settings.Changes.Returns(_changes);

        // The registry's Bybit record: the surface's own decision input —
        // the same record the mode fact's registry carries.
        _settings
            .GetAsync<BybitEndpoints?>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<BybitEndpoints?>())
            .Returns(new BybitEndpoints("https://api.example/testnet", "wss://stream.example/testnet", true));

        _paperMarket.Id.Returns("crypto-demo");
        _venueMarket.Id.Returns(BybitTradingAdapter.ExchangeId);

        var bybitVenue = new BybitTradingAdapter(
            NullLogger<BybitTradingAdapter>.Instance,
            new StubHttpClientFactory(),
            _venueMarket,
            new StubSecretStore(),
            new BybitTradingEndpoints("https://stub.local", "ws://127.0.0.1:9/stream"));

        var facade = new ActiveExchangeService(
            [_paperMarket, _venueMarket],
            _settings,
            NullLogger<ActiveExchangeService>.Instance);

        var registry = new ExchangeEndpointRegistry(_settings, NullLogger<ExchangeEndpointRegistry>.Instance);
        await registry.InitializeAsync(CancellationToken.None);

        // The toolbar pick: the venue's market data stands before the surface
        // boots — the toggle stays the only un-armed gate.
        await facade.SwitchToAsync(BybitTradingAdapter.ExchangeId);

        var paper = new PaperTradingAdapter(
            _paperMarket,
            Substitute.For<IFeeScheduleProvider>(),
            NullLogger<PaperTradingAdapter>.Instance);

        var surface = new TradingSurfaceService(
            facade,
            paper,
            bybitVenue,
            _settings,
            registry,
            NullLogger<TradingSurfaceService>.Instance);
        await surface.InitializeAsync();
        return surface;
    }

    /// <summary>The stub REST transport: the venue adapter's client rides nowhere in these facts.</summary>
    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new HttpClientHandler());
    }

    /// <summary>The stub secret store: the testnet key pair the adapter fetches per operation.</summary>
    private sealed class StubSecretStore : ISecretStore
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => key switch
        {
            BybitTradingAdapter.ApiKeySecretKey => Task.FromResult<string?>("sa-key"),
            BybitTradingAdapter.ApiSecretSecretKey => Task.FromResult<string?>("sa-secret"),
            _ => Task.FromResult<string?>(null),
        };

        public Task SetAsync(string key, string value, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RemoveAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListKeysAsync(string prefix = "", CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
    }
}
