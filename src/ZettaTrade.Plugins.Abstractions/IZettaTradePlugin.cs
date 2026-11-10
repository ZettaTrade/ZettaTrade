using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace ZettaTrade.Plugins.Abstractions;

/// <summary>
/// The entry type of an out-of-tree plugin (T-131): the class carrying the
/// <see cref="ZettaTradePluginAttribute"/> implements this. The loader
/// instantiates it once, calls <see cref="ConfigureServices"/> so the
/// plugin's own services land in a per-plugin DI layer, and then runs
/// <see cref="InitializeAsync"/> once before the host consumes the
/// plugin's registrations.
/// </summary>
public interface IZettaTradePlugin
{
    /// <summary>
    /// Registers the plugin's own services: panel/toolbar/settings
    /// descriptors, the plugin's internal collaborators — never a host
    /// service the plugin merely consumes. Runs once, before
    /// <see cref="InitializeAsync"/>.
    /// </summary>
    void ConfigureServices(IServiceCollection services);

    /// <summary>
    /// The one-time async start, following the app's
    /// <c>IAsyncInitializable</c> canon: awaits the startup work (loading
    /// caches, opening files), stores nothing that withers, touches no UI
    /// thread, honours <param name="cancellationToken"/>. Called after
    /// <see cref="ConfigureServices"/>, before the host serves this
    /// plugin's marketplace of registrations.
    /// </summary>
    Task InitializeAsync(
        IPluginContext pluginContext,
        CancellationToken cancellationToken);
}
