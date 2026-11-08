namespace ZettaTrade.App.Tests.Services;

using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// Tests for the trading-mode service (T-123): the mapping matrix resolves
/// the shipped paper surface to the paper lane and classifies the venue
/// surfaces by their T-015 endpoint identity (the configured record's
/// testnet flag); an identity outside the registry snapshot is an error,
/// never a substituted lane; and the flip gate requires the human
/// confirmation exactly for a flip into live.
/// </summary>
public sealed class TradingModeServiceTests
{
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly ITradingAdapter _traded = Substitute.For<ITradingAdapter>();
    private readonly IConfirmationService _confirmation = Substitute.For<IConfirmationService>();

    /// <summary>
    /// Build the registry the fact needs: no override rides the shipped
    /// production defaults unless the fact configured one, and the override
    /// (when present) is read through the async initialization walk the
    /// startup uses.
    /// </summary>
    private async Task<ExchangeEndpointRegistry> CreateRegistryAsync(
        BinanceEndpoints? binanceOverride = null,
        BybitEndpoints? bybitOverride = null)
    {
        if (binanceOverride is not null)
        {
            _settings
                .GetAsync<BinanceEndpoints?>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<BinanceEndpoints?>())
                .Returns(binanceOverride);
        }

        if (bybitOverride is not null)
        {
            _settings
                .GetAsync<BybitEndpoints?>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<BybitEndpoints?>())
                .Returns(bybitOverride);
        }

        var registry = new ExchangeEndpointRegistry(
            _settings,
            NullLogger<ExchangeEndpointRegistry>.Instance);
        await registry.InitializeAsync(CancellationToken.None);
        return registry;
    }

    private TradingModeService CreateService(ExchangeEndpointRegistry registry) =>
        new(
            _traded,
            registry,
            _confirmation,
            NullLogger<TradingModeService>.Instance);

    #region Tests: the lane mapping matrix

    /// <summary>The shipped paper surface parks on the paper lane, whatever the registry holds.</summary>
    [Fact]
    public async Task ThePaperSurface_ParksOnThePaperLane()
    {
        // Arrange: the wrapped identity is the paper one; the registry
        // override is a full testnet record — the paper lane outranks it
        // (the simulator trades inside this process, never at the venue).
        _traded.Id.Returns(PaperTradingAdapter.ExchangeId);
        var registry = await CreateRegistryAsync(new BinanceEndpoints("https://testnet.example", "wss://stream.example", true));

        var service = CreateService(registry);

        service.CurrentLane.Should().Be(TradingLane.Paper);
        service.Lane.Should().BeAssignableTo<IObservable<TradingLane>>();
    }

    /// <summary>A venue surface on the shipped production record resolves live.</summary>
    [Fact]
    public async Task AVenueSurface_OnTheProductionRecord_ResolvesLive()
    {
        _traded.Id.Returns(ExchangeEndpointRegistry.BinanceKey);
        var registry = await CreateRegistryAsync();

        var service = CreateService(registry);

        service.CurrentLane.Should().Be(TradingLane.Live);
    }

    /// <summary>A venue surface whose configured record carries the testnet flag resolves testnet.</summary>
    [Fact]
    public async Task AVenueSurface_OnATestnetRecord_ResolvesTestnet()
    {
        _traded.Id.Returns(ExchangeEndpointRegistry.BybitKey);
        var registry = await CreateRegistryAsync(
            bybitOverride: new BybitEndpoints("https://api.example/testnet", "wss://stream.example/testnet", true));

        var service = CreateService(registry);

        service.CurrentLane.Should().Be(TradingLane.Testnet);
    }

    /// <summary>
    /// An identity outside the registry snapshot is an error — a fabricated
    /// lane is exactly the state the chip exists to prevent.
    /// </summary>
    [Fact]
    public async Task AnIdentityOutsideTheRegistry_IsAnError()
    {
        _traded.Id.Returns("MysteryVenue");
        var registry = await CreateRegistryAsync();

        var resolve = () => TradingModeService.ResolveLane(_traded, registry);

        resolve.Should().ThrowExactly<InvalidOperationException>()
            .Which.Message.Should().Contain("MysteryVenue");
    }

    #endregion Tests: the lane mapping matrix

    #region Tests: the lane stream's standing delivery

    /// <summary>
    /// The lane stream delivers the standing value on subscribe: any chip
    /// (window) attaching later bootstraps from the same resolution — no
    /// request/response dance, no missed first value.
    /// </summary>
    [Fact]
    public async Task ASecondSubscriber_BootstrapsFromTheStandingLane()
    {
        _traded.Id.Returns(PaperTradingAdapter.ExchangeId);
        var registry = await CreateRegistryAsync();
        var service = CreateService(registry);

        TradingLane? delivered = null;
        using var subscription = service.Lane.Subscribe(lane => delivered = lane);

        delivered.Should().Be(TradingLane.Paper, "the stream stands down its current lane on subscribe");
    }

    #endregion Tests: the lane stream's standing delivery

    #region Tests: the flip confirmation gate

    /// <summary>A flip into testnet needs no prompt — the gate answers open.</summary>
    [Fact]
    public async Task AFlipIntoTestnet_NeedsNoConfirmation()
    {
        _traded.Id.Returns(PaperTradingAdapter.ExchangeId);
        var registry = await CreateRegistryAsync();
        var service = CreateService(registry);

        var allowed = await service.ConfirmLaneFlipAsync(TradingLane.Testnet, CancellationToken.None);

        allowed.Should().BeTrue();
        await _confirmation.DidNotReceiveWithAnyArgs().RequestConfirmationAsync(
            Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>());
    }

    /// <summary>The go-live flip is the confirmed event: an accepted prompt lets it through.</summary>
    [Fact]
    public async Task TheGoLiveFlip_RidesTheConfirmationPrompt_WhenAccepted()
    {
        _traded.Id.Returns(PaperTradingAdapter.ExchangeId);
        _confirmation
            .RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var registry = await CreateRegistryAsync();
        var service = CreateService(registry);

        var allowed = await service.ConfirmLaneFlipAsync(TradingLane.Live, CancellationToken.None);

        allowed.Should().BeTrue();
        await _confirmation.Received(1).RequestConfirmationAsync(
            Arg.Is<ConfirmationRequest>(request => request.MessageKey == TradingModeService.ConfirmGoLiveKey),
            Arg.Any<CancellationToken>());
    }

    /// <summary>Declining the prompt aborts the flip: the gate reports the refusal, never a silent proceed.</summary>
    [Fact]
    public async Task TheGoLiveFlip_AbortsCleanly_WhenDeclined()
    {
        _traded.Id.Returns(PaperTradingAdapter.ExchangeId);
        _confirmation
            .RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var registry = await CreateRegistryAsync();
        var service = CreateService(registry);

        var allowed = await service.ConfirmLaneFlipAsync(TradingLane.Live, CancellationToken.None);

        allowed.Should().BeFalse();
        await _confirmation.Received(1).RequestConfirmationAsync(
            Arg.Is<ConfirmationRequest>(request => request.MessageKey == TradingModeService.ConfirmGoLiveKey),
            Arg.Any<CancellationToken>());
    }

    #endregion Tests: the flip confirmation gate
}
