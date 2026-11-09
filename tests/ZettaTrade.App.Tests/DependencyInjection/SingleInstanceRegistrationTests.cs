namespace ZettaTrade.App.Tests.DependencyInjection;

using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.Services;
using ZettaTrade.Infrastructure.SingleInstance;

/// <summary>
/// The single-instance handoff's composition pins (T-104): the apply worker
/// rides a typed singleton and the dispatch host rides a factory singleton
/// whose lambda resolves the walk-ready apply worker per delivery (never an
/// orphan instance captured mid-walk). The full graph activation stays the
/// launch smoke's territory; the unit fact pins the registration shape
/// nobody else does - the smoke lesson that pinned DI activation shapes
/// belong in unit facts, not in first-run crashes.
/// </summary>
public sealed class SingleInstanceRegistrationTests
{
    [Fact]
    public void AddSingleInstanceServices_RegistersTheApplyWorkerThenItsHost()
    {
        var services = new ServiceCollection();

        services.AddSingleInstanceServices();

        var descriptors = services.Where(item => item.ServiceType is not null).ToList();
        descriptors.Should().HaveCount(2);

        descriptors[0].ServiceType.Should().Be(typeof(HandoffApplyService));
        descriptors[0].ImplementationType.Should().Be(typeof(HandoffApplyService));
        descriptors[0].Lifetime.Should().Be(ServiceLifetime.Singleton);

        descriptors[1].ServiceType.Should().Be(typeof(SingleInstanceHost));
        descriptors[1].ImplementationFactory.Should().NotBeNull(
            "the host's apply route resolves the walk-ready apply worker inside the factory, never a captured instance");
        descriptors[1].Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void TheWalkScreen_TreatsTheTypedWorkerAsPlain_AndTheHostFactoryAsDynamic()
    {
        var services = new ServiceCollection();
        services.AddSingleInstanceServices();

        var descriptors = services.ToList();

        // The apply worker carries no initialization leg (constructor
        // injection only), so the startup walk skips it; the host's factory
        // registration carries no implementation hint, so the walk inspects
        // its activation dynamically - the dispatch pump's Start stays the
        // app bootstrap's own call, after the composition walk.
        CompositionRoot.IsStartupWalkCandidate(descriptors[0]).Should().BeFalse(
            "the apply worker has no IAsyncInitializable leg - nothing for the walk to own");
        CompositionRoot.IsStartupWalkCandidate(descriptors[1]).Should().BeTrue(
            "a factory registration is inspected dynamically by the walk, never screened out by a stale hint");
    }

    [Fact]
    public void TheSingleInstanceRegistration_StayedAlone()
    {
        // The extension registers the apply worker and the host and nothing
        // else: no scope leaks, no stray aliases.
        var services = new ServiceCollection();

        services.AddSingleInstanceServices();

        services.Should().HaveCount(2);
        services.Select(item => item.ServiceType).Should().OnlyHaveUniqueItems();
    }
}
