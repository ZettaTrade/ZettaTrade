namespace ZettaTrade.App.Tests.DependencyInjection;

using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The detached-panel composition pins (T-103): the window-host map and the
/// coordinator register as two singletons (the hosts as an instance — its
/// factories are the trivial chrome shells, the coordinator behind its
/// interface — the one authority of the detached state), and the
/// registration stays alone.
/// </summary>
public sealed class DetachedPanelsRegistrationTests
{
    [Fact]
    public void AddDetachedPanelsServices_RegistersTheHostsThenTheCoordinator_AsSingletons()
    {
        var services = new ServiceCollection();

        services.AddDetachedPanelsServices();

        var descriptors = services.Where(item => item.ServiceType is not null).ToList();
        descriptors.Should().HaveCount(2);

        descriptors[0].ServiceType.Should().Be(typeof(DetachedPanelHosts));
        descriptors[0].ImplementationInstance.Should().NotBeNull(
            "the host map carries the per-panel window factories as an instance");
        descriptors[0].Lifetime.Should().Be(ServiceLifetime.Singleton);

        descriptors[1].ServiceType.Should().Be(typeof(IDetachedPanelCoordinator));
        descriptors[1].ImplementationType.Should().Be(typeof(DetachedPanelCoordinator));
        descriptors[1].Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void TheHostMap_ListsEveryDetachablePanelExactlyOnce()
    {
        var services = new ServiceCollection();
        services.AddDetachedPanelsServices();

        var hosts = services.BuildServiceProvider().GetRequiredService<DetachedPanelHosts>();

        hosts.IsComplete.Should().BeTrue(
            "every panel the View menu can toggle needs its window factory");
    }

    [Fact]
    public void TheCoordinator_ResolvesAgainstItsOwnDependencies()
    {
        var services = new ServiceCollection();
        services.AddDetachedPanelsServices();
        services.AddSingleton(Substitute.For<ISettingsService>());
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IDetachedPanelCoordinator>()
            .Should().NotBeNull("the runtime arms its persistence pipeline at construction");
    }

    [Fact]
    public void TheDetachedPanelsRegistration_StayedAlone()
    {
        var services = new ServiceCollection();

        services.AddDetachedPanelsServices();

        services.Should().HaveCount(2);
        services.Select(item => item.ServiceType).Should().OnlyHaveUniqueItems();
    }
}
