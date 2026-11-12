namespace ZettaTrade.App.Tests.DependencyInjection;

using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;

/// <summary>
/// The screener panel's composition pins (T-195): the feed and its panel
/// ViewModel ride factory singletons in that order (the feed resolves the
/// facade and the log lane inside the factory, the panel resolves the feed
/// and the chart), and both stay startup-walk candidates — the factory
/// shape is inspected dynamically, so the walk owns the feed's warm-up and
/// the panel's wire-up. The full graph activation stays the launch smoke's
/// territory; the unit fact pins the registration shape nobody else does.
/// </summary>
public sealed class ScreenerRegistrationTests
{
    [Fact]
    public void AddScreenerServices_RegistersTheFeedThenItsPanel_AsFactorySingletons()
    {
        var services = new ServiceCollection();

        services.AddScreenerServices();

        var descriptors = services.Where(item => item.ServiceType is not null).ToList();
        descriptors.Should().HaveCount(2);

        descriptors[0].ServiceType.Should().Be(typeof(ScreenerFeedService));
        descriptors[0].ImplementationFactory.Should().NotBeNull("the feed's warm-up rides the resolved facade, never an orphan member");
        descriptors[0].Lifetime.Should().Be(ServiceLifetime.Singleton);

        descriptors[1].ServiceType.Should().Be(typeof(ScreenerViewModel));
        descriptors[1].ImplementationFactory.Should().NotBeNull("the panel resolves the feed and the chart inside the factory");
        descriptors[1].Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void BothRegistrations_AreStartupWalkCandidates()
    {
        var services = new ServiceCollection();
        services.AddScreenerServices();

        var descriptors = services.ToList();

        // The feed implements IAsyncInitializable (the walk owns the warm-up
        // universe fetch); the ViewModel rides a factory registration, whose
        // activation shape the walk must inspect dynamically - the panels
        // wire on startup like every workspace panel before them.
        CompositionRoot.IsStartupWalkCandidate(descriptors[0]).Should().BeTrue(
            "the feed's initialization leg belongs to the startup walk, never a blocking constructor");
        CompositionRoot.IsStartupWalkCandidate(descriptors[1]).Should().BeTrue(
            "the panel wires at startup like the rest of the workspace panels");
    }

    [Fact]
    public void TheScreenerRegistration_StayedAlone()
    {
        // The extension registers the feed and the panel and nothing else:
        // no scope leaks, no stray aliases.
        var services = new ServiceCollection();

        services.AddScreenerServices();

        services.Should().HaveCount(2);
        services.Select(item => item.ServiceType).Should().OnlyHaveUniqueItems();
    }
}
