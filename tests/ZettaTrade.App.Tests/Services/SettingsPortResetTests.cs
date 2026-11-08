namespace ZettaTrade.App.Tests.Services;

using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NSubstitute;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Configuration;

/// <summary>
/// The settings port's factory reset (T-100) over a real store: the
/// destructive act rides the typed human confirmation — a decline renders
/// false with nothing written, an acceptance stamps every registry-owned key
/// back at its listed default (or removes those whose default IS absence)
/// and never touches a key outside the registry (the secret-store lane and
/// foreign tunings survive).
/// </summary>
public sealed class SettingsPortResetTests
{
    /// <summary>
    /// An accepted reset returns every registry-owned key to its fresh-install
    /// posture: a stamped default value for keys the shipped code always
    /// stores, removal for keys whose default is the fresh-install absence —
    /// while the secret the store holds and a foreign tuning key stay.
    /// </summary>
    [Fact]
    public async Task AnAcceptedReset_RestoresTheFreshInstallPosture_AndNeverTouchesTheSecrets()
    {
        using var kit = new SettingsPortKit();
        await SeedTunedStateAsync(kit.Settings);
        await kit.Settings.SetAsync("Secrets", "ApiKey", "hunter2");
        await kit.Settings.SetAsync(UiSettingsKeys.Section, "ThirdPartyWidth", 42);
        var confirmations = ConfirmingSubstitute(confirmed: true);

        var ran = await kit.Port.ResetToDefaultsAsync(confirmations);

        ran.Should().BeTrue();
        await confirmations.Received(1).RequestConfirmationAsync(
            Arg.Is<ConfirmationRequest>(
                request => request.MessageKey == SettingsPortService.ResetConfirmMessageKey),
            Arg.Any<CancellationToken>());

        // The stamped defaults: the shipped values the fresh install keeps.
        (await kit.Settings.GetAsync<bool>(
                UiSettingsKeys.Section,
                UiSettingsKeys.ChartUse12HourClock,
                UiSettingsKeys.DefaultUse12HourClock))
            .Should().BeFalse("the reset stamps the shipped 24-hour clock");
        (await kit.Settings.GetAsync<bool>(
                NotificationSettingsKeys.Section,
                NotificationSettingsKeys.DoNotDisturb,
                NotificationSettingsKeys.DefaultDoNotDisturb))
            .Should().BeFalse("the reset hands out the open posture");
        (await kit.Settings.GetAsync<int>(
                UiSettingsKeys.Section,
                UiSettingsKeys.DisclaimerVersion,
                99))
            .Should().Be(0, "the reset stamps the unacknowledged-disclaimer default");

        // The absence-natural defaults: removal (a fresh install stores them not).
        (await kit.Settings.GetAsync<string>(UiSettingsKeys.Section, UiSettingsKeys.Theme))
            .Should().BeNull("the theme's fresh-install default IS the absence");
        (await kit.Settings.GetAsync<string>(
                UiSettingsKeys.WorkspaceSection,
                ActiveExchangeService.ExchangeIdSettingKey))
            .Should().BeNull("the exchange choice's fresh-install default IS the absence");

        // Nothing outside the registry moves — the secrets lane first of all.
        (await kit.Settings.GetAsync<string>("Secrets", "ApiKey"))
            .Should().Be("hunter2", "the reset never touches a secret");
        (await kit.Settings.GetAsync<int>(UiSettingsKeys.Section, "ThirdPartyWidth"))
            .Should().Be(42, "the reset stamps registry-owned keys only");
    }

    /// <summary>
    /// A declined reset commands the confirmation once and writes nothing:
    /// the exported posture is byte-identical before and after the declined
    /// gate closed.
    /// </summary>
    [Fact]
    public async Task ADeclinedReset_WritesNothing()
    {
        using var kit = new SettingsPortKit();
        await SeedTunedStateAsync(kit.Settings);
        var before = await kit.Port.ExportAsync();
        var confirmations = ConfirmingSubstitute(confirmed: false);

        var ran = await kit.Port.ResetToDefaultsAsync(confirmations);

        ran.Should().BeFalse();
        (await kit.Port.ExportAsync())
            .Should()
            .Be(before, "a declined reset never moves a byte");
        (await kit.Settings.GetAsync<string>(UiSettingsKeys.Section, UiSettingsKeys.Theme))
            .Should().Be("trading-pro", "the declined gate leaves the tuned choice standing");
    }

    /// <summary>
    /// Resetting an already-fresh store changes nothing: every registry entry
    /// already is the default (or already absent), so no mutation publishes
    /// and the export stays identical.
    /// </summary>
    [Fact]
    public async Task ResettingAnAlreadyFreshStore_IsANoOp()
    {
        using var kit = new SettingsPortKit();
        var before = await kit.Port.ExportAsync();
        var confirmations = ConfirmingSubstitute(confirmed: true);

        var ran = await kit.Port.ResetToDefaultsAsync(confirmations);

        ran.Should().BeTrue();
        (await kit.Port.ExportAsync())
            .Should()
            .Be(before, "a fresh store resets into itself");
    }

    /// <summary>The confirmation substitute the reset gates itself behind.</summary>
    private static IConfirmationService ConfirmingSubstitute(bool confirmed)
    {
        var confirmations = Substitute.For<IConfirmationService>();
        confirmations
            .RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(confirmed);
        return confirmations;
    }

    /// <summary>The tuned posture the reset legs seed (a sampled registry subset is enough).</summary>
    private static async Task SeedTunedStateAsync(SettingsService settings)
    {
        await settings.SetAsync(UiSettingsKeys.Section, UiSettingsKeys.Theme, "trading-pro");
        await settings.SetAsync(UiSettingsKeys.Section, UiSettingsKeys.Language, "ru");
        await settings.SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartUse12HourClock, true);
        await settings.SetAsync(
            UiSettingsKeys.WorkspaceSection,
            ActiveExchangeService.ExchangeIdSettingKey,
            "demo");
        await settings.SetAsync(UiSettingsKeys.Section, UiSettingsKeys.DisclaimerVersion, 3);
        await settings.SetAsync(NotificationSettingsKeys.Section, NotificationSettingsKeys.DoNotDisturb, true);
    }
}
