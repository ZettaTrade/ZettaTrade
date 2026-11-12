namespace ZettaTrade.App.Tests.Services;

using System.Text.Json;
using FluentAssertions;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.Services;

/// <summary>
/// The daily loss governor's port pins (T-189): the settings the operator
/// adjusts ride the settings port (T-100) — the three <c>Risk</c> keys ship
/// with their stored-shape kinds and fresh-install defaults, an unknown Risk
/// key stays unknown (an import carrying it rejects), and the reset marker's
/// default IS the fresh-install absence (its consumers' absent-key behavior
/// is the default).
/// </summary>
public sealed class PortSettingsRegistryRiskPinsTests
{
    [Fact]
    public void TheGovernorSwitch_ShipsAsASwitchDefaultingToOff()
    {
        var key = PortSettingsRegistry.TryGet(RiskSettingsKeys.Section, RiskSettingsKeys.DailyLossLimitEnabled);

        key.Should().NotBeNull("the master switch ships through the port");
        key!.Kind.Should().Be(PortValueKind.Switch);
        key.Default.Should().NotBeNull("the shipped default is the OFF posture, not absence");
        key.Default!.Value.GetBoolean().Should().Be(RiskSettingsKeys.DailyLossLimitEnabledValue);
        key.Default.Value.GetBoolean().Should().BeFalse(
            "the governor ships disabled — a day-loss block is a deliberate operator choice");
    }

    [Fact]
    public void TheGovernorThreshold_ShipsAsANumberDefaultingToTheConstant()
    {
        var key = PortSettingsRegistry.TryGet(RiskSettingsKeys.Section, RiskSettingsKeys.DailyLossLimitAmount);

        key.Should().NotBeNull("the loss threshold ships through the port");
        key!.Kind.Should().Be(PortValueKind.Number);
        key.Default.Should().NotBeNull("the shipped threshold is a concrete anchor, not absence");
        key.Default!.Value.GetDecimal().Should().Be(RiskSettingsKeys.DailyLossLimitAmountValue);
        key.Default.Value.GetDecimal().Should().Be(1000m);
    }

    [Fact]
    public void TheResetMarker_ShipsAsTextDefaultingToAbsence()
    {
        var key = PortSettingsRegistry.TryGet(RiskSettingsKeys.Section, RiskSettingsKeys.DailyLossResetAt);

        key.Should().NotBeNull("the reset marker ships through the port");
        key!.Kind.Should().Be(PortValueKind.Text);
        key.Default.Should().BeNull(
            "absence IS the default — the window then spans the whole local day, and a reset writes the first stored value");
    }

    [Fact]
    public void AnUnknownRiskKey_StaysUnknown()
    {
        PortSettingsRegistry.TryGet(RiskSettingsKeys.Section, "DailyLossNoSuchKey")
            .Should().BeNull("an import carrying an unknown Risk key must reject, never partially land");
    }
}
