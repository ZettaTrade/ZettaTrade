namespace ZettaTrade.App.Tests.DependencyInjection;

using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.Services;

/// <summary>
/// The daily loss governor's composition pins (T-189): the state service
/// registers typed and aliased behind <see cref="IDailyLossLimit"/> as
/// singletons — the one seam the order gate and the ticket's banner both
/// resolve, so the block's surface never splits lifetimes. The shape rides
/// unit facts per the T-104 smoke lesson: DI shapes nobody else pins belong
/// in unit facts, not in first-run crashes.
/// </summary>
public sealed class RiskLimitRegistrationTests
{
    [Fact]
    public void AddRiskLimitServices_RegistersTheServiceTypedThenAliased()
    {
        var services = new ServiceCollection();

        services.AddRiskLimitServices();

        var descriptors = services.Where(item => item.ServiceType is not null).ToList();
        descriptors.Should().HaveCount(2);

        descriptors[0].ServiceType.Should().Be(typeof(DailyLossLimitService));
        descriptors[0].ImplementationType.Should().Be(typeof(DailyLossLimitService));
        descriptors[0].ImplementationFactory.Should().BeNull();
        descriptors[0].Lifetime.Should().Be(ServiceLifetime.Singleton);

        descriptors[1].ServiceType.Should().Be(typeof(IDailyLossLimit));
        descriptors[1].ImplementationFactory.Should().NotBeNull(
            "the alias resolves the same instance the gate and the ticket share");
        descriptors[1].ImplementationType.Should().BeNull();
        descriptors[1].Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void TheWalkScreen_SkipsTheTypedServiceAndInspectsTheAliasDynamically()
    {
        var services = new ServiceCollection();
        services.AddRiskLimitServices();

        var descriptors = services.ToList();

        // The typed state service carries constructor injection only — no
        // IAsyncInitializable leg — so the walk skips it. The interface alias
        // rides a factory registration, which the walk inspects dynamically
        // (it resolves and checks the instance); the non-initializable state
        // service is skipped there — the governor stays the decision-time
        // reader it must be.
        CompositionRoot.IsStartupWalkCandidate(descriptors[0]).Should().BeFalse(
            "the state service has no initialization walk leg of its own");
        CompositionRoot.IsStartupWalkCandidate(descriptors[1]).Should().BeTrue(
            "factory registrations are inspected dynamically by the walk");
    }

    [Fact]
    public void TheRiskLimitRegistration_StayedAlone()
    {
        // The extension registers the state service and its alias and
        // nothing else: no scope leaks, no stray extra registrations.
        var services = new ServiceCollection();

        services.AddRiskLimitServices();

        services.Should().HaveCount(2);
        services.Select(item => item.ServiceType).Should().OnlyHaveUniqueItems();
    }
}
