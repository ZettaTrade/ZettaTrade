namespace ZettaTrade.App.Tests.Services;

using FluentAssertions;
using NSubstitute;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The pre-submit confirmation policy's decision matrix (T-078): live asks
/// by default and releases only what the threshold explicitly releases;
/// market orders always confirm in live; the paper sandbox stays ungated
/// until its own master switch says otherwise; an unassessable notional
/// keeps the gate; a stored skip-threshold change applies at the next
/// decision without a restart.
/// </summary>
public sealed class OrderConfirmPolicyTests
{
    private static ISettingsService StubSettings(
        bool liveEnabled,
        bool paperEnabled,
        decimal skipFirst,
        params decimal[] skipMore)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<bool>(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<bool>())
            .Returns(liveEnabled);
        settings.GetAsync<bool>(
                Arg.Any<string>(),
                ConfirmSettingsKeys.EnabledInPaper,
                Arg.Any<bool>())
            .Returns(paperEnabled);
        settings.GetAsync(
                Arg.Any<string>(),
                ConfirmSettingsKeys.SkipBelowNotionalUsd,
                Arg.Any<decimal>())
            .Returns(skipFirst, skipMore);
        return settings;
    }

    private static OrderConfirmPolicy CreatePolicy(
        bool liveEnabled = true,
        bool paperEnabled = false,
        decimal skipFirst = 0m,
        params decimal[] skipMore) =>
        new(StubSettings(liveEnabled, paperEnabled, skipFirst, skipMore));

    [Fact]
    public async Task ShouldConfirmAsync_LiveLimitAboveSkip_AlwaysAsks()
    {
        var policy = CreatePolicy(skipFirst: 0m);

        var asked = await policy.ShouldConfirmAsync(false, OrderType.Limit, 500m);

        asked.Should().BeTrue();
    }

    [Fact]
    public async Task ShouldConfirmAsync_LiveLimitBelowSkip_ReleasesTheGate()
    {
        var policy = CreatePolicy(skipFirst: 1_000m);

        var asked = await policy.ShouldConfirmAsync(false, OrderType.Limit, 500m);

        asked.Should().BeFalse();
    }

    [Fact]
    public async Task ShouldConfirmAsync_LiveDisabled_NonMarketOrderReleasesTheGate()
    {
        var policy = CreatePolicy(liveEnabled: false);

        var asked = await policy.ShouldConfirmAsync(false, OrderType.Limit, 50_000m);

        asked.Should().BeFalse();
    }

    [Fact]
    public async Task ShouldConfirmAsync_LiveMarketOrder_AlwaysAsksWhateverTheSettingsSay()
    {
        // The most aggressive release the settings allow (disabled + small
        // threshold) must still keep a live market order's gate: immediate
        // execution is the shape the summary always faces.
        var policy = CreatePolicy(
            liveEnabled: false,
            paperEnabled: true,
            skipFirst: 1_000_000m);

        var asked = await policy.ShouldConfirmAsync(false, OrderType.Market, 500m);

        asked.Should().BeTrue();
    }

    [Fact]
    public async Task ShouldConfirmAsync_UnknownNotional_KeepsTheGate()
    {
        var policy = CreatePolicy(liveEnabled: false, skipFirst: 1_000m);

        var asked = await policy.ShouldConfirmAsync(false, OrderType.Limit, null);

        asked.Should().BeTrue("the skip threshold cannot be assessed without a notional: ask, don't release");
    }

    [Fact]
    public async Task ShouldConfirmAsync_PaperDefault_DoesNotAsk()
    {
        var policy = CreatePolicy();

        var asked = await policy.ShouldConfirmAsync(true, OrderType.Market, 250_000m);

        asked.Should().BeFalse("the sandbox defaults to no gate, even for market orders");
    }

    [Fact]
    public async Task ShouldConfirmAsync_PaperEnabled_AboveSkip_Asks()
    {
        var policy = CreatePolicy(paperEnabled: true);

        var asked = await policy.ShouldConfirmAsync(true, OrderType.Limit, 50_000m);

        asked.Should().BeTrue();
    }

    [Fact]
    public async Task ShouldConfirmAsync_PaperEnabled_BelowSkip_ReleasesTheGate()
    {
        var policy = CreatePolicy(paperEnabled: true, skipFirst: 1_000m);

        var asked = await policy.ShouldConfirmAsync(true, OrderType.Limit, 500m);

        asked.Should().BeFalse();
    }

    [Fact]
    public async Task ShouldConfirmAsync_NegativeStoredThreshold_ClampsToNoSkip()
    {
        var policy = CreatePolicy(liveEnabled: true, skipFirst: -5m);

        var asked = await policy.ShouldConfirmAsync(false, OrderType.Limit, 1m);

        asked.Should().BeTrue("a negative threshold only ever releases orders — floored to never-skip");
    }

    [Fact]
    public async Task ShouldConfirmAsync_StoredThresholdChange_AppliesAtTheNextDecision()
    {
        // The user widens the skip threshold between the two decisions; the
        // policy re-reads the settings every time, so the NEXT decision
        // rides the fresh value — no restart (the hot-reload path rides the
        // same store memory either way).
        var policy = CreatePolicy(skipFirst: 0m, skipMore: [1_000m]);

        var askedFirst = await policy.ShouldConfirmAsync(false, OrderType.Limit, 500m);
        var askedSecond = await policy.ShouldConfirmAsync(false, OrderType.Limit, 500m);

        askedFirst.Should().BeTrue();
        askedSecond.Should().BeFalse();
    }
}
