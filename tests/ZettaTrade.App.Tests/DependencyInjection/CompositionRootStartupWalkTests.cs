namespace ZettaTrade.App.Tests.DependencyInjection;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// Pins the startup-walk screen in the composition root so the typed
/// registration assignability check (shipped in T-015 after a naive
/// pattern-match check silently skipped every typed registration and the
/// endpoint registry never initialized) cannot quietly regress to the
/// `type is IAsyncInitializable` form. The helper is the exact guard the
/// walk applies to each registration descriptor before resolving anything.
/// </summary>
public sealed class CompositionRootStartupWalkTests
{
    [Fact]
    public void IsStartupWalkCandidate_TypedInitializableRegistration_IsCandidate()
    {
        // Arrange
        var descriptor = ServiceDescriptor.Singleton(typeof(IServiceMarker), typeof(InitializableService));

        // Act, assert
        CompositionRoot.IsStartupWalkCandidate(descriptor).Should().BeTrue();
    }

    [Fact]
    public void IsStartupWalkCandidate_TypedNonInitializableRegistration_IsNotCandidate()
    {
        // Arrange
        var descriptor = ServiceDescriptor.Singleton(typeof(IServiceMarker), typeof(NonInitializableService));

        // Act, assert
        CompositionRoot.IsStartupWalkCandidate(descriptor).Should().BeFalse();
    }

    [Fact]
    public void IsStartupWalkCandidate_FactoryRegistration_IsCandidateForDynamicInspection()
    {
        // Arrange: factories carry no implementation hint; the walk screens
        // them statically only on the service type (open generics are out)
        // and inspects the resolved instance dynamically.
        var descriptor = ServiceDescriptor.Singleton(
            typeof(IServiceMarker),
            (IServiceProvider sp) => new NonInitializableService());

        // Act, assert
        CompositionRoot.IsStartupWalkCandidate(descriptor).Should().BeTrue();
    }

    [Fact]
    public void IsStartupWalkCandidate_InstanceNonInitializableRegistration_IsNotCandidate()
    {
        // Arrange
        var descriptor = ServiceDescriptor.Singleton(typeof(IServiceMarker), new NonInitializableService());

        // Act, assert
        CompositionRoot.IsStartupWalkCandidate(descriptor).Should().BeFalse();
    }

    [Fact]
    public void IsStartupWalkCandidate_InstanceInitializableRegistration_IsCandidate()
    {
        // Arrange
        var descriptor = ServiceDescriptor.Singleton(typeof(IServiceMarker), new InitializableService());

        // Act, assert
        CompositionRoot.IsStartupWalkCandidate(descriptor).Should().BeTrue();
    }

    [Fact]
    public void IsStartupWalkCandidate_OpenGenericRegistration_IsNotCandidate()
    {
        // Arrange: open generics cannot be resolved directly by the walk.
        var descriptor = ServiceDescriptor.Singleton(typeof(IList<>), typeof(List<>));

        // Act, assert
        CompositionRoot.IsStartupWalkCandidate(descriptor).Should().BeFalse();
    }

    [Fact]
    public void IsStartupWalkCandidate_TypedInitializableModules_RemainCandidates()
    {
        // Arrange: audit regressions — the real typed IAsyncInitializable
        // modules must stay walk candidates (T-015 exists because they once
        // silently did not).
        var registry = ServiceDescriptor.Singleton(
            typeof(ExchangeEndpointRegistry),
            typeof(ExchangeEndpointRegistry));
        var symbols = ServiceDescriptor.Singleton(
            typeof(SymbolListProvider),
            typeof(SymbolListProvider));

        // Act, assert
        CompositionRoot.IsStartupWalkCandidate(registry).Should().BeTrue();
        CompositionRoot.IsStartupWalkCandidate(symbols).Should().BeTrue();
    }

    private interface IServiceMarker
    {
    }

    private sealed class NonInitializableService : IServiceMarker
    {
    }

    private sealed class InitializableService : IServiceMarker, IAsyncInitializable
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
