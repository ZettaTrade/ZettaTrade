namespace ZettaTrade.Strategies;

using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging;

/// <summary>
/// The plugin loader of the strategy seam (T-111): every <c>*.dll</c> in
/// the strategies plugins directory is scanned once for
/// <see cref="IStrategy"/> implementations. A malformed or non-plugin
/// assembly is skipped and the rest of the scan keeps working - the
/// not-an-assembly drop rides the debug level (T-229: a stray file in the
/// plugins folder is the scan's routine cargo, never a failure family), a
/// real inspection failure stays a warning; the loader never throws (the
/// T-054 tolerance canon, reused wholesale: the load guard, the GetTypes()
/// ReflectionTypeLoadException guard and the per-type guard). The scan
/// runs at load only: the plugin set is fixed for the session.
/// </summary>
/// <remarks>
/// The documented ctor convention decides how an implementation is built
/// and how its id is read: a public parameterless constructor is probed
/// (it is the strategy seam's only documented shape), the canonical
/// <see cref="IStrategy.Id"/> is read from the built instance. A
/// whitespace id, an id already registered (the first registration wins)
/// or a type the probe cannot build is warned and skipped - nothing
/// defaults.
/// </remarks>
public sealed class PluginStrategyCatalog : IStrategyCatalog
{
    #region Constants

    /// <summary>The file pattern the plugins directory scan covers.</summary>
    private const string PluginFilePattern = "*.dll";

    #endregion

    #region Private Fields

    private readonly ILogger<PluginStrategyCatalog> _logger;

    /// <summary>The discovered strategies by canonical id; the first registration wins (a later duplicate is warned and dropped).</summary>
    private readonly ConcurrentDictionary<string, IStrategy> _registered = new(StringComparer.Ordinal);

    /// <summary>
    /// The strategies snapshot the contract hands out: materialized once
    /// the scan completes (registrations happen nowhere else), in load
    /// order, stable for the session lifetime.
    /// </summary>
    private volatile IReadOnlyList<IStrategy> _all = [];

    /// <summary>The registrations in load order; the scan is single-threaded, so a plain list is the honest keeper.</summary>
    private readonly List<IStrategy> _loadOrder = [];

    #endregion

    #region Constructors

    /// <summary>
    /// Construct the catalog: the scan runs once over the given
    /// strategies plugins directory and the snapshot is frozen from then
    /// on. A missing directory is an honest zero (an information log, an
    /// empty catalog), never an error.
    /// </summary>
    /// <param name="strategiesDirectory">The directory scanned for strategy plugin assemblies.</param>
    /// <param name="logger">The loader's logger.</param>
    public PluginStrategyCatalog(string strategiesDirectory, ILogger<PluginStrategyCatalog> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentException.ThrowIfNullOrWhiteSpace(strategiesDirectory);
        Scan(strategiesDirectory);
    }

    #endregion

    #region IStrategyCatalog

    /// <inheritdoc />
    public IReadOnlyList<IStrategy> All => _all;

    /// <inheritdoc />
    public IStrategy? Get(string strategyId) =>
        _registered.TryGetValue(strategyId, out var strategy) ? strategy : null;

    #endregion

    #region Plugin Scan

    private void Scan(string strategiesDirectory)
    {
        if (!Directory.Exists(strategiesDirectory))
        {
            _logger.LogInformation(
                "The strategies directory does not exist ({Path}); no strategies are loaded",
                strategiesDirectory);
            return;
        }

        foreach (var file in Directory.EnumerateFiles(strategiesDirectory, PluginFilePattern))
        {
            ScanAssembly(file);
        }

        // The scan is the only registrant and it runs synchronously in the
        // constructor window: the materialized snapshot is stable for the
        // session lifetime from this point on.
        _all = [.. _loadOrder];

        _logger.LogInformation(
            "The strategies directory {Path} provided strategies: {Strategies}",
            strategiesDirectory,
            _registered.Count == 0 ? "none" : string.Join(", ", _all.Select(strategy => strategy.Id)));
    }

    private void ScanAssembly(string filePath)
    {
        Assembly assembly;

        try
        {
            assembly = Assembly.LoadFrom(filePath);
        }
        catch (BadImageFormatException ex)
        {
            // The honest drop (T-229): a file that is not a loadable
            // assembly at all — a stray dll, a native helper, a corrupted
            // download — rides the debug level. The plugins folder may
            // carry non-plugin files by design; the scan's job is the
            // folder's real strategies, and the summary line's missing
            // name is the only production trace of a dropped file.
            _logger.LogDebug(ex, "Strategy plugin assembly {Path} is not a loadable assembly and is skipped", filePath);
            return;
        }
        catch (Exception ex)
        {
            // A file the OS could not hand over (locked, inaccessible) is a
            // real environment failure and stays loud.
            _logger.LogWarning(ex, "Strategy plugin assembly {Path} failed to load and is skipped", filePath);
            return;
        }

        // The type enumeration needs its own guard: Assembly.GetTypes()
        // throws on its own when ANY type of the plugin fails to load - a
        // missing dependency assembly is the common cause - and one broken
        // file aborting the whole directory scan is the exact T-054
        // regression (the pre-fix BUG-014 shape). The file is skipped
        // loudly; the scan continues with the healthy neighbors.
        Type[] pluginTypes;

        try
        {
            pluginTypes = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            _logger.LogWarning(
                ex,
                "Strategy plugin assembly {Path} has types that fail to load (missing dependencies?); the file is skipped",
                filePath);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Strategy plugin assembly {Path} could not be inspected and is skipped", filePath);
            return;
        }

        foreach (var type in pluginTypes)
        {
            // Even a type GetTypes() returned can be partially broken (its
            // members may still fail to resolve): inspecting it must never
            // take the file - and the healthy neighbors - down with it.
            // The type alone is skipped, the file's scan continues.
            try
            {
                if (!type.IsClass || type.IsAbstract || !typeof(IStrategy).IsAssignableFrom(type))
                {
                    continue;
                }

                RegisterStrategy(type, filePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Strategy type {Type} in {Path} could not be inspected and is skipped",
                    type.FullName,
                    filePath);
            }
        }
    }

    /// <summary>
    /// Read the implementation's id via the parameterless-ctor convention
    /// and register it when the id is free. A duplicate id, a missing
    /// parameterless constructor or a build the probe throws on is warned
    /// and skipped - nothing substitutes.
    /// </summary>
    private void RegisterStrategy(Type type, string filePath)
    {
        var parameterlessCtor = FindParameterlessCtor(type);
        if (parameterlessCtor is null)
        {
            _logger.LogWarning(
                "Strategy type {Type} ({Path}) offers no documented ctor (a public parameterless one) and is skipped",
                type.FullName,
                filePath);
            return;
        }

        IStrategy? probe;
        try
        {
            probe = (IStrategy)parameterlessCtor.Invoke([])!;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Strategy type {Type} ({Path}) failed to build and is skipped",
                type.FullName,
                filePath);
            return;
        }

        var id = probe.Id;
        if (string.IsNullOrWhiteSpace(id))
        {
            _logger.LogWarning(
                "Strategy type {Type} ({Path}) exposes an empty id and is skipped",
                type.FullName,
                filePath);
            return;
        }

        if (!_registered.TryAdd(id, probe))
        {
            _logger.LogWarning(
                "Strategy id '{Id}' is already registered; the duplicate from {Path} is skipped",
                id,
                filePath);
            return;
        }

        _loadOrder.Add(probe);
    }

    private static ConstructorInfo? FindParameterlessCtor(Type type) =>
        type.GetConstructors()
            .FirstOrDefault(constructor => constructor.GetParameters().Length == 0);

    #endregion
}
