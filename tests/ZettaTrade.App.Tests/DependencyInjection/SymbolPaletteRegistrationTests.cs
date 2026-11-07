namespace ZettaTrade.App.Tests.DependencyInjection;

using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;

/// <summary>
/// The symbol palette's composition pins (T-095): the palette source and the
/// palette ViewModel register as application-lifetime singletons in apply
/// order (source - state first, ViewModel - the surface second), the source
/// rides <c>IAsyncInitializable</c> so the startup walk warms the instrument
/// snapshot before any window opens, and the ViewModel's typed registration
/// is a plain singleton (nothing to initialize there).
/// </summary>
public sealed class SymbolPaletteRegistrationTests
{
    [Fact]
    public void AddPaletteServices_RegistersTheSourceThenTheViewModel_AsSingletons()
    {
        var services = new ServiceCollection();

        services.AddPaletteServices();

        var descriptors = services.Where(item => item.ServiceType is not null).ToList();
        descriptors.Should().HaveCount(2);

        descriptors[0].ServiceType.Should().Be(typeof(SymbolPaletteSource));
        descriptors[0].ImplementationType.Should().Be(typeof(SymbolPaletteSource));
        descriptors[0].Lifetime.Should().Be(ServiceLifetime.Singleton);

        descriptors[1].ServiceType.Should().Be(typeof(SymbolPaletteViewModel));
        descriptors[1].ImplementationType.Should().Be(typeof(SymbolPaletteViewModel));
        descriptors[1].Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void ThePaletteSource_IsAStartupWalkCandidate_AndTheViewModelIsNot()
    {
        var services = new ServiceCollection();
        services.AddPaletteServices();

        var descriptors = services.ToList();

        CompositionRoot.IsStartupWalkCandidate(descriptors[0]).Should().BeTrue(
            "the source implements IAsyncInitializable: the warm-up fetch rides the walk");
        CompositionRoot.IsStartupWalkCandidate(descriptors[1]).Should().BeFalse(
            "the ViewModel carries nothing the walk must await");
    }

    [Fact]
    public void ThePaletteRegistration_StayedAlone()
    {
        // The extension registers the palette's own two services and nothing
        // else: no scope leaks, no stray aliases.
        var services = new ServiceCollection();

        services.AddPaletteServices();

        services.Should().HaveCount(2);
        services.Select(item => item.ServiceType).Should().HaveCount(2);
    }
}
