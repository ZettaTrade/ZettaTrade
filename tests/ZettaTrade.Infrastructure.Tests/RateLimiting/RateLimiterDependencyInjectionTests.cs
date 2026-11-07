namespace ZettaTrade.Infrastructure.Tests.RateLimiting;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Livery.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Exchange;
using ZettaTrade.Infrastructure.RateLimiting;

/// <summary>
/// The registration seam of the REST rate limiters (T-106): the
/// exchange-adapter chain carries one keyed singleton limiter per adapter
/// under the adapter's own HttpClient identity, composes with or without a
/// settings service (Infrastructure providers built standalone keep working),
/// tolerates the composition root's startup walk over the keyed descriptors,
/// and hands the shared <see cref="ISettingsService"/> stream to the limiter
/// it builds.
/// </summary>
public sealed class RateLimiterDependencyInjectionTests
{
    #region Fixture

    /// <summary>
    /// The composition-root startup walk, verbatim: every initializer-aligned
    /// descriptor is initialized before the first adapter resolves (the walk
    /// must tolerate keyed limiter descriptors standing beside them).
    /// </summary>
    private static async Task WalkStartupAsync(ServiceProvider provider, IServiceCollection services)
    {
        var initialized = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var descriptor in services)
        {
            var implementationType = descriptor.ImplementationType;
            if (implementationType is { } typed &&
                !typeof(IAsyncInitializable).IsAssignableFrom(typed))
            {
                continue;
            }

            var implementationInstance = descriptor.ImplementationInstance;
            if (implementationInstance is { } instance &&
                instance is not IAsyncInitializable)
            {
                continue;
            }

            if (descriptor.ServiceType.IsGenericType &&
                !descriptor.ServiceType.IsConstructedGenericType)
            {
                continue;
            }

            if (provider.GetService(descriptor.ServiceType) is not IAsyncInitializable service)
            {
                continue;
            }

            if (!initialized.Add(service))
            {
                continue;
            }

            await service.InitializeAsync();
        }
    }

    #endregion

    #region Tests: the chain

    /// <summary>
    /// The chain composes standalone: with no settings service registered,
    /// the provider builds and the keyed limiters stand on the shipped
    /// manifests alone (their construction reads the settings service
    /// optionally), each key yielding its own manifest's limiter, and the
    /// keyed descriptors never break the plain non-keyed lookups the
    /// composition root's startup walk issues.
    /// </summary>
    /// <remarks>
    /// The adapters themselves owe the registry their settings service (the
    /// pre-existing chain), so they resolve only in the settings-bearing
    /// fact; the limiter seam is what composes standalone.
    /// </remarks>
    [Fact]
    public async Task AddExchangeAdapters_WithoutSettings_KeepsTheKeyedLimitersResolvable()
    {
        var services = new ServiceCollection();
        services.AddExchangeAdapters();

        await using var provider = services.BuildServiceProvider();

        var binance = provider.GetRequiredKeyedService<IRestRateLimiter>(BinanceAdapter.HttpClientName);

        binance.Should().BeOfType<RestRateLimiter>();

        provider.GetRequiredKeyedService<IRestRateLimiter>(BybitAdapter.HttpClientName)
            .Should().NotBeSameAs(binance, "each adapter builds its limiter from its own manifest");

        provider.GetService(typeof(IRestRateLimiter))
            .Should().BeNull("a keyed-only descriptor must not break the plain GetService the startup walk issues");
    }

    /// <summary>
    /// The full registration path against a real settings stream: the
    /// composition-root startup walk completes with the keyed descriptors
    /// standing (a keyed lookup that broke the walk would break the App) and
    /// the walkers' registry initializes off the shared stream.
    /// </summary>
    [Fact]
    public async Task AddExchangeAdapters_WithSettings_StartupWalkToleratesTheKeyedDescriptors()
    {
        var services = new ServiceCollection();
        var settings = new TestSettingsService();
        services.AddSingleton<ISettingsService>(settings);
        services.AddSingleton(NullLogger<ExchangeEndpointRegistry>.Instance);
        services.AddSingleton(NullLogger<BinanceAdapter>.Instance);
        services.AddSingleton(NullLogger<BybitAdapter>.Instance);
        services.AddSingleton(NullLogger<ReconnectSupervisor>.Instance);
        services.AddExchangeAdapters();

        await using var provider = services.BuildServiceProvider();

        await WalkStartupAsync(provider, services);

        provider.GetRequiredService<BinanceAdapter>().Should().NotBeNull();
        provider.GetRequiredService<BybitAdapter>().Should().NotBeNull();
    }

    /// <summary>
    /// One keyed singleton per adapter under the strict key: resolving the
    /// same key twice yields one shared instance (the adapter's build) and
    /// the other adapter's key yields the other manifest's limiter.
    /// </summary>
    [Fact]
    public void AddRestRateLimiters_RegistersOneKeyedSingletonPerAdapter()
    {
        var services = new ServiceCollection();
        services.AddRestRateLimiters();

        using var provider = services.BuildServiceProvider();

        var binance = provider.GetRequiredKeyedService<IRestRateLimiter>(BinanceAdapter.HttpClientName);

        binance.Should().BeOfType<RestRateLimiter>("the wired limiter is the real per-adapter bucket limiter");
        provider.GetRequiredKeyedService<IRestRateLimiter>(BinanceAdapter.HttpClientName)
            .Should().BeSameAs(binance, "the adapter shares its own resolved limiter");
        provider.GetRequiredKeyedService<IRestRateLimiter>(BybitAdapter.HttpClientName)
            .Should().NotBeSameAs(binance, "each adapter builds its limiter from its own manifest");
    }

    /// <summary>
    /// The DI-built limiter reads the shared settings stream: hydrated once
    /// from the persisted keys (the stored zero share mutes the background
    /// lane live) and then followed reactively (the stored share restored
    /// un-mutes without an acquire in between).
    /// </summary>
    [Fact]
    public async Task AddRestRateLimiters_WithSettings_HandsTheSharedStreamToTheWiredLimiter()
    {
        var services = new ServiceCollection();
        var settings = new TestSettingsService();
        services.AddSingleton<ISettingsService>(settings);
        services.AddRestRateLimiters();

        await using var provider = services.BuildServiceProvider();
        var limiter = provider.GetRequiredKeyedService<IRestRateLimiter>(BinanceAdapter.HttpClientName);

        await settings.SetAsync(RestRateLimiterOptions.SettingsSection, RestRateLimiter.BackgroundShareKey, 0.0);

        await FluentActions.Awaiting(() => limiter.AcquireAsync(RestLane.Background, "/api/v3/klines", 1))
            .Should().ThrowAsync<RestRateLimitWaitExpiredException>("the hydrated zero share mutes the background lane");

        await settings.SetAsync(RestRateLimiterOptions.SettingsSection, RestRateLimiter.BackgroundShareKey, 0.35);

        await limiter.AcquireAsync(RestLane.User, "/api/v3/klines", 3400);

        settings.Reads.Should().Be(3, "the DI-built limiter hydrates through the same one-pass discipline");
    }

    #endregion
}
