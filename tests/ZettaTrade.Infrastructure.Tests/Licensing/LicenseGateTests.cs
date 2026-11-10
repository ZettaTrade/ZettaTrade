namespace ZettaTrade.Infrastructure.Tests.Licensing;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.Infrastructure.Licensing;
using ZettaTrade.Infrastructure.Tests.RateLimiting;

/// <summary>
/// The entitlement-gate matrix (T-132): a plugin outside the protected
/// list entitles without any check, a protected plugin refuses on a
/// missing or invalid stored key with the explicit status, a valid stored
/// key entitles, and a MALFORMED protected list protects everything — an
/// unparsable operator list must open no gate (refused-not-defaulted).
/// </summary>
public sealed class LicenseGateTests
{
    [Fact]
    public async Task UnlistedPlugin_EntitlesWithoutAnyCheck()
    {
        var validator = Substitute.For<ILicenseValidator>();
        var store = Substitute.For<ILicenseStore>();
        var gate = new LicenseGate(
            Settings("""["chart-lens"]"""),
            validator,
            store,
            NullLogger<LicenseGate>.Instance);

        var verdict = await gate.EvaluateAsync("some-open-plugin");

        verdict.Entitled.Should().BeTrue();
        verdict.Status.Should().Be(LicenseValidationStatus.Valid);
        await store.DidNotReceiveWithAnyArgs().GetKeyAsync(default!);
        await validator.DidNotReceiveWithAnyArgs().ValidateAsync(default!, default!);
    }

    [Fact]
    public async Task ProtectedPluginWithoutStoredKey_RefusesMissing()
    {
        var gate = new LicenseGate(
            Settings("""["chart-lens"]"""),
            Substitute.For<ILicenseValidator>(),
            Substitute.For<ILicenseStore>(),
            NullLogger<LicenseGate>.Instance);

        var verdict = await gate.EvaluateAsync("chart-lens");

        verdict.Entitled.Should().BeFalse();
        verdict.Status.Should().Be(LicenseValidationStatus.Missing);
    }

    [Fact]
    public async Task ProtectedPluginWithValidKey_Entitles()
    {
        var validator = Substitute.For<ILicenseValidator>();
        validator.ValidateAsync("chart-lens", "zt1.good.good").Returns(
            LicenseValidationStatus.Valid);
        var store = Substitute.For<ILicenseStore>();
        store.GetKeyAsync("chart-lens").Returns("zt1.good.good");
        var gate = new LicenseGate(
            Settings("""["chart-lens"]"""),
            validator,
            store,
            NullLogger<LicenseGate>.Instance);

        var verdict = await gate.EvaluateAsync("chart-lens");

        verdict.Entitled.Should().BeTrue();
        verdict.Status.Should().Be(LicenseValidationStatus.Valid);
    }

    [Fact]
    public async Task ProtectedPluginWithTamperedKey_RefusesWithTheStatus()
    {
        var validator = Substitute.For<ILicenseValidator>();
        validator.ValidateAsync("chart-lens", "zt1.baddy.baddy").Returns(
            LicenseValidationStatus.Tampered);
        var store = Substitute.For<ILicenseStore>();
        store.GetKeyAsync("chart-lens").Returns("zt1.baddy.baddy");
        var gate = new LicenseGate(
            Settings("""["chart-lens"]"""),
            validator,
            store,
            NullLogger<LicenseGate>.Instance);

        var verdict = await gate.EvaluateAsync("chart-lens");

        verdict.Entitled.Should().BeFalse();
        verdict.Status.Should().Be(LicenseValidationStatus.Tampered);
    }

    [Fact]
    public async Task ProtectedPluginWithoutConfiguredVerificationKey_RefusesUnconfigured()
    {
        var validator = Substitute.For<ILicenseValidator>();
        validator.ValidateAsync("chart-lens", "zt1.whatever.whatever").Returns(
            LicenseValidationStatus.Unconfigured);
        var store = Substitute.For<ILicenseStore>();
        store.GetKeyAsync("chart-lens").Returns("zt1.whatever.whatever");
        var gate = new LicenseGate(
            Settings("""["chart-lens"]"""),
            validator,
            store,
            NullLogger<LicenseGate>.Instance);

        var verdict = await gate.EvaluateAsync("chart-lens");

        verdict.Entitled.Should().BeFalse();
        verdict.Status.Should().Be(LicenseValidationStatus.Unconfigured);
    }

    [Fact]
    public async Task MalformedProtectedList_ProtectsEverything()
    {
        // Fresh substitutes: any fact touching the shared pair would tint
        // the no-audit assertions of the others (the NSubstitute trap).
        var gate = new LicenseGate(
            Settings("{ this is not a json array"),
            Substitute.For<ILicenseValidator>(),
            Substitute.For<ILicenseStore>(),
            NullLogger<LicenseGate>.Instance);

        var verdict = await gate.EvaluateAsync("some-open-plugin");

        verdict.Entitled.Should().BeFalse();
        verdict.Status.Should().Be(LicenseValidationStatus.Missing);
    }

    #region Stubs

    private static TestSettingsService Settings(string protectedList)
    {
        var settings = new TestSettingsService();
        settings.SetAsync(
            LicensingSettingsKeys.Section,
            LicensingSettingsKeys.ProtectedPlugins,
            protectedList).GetAwaiter().GetResult();
        return settings;
    }

    #endregion
}
