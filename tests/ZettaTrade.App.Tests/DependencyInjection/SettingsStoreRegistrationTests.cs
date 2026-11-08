namespace ZettaTrade.App.Tests.DependencyInjection;

using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Configuration;

/// <summary>
/// The settings store's composition pins (T-137): ONE instance answers both
/// the interface and the concrete type (the T-100 settings port depends on
/// the concrete's batch/snapshot members by design), and the port activates
/// over the same graph shell the root composes — the user-reported startup
/// crash (the concrete store unresolvable, the process dying before the main
/// window) stays pinned as impossible.
/// </summary>
public sealed class SettingsStoreRegistrationTests
{
    [Fact]
    public void AddSettingsStoreServices_RegistersTheConcreteAndTheForwardedInterface()
    {
        var services = new ServiceCollection();

        services.AddSettingsStoreServices();

        services.Should().HaveCount(2, "the store extension registers the concrete and the forwarded interface, nothing else");
        var descriptors = services.ToList();

        descriptors[0].ServiceType.Should().Be(typeof(SettingsService));
        descriptors[0].ImplementationType.Should().BeNull();
        descriptors[0].ImplementationFactory.Should().NotBeNull("the store rides a factory: the resolver's shipped settings path plus the DI logging plumbing");
        descriptors[0].Lifetime.Should().Be(ServiceLifetime.Singleton);

        descriptors[1].ServiceType.Should().Be(typeof(ISettingsService));
        descriptors[1].ImplementationType.Should().BeNull();
        descriptors[1].ImplementationFactory.Should().NotBeNull("the interface registration forwards the ONE concrete instance");
        descriptors[1].Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void ThePortService_ActivatesAgainstTheStoreGraph_SharingTheOneStoreInstance()
    {
        // The exact shell the root composes around the port: logging
        // plumbing, the store extension, the port extension.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSettingsStoreServices();
        services.AddSettingsPortServices();

        using var provider = services.BuildServiceProvider();

        var concrete = provider.GetRequiredService<SettingsService>();
        var interfaceStore = provider.GetRequiredService<ISettingsService>();

        concrete.Should().BeSameAs(interfaceStore, "two identities, one store object");
        provider.GetRequiredService<SettingsPortService>().Should().NotBeNull(
            "the user-reported crash was exactly this resolution failing");
    }
}
