namespace ZettaTrade.App.Tests.DependencyInjection;

using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.Services;
using ZettaTrade.Infrastructure.PnL;

/// <summary>
/// The chart footprint feed's composition pins (T-122): the feed's
/// positions stream rides the PnL engine's OWN state feed (the portfolio
/// panel's stream) as a factory singleton registered BEFORE the feed — the
/// feed's activation resolves the real engine stream, never an uninjected
/// <c>IObservable</c> member (the pre-merge launch smoke caught exactly
/// that crash: a feed without its stream could not activate, and no unit
/// fact pins DI activation shape, so this registration pin does). The
/// feed itself rides <c>IAsyncInitializable</c> so the startup walk knows
/// it; the full graph activation stays the launch smoke's territory.
/// </summary>
public sealed class ChartFootprintRegistrationTests
{
    [Fact]
    public void AddChartFootprintServices_RegistersTheStreamThenTheFeed_AsFactorySingletons()
    {
        var services = new ServiceCollection();

        services.AddChartFootprintServices();

        var descriptors = services.Where(item => item.ServiceType is not null).ToList();
        descriptors.Should().HaveCount(2);

        descriptors[0].ServiceType.Should().Be(typeof(IObservable<PnlSnapshot>), "the positions stream is explicit, not an orphan member the activation invents");
        descriptors[0].ImplementationFactory.Should().NotBeNull("the stream rides the PnL feed's standing snapshots");
        descriptors[0].Lifetime.Should().Be(ServiceLifetime.Singleton);

        descriptors[1].ServiceType.Should().Be(typeof(ChartFootprintService));
        descriptors[1].Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void TheFeed_IsAStartupWalkCandidate()
    {
        var services = new ServiceCollection();
        services.AddChartFootprintServices();

        var descriptors = services.ToList();

        CompositionRoot.IsStartupWalkCandidate(descriptors[0]).Should().BeTrue(
            "a factory registration has no static type to screen - the candidates stay conservative");
        CompositionRoot.IsStartupWalkCandidate(descriptors[1]).Should().BeTrue(
            "the feed implements IAsyncInitializable: the walk owns its startup leg");
    }

    [Fact]
    public void TheFootprintRegistration_StayedAlone()
    {
        // The extension registers the feed's stream and the feed and
        // nothing else: no scope leaks, no stray aliases.
        var services = new ServiceCollection();

        services.AddChartFootprintServices();

        services.Should().HaveCount(2);
        services.Select(item => item.ServiceType).Should().OnlyHaveUniqueItems();
    }
}
