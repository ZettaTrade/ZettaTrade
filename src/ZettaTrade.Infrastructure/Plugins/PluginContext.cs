using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.Plugins.Abstractions;

namespace ZettaTrade.Infrastructure.Plugins;

/// <summary>
/// The host-side <see cref="IPluginContext"/> (T-131): the plugin's own DI
/// layer first, the host's services behind it. Missing capabilities answer
/// <c>null</c> — rejected-not-defaulted, the plugin decides.
/// </summary>
/// <param name="ManifestId">The plugin's manifest id.</param>
/// <param name="PluginLayer">The plugin's own DI layer (its registrations resolve here).</param>
/// <param name="HostLayer">The host's root provider.</param>
/// <param name="LoggerFactory">The host's logger factory.</param>
public sealed class PluginContext(
    string manifestId,
    IServiceProvider pluginLayer,
    IServiceProvider hostLayer,
    ILoggerFactory log) : IPluginContext
{
    /// <inheritdoc/>
    public string PluginId => manifestId;

    /// <inheritdoc/>
    public T? GetService<T>() where T : class
    {
        // The plugin's own layer first: a plugin service never gets
        // shadowed by a host registration. The host layer answers what the
        // plugin layer does not own (market data adapters, settings,
        // risk/confirmation) and null when the capability is absent.
        return pluginLayer.GetService<T>() ?? hostLayer.GetService<T>();
    }

    /// <inheritdoc/>
    public ILoggerFactory LoggerFactory { get; } = log ?? throw new ArgumentNullException(nameof(log));
}
