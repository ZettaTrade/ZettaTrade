namespace ZettaTrade.Infrastructure.Tests.Licensing;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.Infrastructure.Licensing;

/// <summary>
/// The activation-service matrix (T-132): blank inputs refuse before any
/// validation, every refused status stores nothing, and only a valid
/// verdict stores the trimmed key — refused-not-defaulted on every path.
/// </summary>
public sealed class LicenseActivationServiceTests
{
    [Fact]
    public async Task BlankPluginId_RefusesBeforeValidation()
    {
        var (activation, validator, store) = CreateValidStage();

        var status = await activation.ActivateAsync("   ", "zt1.a.b");

        status.Should().Be(LicenseValidationStatus.Malformed);
        await validator.DidNotReceiveWithAnyArgs().ValidateAsync(default!, default!);
        await store.DidNotReceiveWithAnyArgs().SetKeyAsync(default!, default!);
    }

    [Fact]
    public async Task BlankKey_RefusesBeforeValidation()
    {
        var (activation, validator, store) = CreateValidStage();

        var status = await activation.ActivateAsync("chart-lens", "  \r\n");

        status.Should().Be(LicenseValidationStatus.Malformed);
        await validator.DidNotReceiveWithAnyArgs().ValidateAsync(default!, default!);
        await store.DidNotReceiveWithAnyArgs().SetKeyAsync(default!, default!);
    }

    [Fact]
    public async Task RefusedVerdict_StoresNothing_AndCarriesTheStatus()
    {
        var (activation, _, store) = CreateRefusing(LicenseValidationStatus.Tampered);

        var status = await activation.ActivateAsync("chart-lens", "zt1.bad.bad");

        status.Should().Be(LicenseValidationStatus.Tampered);
        await store.DidNotReceiveWithAnyArgs().SetKeyAsync(default!, default!);
    }

    [Fact]
    public async Task ValidVerdict_StoresTheTrimmedKey()
    {
        var (activation, validator, store) = CreateValidStage();

        var status = await activation.ActivateAsync("chart-lens", "  zt1.good.good \n");

        status.Should().Be(LicenseValidationStatus.Valid);
        await validator.Received(1).ValidateAsync("chart-lens", "zt1.good.good");
        await store.Received(1).SetKeyAsync("chart-lens", "zt1.good.good");
    }

    #region Stubs

    private static (ILicenseActivationService Activation, ILicenseValidator Validator, ILicenseStore Store) CreateValidStage()
    {
        var validator = Substitute.For<ILicenseValidator>();
        validator.ValidateAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(LicenseValidationStatus.Valid);
        var store = Substitute.For<ILicenseStore>();
        var activation = new LicenseActivationService(
            validator,
            store,
            NullLogger<LicenseActivationService>.Instance);
        return (activation, validator, store);
    }

    private static (ILicenseActivationService Activation, ILicenseValidator Validator, ILicenseStore Store) CreateRefusing(
        LicenseValidationStatus refusing)
    {
        var validator = Substitute.For<ILicenseValidator>();
        validator.ValidateAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(refusing);
        var store = Substitute.For<ILicenseStore>();
        var activation = new LicenseActivationService(
            validator,
            store,
            NullLogger<LicenseActivationService>.Instance);
        return (activation, validator, store);
    }

    #endregion
}
