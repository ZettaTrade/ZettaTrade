namespace ZettaTrade.App.Tests.DependencyInjection;

using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;

/// <summary>
/// The watchlist's composition pins (T-120): the feed and the panel
/// ViewModel register as factory-driven application-lifetime singletons in
/// apply order (feed first, rows second); the feed rides
/// <c>IAsyncInitializable</c> so the startup walk knows it (the feed's own
/// composition is constructed lazily — a full graph activation needs the
/// chart ViewModel's real dependencies, so the activation itself rides the
/// pre-merge launch smoke per the standing directive).
/// </summary>
public sealed class WatchlistRegistrationTests
{
    [Fact]
    public void AddWatchlistServices_RegistersTheFeedThenTheRows_AsFactorySingletons()
    {
        var services = new ServiceCollection();

        services.AddWatchlistServices();

        var descriptors = services.Where(item => item.ServiceType is not null).ToList();
        descriptors.Should().HaveCount(2);

        descriptors[0].ServiceType.Should().Be(typeof(WatchlistFeedService));
        descriptors[0].ImplementationFactory.Should().NotBeNull("the feed wires ChartViewModel.SymbolSelections");
        descriptors[0].Lifetime.Should().Be(ServiceLifetime.Singleton);

        descriptors[1].ServiceType.Should().Be(typeof(WatchlistViewModel));
        descriptors[1].ImplementationFactory.Should().NotBeNull();
        descriptors[1].Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void TheFeed_IsAStartupWalkCandidate_AndTheViewModelRidesTheRuntimeGuard()
    {
        var services = new ServiceCollection();
        services.AddWatchlistServices();

        var descriptors = services.ToList();

        CompositionRoot.IsStartupWalkCandidate(descriptors[0]).Should().BeTrue(
            "the feed implements IAsyncInitializable: the walk owns its startup leg");
        CompositionRoot.IsStartupWalkCandidate(descriptors[1]).Should().BeTrue(
            "a factory registration has no static type to screen — the candidates stay conservative; " +
            "the walk's instance guard skips the rows ViewModel (it implements no IAsyncInitializable)");
    }

    [Fact]
    public void TheWatchlistRegistration_StayedAlone()
    {
        // The extension registers the watchlist's own two services and
        // nothing else: no scope leaks, no stray aliases.
        var services = new ServiceCollection();

        services.AddWatchlistServices();

        services.Should().HaveCount(2);
        services.Select(item => item.ServiceType).Should().OnlyHaveUniqueItems();
    }
}
