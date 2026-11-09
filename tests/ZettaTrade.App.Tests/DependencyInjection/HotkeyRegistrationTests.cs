namespace ZettaTrade.App.Tests.DependencyInjection;

using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;

/// <summary>
/// The hotkey manager's composition pins (T-102): the live-map service and
/// the shared editor/cheat-sheet ViewModel register singly, typed, as
/// application-lifetime singletons — the one seam the window's key route,
/// the menu's advertised gestures, the cheat-sheet overlay and the settings
/// tab all resolve, so its two registrations must never split lifetimes.
/// The shape rides unit facts per the T-104 smoke lesson: DI shapes nobody
/// else pins belong in unit facts, not in first-run crashes.
/// </summary>
public sealed class HotkeyRegistrationTests
{
    [Fact]
    public void AddHotkeyServices_RegistersTheServiceThenTheViewModel()
    {
        var services = new ServiceCollection();

        services.AddHotkeyServices();

        var descriptors = services.Where(item => item.ServiceType is not null).ToList();
        descriptors.Should().HaveCount(2);

        descriptors[0].ServiceType.Should().Be(typeof(HotkeyService));
        descriptors[0].ImplementationType.Should().Be(typeof(HotkeyService));
        descriptors[0].Lifetime.Should().Be(ServiceLifetime.Singleton);

        descriptors[1].ServiceType.Should().Be(typeof(HotkeyViewModel));
        descriptors[1].ImplementationType.Should().Be(typeof(HotkeyViewModel));
        descriptors[1].Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void TheWalkScreen_TreatsBothRegistrationsAsPlain()
    {
        var services = new ServiceCollection();
        services.AddHotkeyServices();

        var descriptors = services.ToList();

        // Both registrations carry constructor injection only — no
        // IAsyncInitializable leg — so the startup walk skips both and the
        // hotkey map's seeding stays the service's own construction read.
        CompositionRoot.IsStartupWalkCandidate(descriptors[0]).Should().BeFalse(
            "the service has no initialization walk leg of its own");
        CompositionRoot.IsStartupWalkCandidate(descriptors[1]).Should().BeFalse(
            "the ViewModel has no initialization walk leg of its own");
    }

    [Fact]
    public void TheHotkeyRegistration_StayedAlone()
    {
        // The extension registers the service and the ViewModel and nothing
        // else: no scope leaks, no stray aliases.
        var services = new ServiceCollection();

        services.AddHotkeyServices();

        services.Should().HaveCount(2);
        services.Select(item => item.ServiceType).Should().OnlyHaveUniqueItems();
    }
}
