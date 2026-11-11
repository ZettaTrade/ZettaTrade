namespace ZettaTrade.App.Tests.DependencyInjection;

using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.Services;
using ZettaTrade.Infrastructure.Licensing;

/// <summary>
/// The licensing module's registration pins (T-171): the EULA surface
/// answers its INTERFACE. The T-132 factory registered the concrete
/// type only — a factory <c>AddSingleton(sp => ...)</c> without the
/// generic service type — so the activation ViewModel's
/// <see cref="ILicenseEulaService"/> contract was unresolvable and the
/// license activation dialog never opened (the user-reported live ERR,
/// the T-137 shape). Pinned shut from both sides: the registration
/// descriptor and the graph activation.
/// </summary>
public sealed class LicenseRegistrationTests
{
    [Fact]
    public void AddLicensingServices_RegistersTheEulaSurfaceUnderItsInterface()
    {
        var services = new ServiceCollection();

        services.AddLicensingServices();

        var eula = services.Single(d => d.ServiceType == typeof(ILicenseEulaService));
        eula.ImplementationFactory.Should().NotBeNull(
            "the EULA surface rides a factory: the settings service, the plugins directory and the DI logging plumbing");
        eula.ImplementationType.Should().BeNull("the instance is composed by the factory, not constructed bare");
        eula.Lifetime.Should().Be(ServiceLifetime.Singleton);

        services.Should().NotContain(d => d.ServiceType == typeof(LicenseEulaService),
            "the concrete type registered bare is the exact T-132 defect shape: the contract the consumer asks for never answers");
    }

    [Fact]
    public void TheEulaSurface_ActivatesAgainstTheLicensingGraph()
    {
        // The graph shell the root composes around licensing: the logging
        // plumbing, the settings store (the EULA factory consumes it) and
        // the module's own extension.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSettingsStoreServices();
        services.AddLicensingServices();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ILicenseEulaService>().Should().NotBeNull(
            "the user-reported crash was exactly this resolution failing");
    }
}
