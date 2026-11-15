namespace ZettaTrade.App.Tests.DependencyInjection;

using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;

/// <summary>
/// The screener panel's composition pins (T-195, the trend leg T-239): the
/// feed, the trend computer and the panel ViewModel ride factory
/// singletons in that order (the feed resolves the facade and the log lane
/// inside the factory, the trend computer resolves the feed, the cache,
/// the facade, the fetcher and the store, the panel resolves the feed, the
/// chart, the trend computer and the localization manager), and all three
/// stay startup-walk candidates — the factory shape is inspected
/// dynamically, so the walk owns the feed's warm-up, the trend computer's
/// tuning load and the panel's wire-up. The full graph activation stays
/// the launch smoke's territory; the unit fact pins the registration shape
/// nobody else does.
/// </summary>
public sealed class ScreenerRegistrationTests
{
    [Fact]
    public void AddScreenerServices_RegistersTheFeedThenItsTrendThenItsPanel_AsFactorySingletons()
    {
        var services = new ServiceCollection();

        services.AddScreenerServices();

        var descriptors = services.Where(item => item.ServiceType is not null).ToList();
        descriptors.Should().HaveCount(3);

        descriptors[0].ServiceType.Should().Be(typeof(ScreenerFeedService));
        descriptors[0].ImplementationFactory.Should().NotBeNull("the feed's warm-up rides the resolved facade, never an orphan member");
        descriptors[0].Lifetime.Should().Be(ServiceLifetime.Singleton);

        descriptors[1].ServiceType.Should().Be(typeof(MtfTrendService));
        descriptors[1].ImplementationFactory.Should().NotBeNull("the trend computer resolves its dependencies inside the factory");
        descriptors[1].Lifetime.Should().Be(ServiceLifetime.Singleton);

        descriptors[2].ServiceType.Should().Be(typeof(ScreenerViewModel));
        descriptors[2].ImplementationFactory.Should().NotBeNull("the panel resolves the feed, the chart and the trend computer inside the factory");
        descriptors[2].Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void AllRegistrations_AreStartupWalkCandidates()
    {
        var services = new ServiceCollection();
        services.AddScreenerServices();

        var descriptors = services.ToList();

        // The feed implements IAsyncInitializable (the walk owns the warm-up
        // universe fetch); the trend computer implements it too (the walk
        // owns the tuning load and the page subscription); the ViewModel
        // rides a factory registration, whose activation shape the walk
        // must inspect dynamically - the panels wire on startup like every
        // workspace panel before them.
        CompositionRoot.IsStartupWalkCandidate(descriptors[0]).Should().BeTrue(
            "the feed's initialization leg belongs to the startup walk, never a blocking constructor");
        CompositionRoot.IsStartupWalkCandidate(descriptors[1]).Should().BeTrue(
            "the trend computer's tuning load and page subscription belong to the walk");
        CompositionRoot.IsStartupWalkCandidate(descriptors[2]).Should().BeTrue(
            "the panel wires at startup like the rest of the workspace panels");
    }

    [Fact]
    public void TheScreenerRegistration_StayedAlone()
    {
        // The extension registers the feed, the trend computer and the
        // panel and nothing else: no scope leaks, no stray aliases.
        var services = new ServiceCollection();

        services.AddScreenerServices();

        services.Should().HaveCount(3);
        services.Select(item => item.ServiceType).Should().OnlyHaveUniqueItems();
    }
}
