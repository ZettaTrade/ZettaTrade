namespace ZettaTrade.Infrastructure.Tests.Exchange;

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Configuration;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// Tests for the exchange endpoint configuration seam: the registry reads
/// the settings once (missing section keeps the shipped production pair, a
/// configured override is served as-is, blank members degrade to the
/// defaults with a warning) and the registration resolves the Binance
/// adapter against the configured host — all without any network.
/// </summary>
public sealed class ExchangeEndpointRegistryTests
{
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();

    [Fact]
    public async Task InitializeAsync_RealSettingsFile_ServesTheConfiguredOverride()
    {
        // Arrange: a REAL SettingsService bound to a real temp JSON file in
        // the exact shape the app writes it — the NSub stub skipped the
        // actual deserialization, which is what the live run suspects.
        var path = Path.Combine(Path.GetTempPath(), $"t015-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path,
                "{\"Exchange\":{\"Binance\":{\"restBaseUrl\":\"https://real.tld\",\"wsUrl\":\"wss://real.tld/stream\"}}}");
            var settings = new SettingsService(path, NullLogger<SettingsService>.Instance);
            await settings.InitializeAsync();

            var registry = new ExchangeEndpointRegistry(settings, NullLogger<ExchangeEndpointRegistry>.Instance);

            // Act
            await registry.InitializeAsync();

            // Assert
            registry.Binance.RestBaseUrl.Should().Be("https://real.tld", "an end-to-end file-backed read must serve the override");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task StartupReplica_RegistryConfiguresTheResolvedAdapter()
    {
        // Arrange: a faithful replica of the app bootstrap — the settings
        // service registered by factory before AddExchangeAdapters, the
        // composition-root startup walk, and the MCP-tools registration
        // that runs after it. The walk must initialize the typed endpoint
        // registry (System.Type never pattern-matches a service interface,
        // so the guard checks static assignability) and the settings
        // service; only then does resolving ITradingAdapter construct the
        // Binance adapter and capture the configured endpoints from the
        // registry.
        var path = Path.Combine(Path.GetTempPath(), $"t015-replica-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path,
                "{\"Exchange\":{\"Binance\":{\"restBaseUrl\":\"https://replica.tld\",\"wsUrl\":\"wss://replica.tld/stream\"}}}");

            var services = new ServiceCollection();
            services.AddSingleton(NullLogger<SettingsService>.Instance);
            services.AddSingleton(NullLogger<BinanceAdapter>.Instance);
            services.AddSingleton(NullLogger<ExchangeEndpointRegistry>.Instance);
            services.AddSingleton<ISettingsService>(sp =>
                new SettingsService(path, sp.GetRequiredService<ILogger<SettingsService>>()));
            services.AddExchangeAdapters();

            await using var provider = services.BuildServiceProvider();

            // Verbatim replica of the composition-root startup walk.
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

            // Act: the RegisterAll step — resolving the trading adapter
            // constructs the whole adapter stack, market data included.
            provider.GetRequiredService<ITradingAdapter>().Should().NotBeNull();
            var adapter = (BinanceAdapter)provider.GetRequiredService<IMarketDataAdapter>();

            // Assert: the walked registry configures the adapter resolved
            // after the walk — the full ordering story in one test.
            adapter.RestBaseUrl.Should().Be(
                "https://replica.tld",
                "the walked registry must configure the adapter resolved after the walk");
            adapter.WsUrl.Should().Be("wss://replica.tld/stream");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private ExchangeEndpointRegistry CreateRegistry() =>
        new(_settings, NullLogger<ExchangeEndpointRegistry>.Instance);

    [Fact]
    public async Task InitializeAsync_WithoutConfiguredSection_KeepsProductionEndpoints()
    {
        // Arrange: no Exchange section in the settings file — the common case.
        _settings.GetAsync<BinanceEndpoints?>(Arg.Any<string>(), Arg.Any<string>())
            .Returns((BinanceEndpoints?)null);
        var registry = CreateRegistry();

        // Act
        await registry.InitializeAsync();

        // Assert
        registry.Binance.Should().Be(
            BinanceEndpoints.Production,
            "a missing section is hidden-safe: shipped defaults win silently");
    }

    [Fact]
    public async Task InitializeAsync_WithConfiguredEndpoints_ServesTheOverride()
    {
        // Arrange: a complete override (e.g. testnet or a regional mirror).
        var configured = new BinanceEndpoints(
            "https://stub.tld",
            "wss://stream.stub.tld/stream");
        _settings.GetAsync<BinanceEndpoints?>(Arg.Any<string>(), Arg.Any<string>())
            .Returns(configured);
        var registry = CreateRegistry();

        // Act
        await registry.InitializeAsync();

        // Assert
        registry.Binance.Should().Be(configured, "a complete override replaces the snapshot");
    }

    [Fact]
    public async Task InitializeAsync_WithBlankOverride_KeepsProductionEndpoints()
    {
        // Arrange: an incomplete override must not trickle a half-configured
        // host into the adapter (blank strings would break every URL).
        _settings.GetAsync<BinanceEndpoints?>(Arg.Any<string>(), Arg.Any<string>())
            .Returns(new BinanceEndpoints("", "  "));
        var registry = CreateRegistry();

        // Act
        await registry.InitializeAsync();

        // Assert
        registry.Binance.Should().Be(
            BinanceEndpoints.Production,
            "an incomplete override degrades to the shipped defaults");
    }

    [Fact]
    public async Task AdapterResolution_ServesTheEndpointsInitializedByTheRegistry()
    {
        // Arrange: the full registration path. The registry-emulating init
        // loop order matters: the app initializes the registry BEFORE the
        // first adapter resolves its endpoints — mirror exactly that here.
        _settings.GetAsync<BinanceEndpoints?>(Arg.Any<string>(), Arg.Any<string>())
            .Returns(new BinanceEndpoints(
                "https://stub.tld",
                "wss://stream.stub.tld/stream"));
        var services = new ServiceCollection();
        services.AddSingleton(_settings);
        services.AddSingleton(NullLogger<BinanceAdapter>.Instance);
        services.AddSingleton(NullLogger<ExchangeEndpointRegistry>.Instance);
        services.AddExchangeAdapters();

        // BinanceAdapter (and its base) dispose asynchronously — the built
        // provider must be torn down via DisposeAsync.
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<ExchangeEndpointRegistry>().InitializeAsync();

        // Act
        var adapter = (BinanceAdapter)provider.GetRequiredService<IMarketDataAdapter>();

        // Assert
        adapter.RestBaseUrl.Should().Be("https://stub.tld");
        adapter.WsUrl.Should().Be("wss://stream.stub.tld/stream");
    }

    [Fact]
    public void Ctor_WithoutEndpoints_FallsBackToProductionDefaults()
    {
        // Existing call sites keep compiling and get the shipped defaults.
        var adapter = new BinanceAdapter(
            NullLogger<BinanceAdapter>.Instance,
            Substitute.For<IHttpClientFactory>(),
            endpoints: null);

        adapter.RestBaseUrl.Should().Be(BinanceAdapter.DefaultRestBaseUrl);
        adapter.WsUrl.Should().Be(BinanceAdapter.DefaultWsUrl);
    }

    [Theory]
    [InlineData("", "wss://stream.stub.tld/stream")]
    [InlineData("https://stub.tld", "")]
    [InlineData("  ", "  ")]
    public void Ctor_WithBlankConfiguredEndpoints_ThrowsImmediately(string restBaseUrl, string wsUrl)
    {
        // The adapter never substitutes defaults for a caller-supplied pair:
        // a blank member is a configuration error that must fail fast (the
        // tolerant missing-entry case belongs to the registry).
        var build = () => new BinanceAdapter(
            NullLogger<BinanceAdapter>.Instance,
            Substitute.For<IHttpClientFactory>(),
            new BinanceEndpoints(restBaseUrl, wsUrl));

        build.Should().Throw<ArgumentException>();
    }
}
