namespace ZettaTrade.App.Tests.DependencyInjection;

using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;

/// <summary>
/// The watchlist's composition pins (T-120, the catalog added in T-196):
/// the catalog, the feed and the panel ViewModel register as
/// factory-driven application-lifetime singletons in apply order (catalog
/// first, feed second, rows third); the feed rides
/// <c>IAsyncInitializable</c> so the startup walk knows it (the feed's own
/// composition is constructed lazily — a full graph activation needs the
/// chart ViewModel's real dependencies, so the activation itself rides the
/// pre-merge launch smoke per the standing directive).
/// </summary>
public sealed class WatchlistRegistrationTests
{
    [Fact]
    public void AddWatchlistServices_RegistersTheCatalogTheFeedThenTheRows_AsFactorySingletons()
    {
        var services = new ServiceCollection();

        services.AddWatchlistServices();

        var descriptors = services.Where(item => item.ServiceType is not null).ToList();
        descriptors.Should().HaveCount(3);

        descriptors[0].ServiceType.Should().Be(typeof(WatchlistCatalogService));
        descriptors[0].ImplementationFactory.Should().NotBeNull("the catalog wires the settings store");
        descriptors[0].Lifetime.Should().Be(ServiceLifetime.Singleton);

        descriptors[1].ServiceType.Should().Be(typeof(WatchlistFeedService));
        descriptors[1].ImplementationFactory.Should().NotBeNull("the feed wires ChartViewModel.SymbolSelections and the catalog");
        descriptors[1].Lifetime.Should().Be(ServiceLifetime.Singleton);

        descriptors[2].ServiceType.Should().Be(typeof(WatchlistViewModel));
        descriptors[2].ImplementationFactory.Should().NotBeNull();
        descriptors[2].Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void TheFeed_IsAStartupWalkCandidate_AndTheCatalogAndRowsRideTheRuntimeGuard()
    {
        var services = new ServiceCollection();
        services.AddWatchlistServices();

        var descriptors = services.ToList();

        CompositionRoot.IsStartupWalkCandidate(descriptors[0]).Should().BeTrue(
            "a factory registration has no static type to screen — the candidates stay conservative; " +
            "the walk's instance guard skips the catalog (it implements no IAsyncInitializable)");
        CompositionRoot.IsStartupWalkCandidate(descriptors[1]).Should().BeTrue(
            "the feed implements IAsyncInitializable: the walk owns its startup leg");
        CompositionRoot.IsStartupWalkCandidate(descriptors[2]).Should().BeTrue(
            "a factory registration has no static type to screen — the candidates stay conservative; " +
            "the walk's instance guard skips the rows ViewModel (it implements no IAsyncInitializable)");
    }

    [Fact]
    public void TheWatchlistRegistration_StayedAlone()
    {
        // The extension registers the watchlist's own three services and
        // nothing else: no scope leaks, no stray aliases.
        var services = new ServiceCollection();

        services.AddWatchlistServices();

        services.Should().HaveCount(3);
        services.Select(item => item.ServiceType).Should().OnlyHaveUniqueItems();
    }
}
