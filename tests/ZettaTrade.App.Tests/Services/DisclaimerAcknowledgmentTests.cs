namespace ZettaTrade.App.Tests.Services;

using System.Threading.Tasks;
using FluentAssertions;
using NSubstitute;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The disclaimer acknowledgment rules: the version relation (the shipped
/// version or later covers, none does not), the fresh-store read hitting the
/// exact section/key/default, and the acknowledge write landing with the
/// shipped constants.
/// </summary>
public sealed class DisclaimerAcknowledgmentTests
{
    [Fact]
    public void CoversCurrent_NoAcknowledgedVersion_False() =>
        DisclaimerAcknowledgment.CoversCurrent(UiSettingsKeys.DefaultDisclaimerVersion)
            .Should().BeFalse("a fresh install has acknowledged no disclaimer wording");

    [Fact]
    public void CoversCurrent_ShippedVersion_True() =>
        DisclaimerAcknowledgment.CoversCurrent(UiSettingsKeys.DisclaimerCurrentVersion)
            .Should().BeTrue("what was acknowledged is exactly the shipped wording");

    [Fact]
    public void CoversCurrent_LaterVersion_True() =>
        DisclaimerAcknowledgment.CoversCurrent(UiSettingsKeys.DisclaimerCurrentVersion + 1)
            .Should().BeTrue("a newer wording's acknowledge covers the older shipped text too");

    [Fact]
    public async Task ReadAcknowledgedVersionAsync_ReadsThePersistedVersion()
    {
        var settings = Substitute.For<ISettingsService>();
        var persistedVersion = UiSettingsKeys.DisclaimerCurrentVersion + 3;
        settings
            .GetAsync(UiSettingsKeys.Section, UiSettingsKeys.DisclaimerVersion, UiSettingsKeys.DefaultDisclaimerVersion)
            .Returns(Task.FromResult(persistedVersion));

        var acknowledged = await DisclaimerAcknowledgment.ReadAcknowledgedVersionAsync(settings);

        acknowledged.Should().Be(persistedVersion);
        await settings.Received(1).GetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.DisclaimerVersion,
            UiSettingsKeys.DefaultDisclaimerVersion);
    }

    [Fact]
    public async Task AcknowledgeAsync_PersistsTheShippedVersion()
    {
        var settings = Substitute.For<ISettingsService>();

        await DisclaimerAcknowledgment.AcknowledgeAsync(settings);

        await settings.Received(1).SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.DisclaimerVersion,
            UiSettingsKeys.DisclaimerCurrentVersion);
    }
}
