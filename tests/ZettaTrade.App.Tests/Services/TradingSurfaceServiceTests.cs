namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
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
using ZettaTrade.Infrastructure.Exchange;
using ZettaTrade.Infrastructure.Fees;

/// <summary>
/// Tests for the delegating traded surface (T-127): the shipped default
/// parks on the paper adapter; the venue lane requires the operator's three
/// gates TOGETHER (the settings toggle, the toolbar's Bybit pick and the
/// endpoint record's testnet flag — any one missing keeps paper); a live
/// toggle or picker change re-runs the decision on the stream (no restart);
/// and the surface's identity is always the delegated adapter's own (never
/// a third id).
/// </summary>
public sealed class TradingSurfaceServiceTests
{
    #region Fixtures

    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly Subject<SettingsChange> _changes = new();
    private readonly IMarketDataAdapter _paperMarket = Substitute.For<IMarketDataAdapter>();
    private readonly IMarketDataAdapter _venueMarket = Substitute.For<IMarketDataAdapter>();

    /// <summary>The per-fact surface bundle: the delegating surface and its picker facade.</summary>
    private sealed record SurfaceBundle(
        TradingSurfaceService Surface,
        ActiveExchangeService Facade) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await Surface.DisposeAsync();
    }

    /// <summary>
    /// The shipped default: mock toggle, Hence picker on the demo exchange and the
    /// registry's production records — nothing armed.
    /// </summary>
    private async Task<SurfaceBundle> CreateSurfaceAsync(
        bool toggleArmed,
        string pickerExchangeId,
        BybitEndpoints? bybitOverride)
    {
        _settings
            .GetAsync<bool>(TradingSurfaceService.SettingsSection, TradingSurfaceService.TestnetTradingSettingKey, Arg.Any<bool>())
            .Returns(toggleArmed);
        _settings.Changes.Returns(_changes);

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

        // The toolbar pick: the facade serves the named exchange from here on.
        if (!string.Equals(pickerExchangeId, "crypto-demo", StringComparison.Ordinal))
        {
            await facade.SwitchToAsync(pickerExchangeId);
        }

        if (bybitOverride is not null)
        {
            _settings
                .GetAsync<BybitEndpoints?>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<BybitEndpoints?>())
                .Returns(bybitOverride);
            await registry.InitializeAsync(CancellationToken.None);
        }

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

        return new SurfaceBundle(surface, facade);
    }

    private static BybitEndpoints TestnetRecord() =>
        new("https://api.example/testnet", "wss://stream.example/testnet", true);

    #endregion

    #region Tests: the shipped default and the three gates

    [Fact]
    public async Task TheShippedDefault_ParksOnThePaperLane()
    {
        await using var bundle = await CreateSurfaceAsync(false, "crypto-demo", null);

        bundle.Surface.Current.Id.Should().Be(PaperTradingAdapter.ExchangeId);
        bundle.Surface.Id.Should().Be(PaperTradingAdapter.ExchangeId);
    }

    /// <summary>The venue surface requires all three gates together; any one missing keeps paper.</summary>
    [Theory]
    [InlineData(true, "bybit", true, true)]
    [InlineData(false, "bybit", true, false)]
    [InlineData(true, "crypto-demo", true, false)]
    [InlineData(true, "bybit", false, false)]
    public async Task TheVenueLane_RequiresAllThreeGates(
        bool toggleArmed,
        string pickerExchangeId,
        bool testnetFlag,
        bool expectVenue)
    {
        await using var bundle = await CreateSurfaceAsync(
            toggleArmed,
            pickerExchangeId,
            testnetFlag ? TestnetRecord() : null);

        bundle.Surface.Current.Id.Should().Be(expectVenue ? BybitTradingAdapter.ExchangeId : PaperTradingAdapter.ExchangeId);
    }

    [Fact]
    public async Task GetSymbolsAsync_RoutesThroughTheStandingSurface()
    {
        await using var bundle = await CreateSurfaceAsync(true, "bybit", TestnetRecord());

        await bundle.Surface.GetSymbolsAsync();

        _ = _venueMarket.Received(1).GetSymbolsAsync(Arg.Any<CancellationToken>());

        // The paper source stays untouched while the venue surface stands.
        _ = _paperMarket.DidNotReceive().GetSymbolsAsync(Arg.Any<CancellationToken>());
    }

    #endregion

    #region Tests: the live flip streams

    [Fact]
    public async Task ALiveToggleFlip_ReRunsTheDecisionWithoutARestart()
    {
        await using var bundle = await CreateSurfaceAsync(false, "bybit", TestnetRecord());
        bundle.Surface.Current.Id.Should().Be(PaperTradingAdapter.ExchangeId);

        var surfaces = new List<ITradingAdapter>();
        using var tap = bundle.Surface.SurfaceChanges.Subscribe(surfaces.Add);

        // The standing value bootstraps the tap; the flips follow.
        surfaces.Select(s => s.Id).Should().Equal(PaperTradingAdapter.ExchangeId);

        // The operator arms the toggle live: the settings service ships the
        // typed value — the venue surface follows without a restart.
        _changes.OnNext(new SettingsChange(
            TradingSurfaceService.SettingsSection,
            TradingSurfaceService.TestnetTradingSettingKey,
            false,
            true));
        bundle.Surface.Current.Id.Should().Be(BybitTradingAdapter.ExchangeId);

        // The disarm flip (the operator's way back) re-arms paper.
        _changes.OnNext(new SettingsChange(
            TradingSurfaceService.SettingsSection,
            TradingSurfaceService.TestnetTradingSettingKey,
            true,
            false));
        bundle.Surface.Current.Id.Should().Be(PaperTradingAdapter.ExchangeId);

        surfaces.Select(s => s.Id).Should().Equal(
            PaperTradingAdapter.ExchangeId,
            BybitTradingAdapter.ExchangeId,
            PaperTradingAdapter.ExchangeId);
    }

    [Fact]
    public async Task ALivePickerFlip_SteersTheDecision()
    {
        await using var bundle = await CreateSurfaceAsync(true, "crypto-demo", TestnetRecord());
        bundle.Surface.Current.Id.Should().Be(PaperTradingAdapter.ExchangeId);

        // The toolbar flips to Bybit: the facade's adapter stream drives the decision.
        await bundle.Facade.SwitchToAsync("bybit");

        bundle.Surface.Current.Id.Should().Be(BybitTradingAdapter.ExchangeId);
    }

    [Fact]
    public async Task AnUnparsableTogglePayload_FailsSafeToPaper()
    {
        await using var bundle = await CreateSurfaceAsync(true, "bybit", TestnetRecord());
        bundle.Surface.Current.Id.Should().Be(BybitTradingAdapter.ExchangeId);

        // A hot-reload payload of a foreign shape (not a bool, not a JSON
        // boolean) can never arm the venue — doubt means paper.
        _changes.OnNext(new SettingsChange(
            TradingSurfaceService.SettingsSection,
            TradingSurfaceService.TestnetTradingSettingKey,
            true,
            "true-ish"));

        bundle.Surface.Current.Id.Should().Be(PaperTradingAdapter.ExchangeId);
    }

    #endregion

    #region Test fixtures

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

    #endregion
}
