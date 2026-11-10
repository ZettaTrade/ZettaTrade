using Microsoft.Extensions.Logging;

namespace ZettaTrade.Plugins.Abstractions;

/// <summary>
/// The bounded services window a plugin sees (T-131): host capabilities
/// resolve through <see cref="GetService{T}"/>, never a raw
/// <see cref="IServiceProvider"/> full of app internals. The bound surface
/// stays versioned; the trading-safety obligations bind every call — an
/// order path inside a plugin goes through <c>IRiskManager</c> and
/// <c>IConfirmationService</c> like every other order path, no
/// exceptions.
/// </summary>
public interface IPluginContext
{
    /// <summary>The manifest id of the plugin this context serves.</summary>
    string PluginId { get; }

    /// <summary>
    /// Resolves a host capability. A missing capability answers
    /// <c>null</c> — rejected-not-defaulted: the plugin decides what an
    /// absent adapter or risk manager means for it, and refuses the order
    /// path rather than improvising a substitute.
    /// </summary>
    T? GetService<T>() where T : class;

    /// <summary>The host's logger factory — plugin logs ride the app's own files, never a console.</summary>
    ILoggerFactory LoggerFactory { get; }
}
