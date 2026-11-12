namespace ZettaTrade.App.Tests.DependencyInjection;

using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.Services;

/// <summary>
/// The kill switch's composition pins (T-188): the venue set (the explicit
/// paper-then-testnet sweep order behind its factory) and the sweep service
/// register singly, typed, as application-lifetime singletons — the one
/// seam the menu route and the hotkey route both resolve, so the panic
/// action's surface never splits lifetimes. The shape rides unit facts per
/// the T-104 smoke lesson: DI shapes nobody else pins belong in unit facts,
/// not in first-run crashes.
/// </summary>
public sealed class KillSwitchRegistrationTests
{
    [Fact]
    public void AddKillSwitchServices_RegistersTheVenuesThenTheService()
    {
        var services = new ServiceCollection();

        services.AddKillSwitchServices();

        var descriptors = services.Where(item => item.ServiceType is not null).ToList();
        descriptors.Should().HaveCount(2);

        descriptors[0].ServiceType.Should().Be(typeof(KillSwitchVenues));
        descriptors[0].ImplementationFactory.Should().NotBeNull(
            "the venue set carries the explicit paper-then-testnet sweep order");
        descriptors[0].ImplementationType.Should().BeNull();
        descriptors[0].Lifetime.Should().Be(ServiceLifetime.Singleton);

        descriptors[1].ServiceType.Should().Be(typeof(KillSwitchService));
        descriptors[1].ImplementationType.Should().Be(typeof(KillSwitchService));
        descriptors[1].Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void TheWalkScreen_TreatsBothRegistrationsAsPlain()
    {
        var services = new ServiceCollection();
        services.AddKillSwitchServices();

        var descriptors = services.ToList();

        // The typed sweep service carries constructor injection only — no
        // IAsyncInitializable leg — so the walk skips it. The venue set rides
        // a factory registration, which the walk inspects dynamically (it
        // resolves and checks the instance); the non-initializable venue set
        // is skipped there — the sweep stays the stateless action-time
        // reader it must be.
        CompositionRoot.IsStartupWalkCandidate(descriptors[0]).Should().BeTrue(
            "factory registrations are inspected dynamically by the walk");
        CompositionRoot.IsStartupWalkCandidate(descriptors[1]).Should().BeFalse(
            "the sweep service has no initialization walk leg of its own");
    }

    [Fact]
    public void TheKillSwitchRegistration_StayedAlone()
    {
        // The extension registers the venue set and the sweep service and
        // nothing else: no scope leaks, no stray aliases.
        var services = new ServiceCollection();

        services.AddKillSwitchServices();

        services.Should().HaveCount(2);
        services.Select(item => item.ServiceType).Should().OnlyHaveUniqueItems();
    }
}
