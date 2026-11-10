using Microsoft.Extensions.DependencyInjection;
using ZettaTrade.Plugins.Abstractions;

namespace ZettaTrade.Plugins.Fixtures.Hazards;

/// <summary>
/// The identifier collision (T-131): the SAME id as the fixtures
/// assembly's <c>fx.sample</c>, a different type. A scan directory
/// carrying both is loud — every carrier loads never, the load report
/// carries the collision.
/// </summary>
[ZettaTradePlugin("fx.sample", "SDK Fixture Duplicate", "2.0.0")]
public sealed class DuplicateIdPlugin : IZettaTradePlugin
{
    /// <inheritdoc/>
    public void ConfigureServices(IServiceCollection services)
    {
    }

    /// <inheritdoc/>
    public Task InitializeAsync(IPluginContext pluginContext, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

/// <summary>
/// The declared-but-missing dependency (T-131): the manifest names a
/// plugin id no scan produces. The dependency rule says such a plugin
/// loads never — loudly, not quietly half-alive.
/// </summary>
[ZettaTradePlugin("fx.ghost-dep", "SDK Fixture Ghost Dependency", "1.0.0", "fx.does-not-exist")]
public sealed class MissingDependencyPlugin : IZettaTradePlugin
{
    /// <inheritdoc/>
    public void ConfigureServices(IServiceCollection services)
    {
    }

    /// <inheritdoc/>
    public Task InitializeAsync(IPluginContext pluginContext, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

/// <summary>
/// The unparsable version (T-131): the manifest's version is not SemVer.
/// Loads never; the load report carries the reason.
/// </summary>
[ZettaTradePlugin("fx.bad-version", "SDK Fixture Bad Version", "not-semver")]
public sealed class BadVersionPlugin : IZettaTradePlugin
{
    /// <inheritdoc/>
    public void ConfigureServices(IServiceCollection services)
    {
    }

    /// <inheritdoc/>
    public Task InitializeAsync(IPluginContext pluginContext, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

/// <summary>
/// The orphaned entry (T-131): implements <see cref="IZettaTradePlugin"/>
/// WITHOUT the manifest attribute — no identity, loads never, loudly.
/// </summary>
public sealed class ManifestLessPlugin : IZettaTradePlugin
{
    /// <inheritdoc/>
    public void ConfigureServices(IServiceCollection services)
    {
    }

    /// <inheritdoc/>
    public Task InitializeAsync(IPluginContext pluginContext, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

/// <summary>The self-dependency (T-131): the manifest names its own id — the smallest cycle the loader refuses.</summary>
[ZettaTradePlugin("fx.self", "SDK Fixture Self Cycle", "1.0.0", "fx.self")]
public sealed class SelfCyclePlugin : IZettaTradePlugin
{
    /// <inheritdoc/>
    public void ConfigureServices(IServiceCollection services)
    {
    }

    /// <inheritdoc/>
    public Task InitializeAsync(IPluginContext pluginContext, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

/// <summary>The mirrored cycle head (T-131): a depends on b while b depends on a — the pair loads never.</summary>
[ZettaTradePlugin("fx.ring-a", "SDK Fixture Ring A", "1.0.0", "fx.ring-b")]
public sealed class RingAPlugin : IZettaTradePlugin
{
    /// <inheritdoc/>
    public void ConfigureServices(IServiceCollection services)
    {
    }

    /// <inheritdoc/>
    public Task InitializeAsync(IPluginContext pluginContext, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

/// <summary>The mirrored cycle tail: <c>fx.ring-a</c> and this never resolve.</summary>
[ZettaTradePlugin("fx.ring-b", "SDK Fixture Ring B", "1.0.0", "fx.ring-a")]
public sealed class RingBPlugin : IZettaTradePlugin
{
    /// <inheritdoc/>
    public void ConfigureServices(IServiceCollection services)
    {
    }

    /// <inheritdoc/>
    public Task InitializeAsync(IPluginContext pluginContext, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
