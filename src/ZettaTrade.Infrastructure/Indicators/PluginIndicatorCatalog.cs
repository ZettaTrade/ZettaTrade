namespace ZettaTrade.Infrastructure.Indicators;

using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging;
using ZettaTrade.Charting;
using ZettaTrade.Infrastructure.Configuration;

/// <summary>
/// The plugin loader of the indicator seam (T-034): every <c>*.dll</c> in
/// the plugins directory is scanned once for <see cref="IIndicator"/>
/// implementations. A malformed or non-plugin assembly is skipped with a
/// logged warning and the chart keeps working - the loader never throws.
/// The scan runs at startup only: the plugin set is fixed for the session.
/// </summary>
/// <remarks>
/// The documented ctor convention decides how an implementation is built
/// and how its name is read: a public <c>ctor(int)</c> makes the period
/// configurable (the preferred shape), a parameterless constructor is the
/// fallback. The name is read from an instance: the period-carrying ctor is
/// probed with the smallest sane period first, so a plugin that rejects it
/// but offers a parameterless constructor still registers.
/// </remarks>
public sealed class PluginIndicatorCatalog : IIndicatorCatalog
{
    #region Constants

    /// <summary>The file pattern the plugins directory scan covers.</summary>
    private const string PluginFilePattern = "*.dll";

    /// <summary>Period the ctor-probe instantiates with (the smallest sane value for any period-based indicator).</summary>
    private const int ProbePeriod = 1;

    #endregion

    #region Private Fields

    private readonly ILogger<PluginIndicatorCatalog> _logger;

    /// <summary>The discovered indicators by canonical name; the first registration wins (a later duplicate is warned and dropped).</summary>
    private readonly ConcurrentDictionary<string, PluginDescriptor> _descriptors =
        new(StringComparer.Ordinal);

    #endregion

    #region Constructors

    /// <summary>Production construction: scan the real plugins directory.</summary>
    public PluginIndicatorCatalog(ILogger<PluginIndicatorCatalog> logger)
        : this(ConfigPathResolver.PluginsDirectory, logger)
    {
    }

    /// <summary>Test/fixture construction: scan the given directory.</summary>
    public PluginIndicatorCatalog(string pluginsDirectory, ILogger<PluginIndicatorCatalog> logger)
    {
        _logger = logger;
        Scan(pluginsDirectory);
    }

    #endregion

    #region IIndicatorCatalog

    /// <inheritdoc />
    public IIndicator? Create(string name, int period)
    {
        if (!_descriptors.TryGetValue(name, out var descriptor))
        {
            return null;
        }

        try
        {
            return descriptor.Build(period);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Indicator '{Name}' rejected the period {Period}; the overlay row is skipped",
                name,
                period);
            return null;
        }
    }

    #endregion

    #region Plugin Scan

    private void Scan(string pluginsDirectory)
    {
        if (!Directory.Exists(pluginsDirectory))
        {
            _logger.LogInformation(
                "The plugins directory does not exist ({Path}); no indicators are loaded",
                pluginsDirectory);
            return;
        }

        foreach (var file in Directory.EnumerateFiles(pluginsDirectory, PluginFilePattern))
        {
            ScanAssembly(file);
        }

        _logger.LogInformation(
            "The plugins directory {Path} provided indicators: {Indicators}",
            pluginsDirectory,
            _descriptors.Count == 0 ? "none" : string.Join(", ", _descriptors.Keys.Order()));
    }

    private void ScanAssembly(string filePath)
    {
        Assembly assembly;

        try
        {
            assembly = Assembly.LoadFrom(filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Plugin assembly {Path} failed to load and is skipped", filePath);
            return;
        }

        foreach (var type in assembly.GetTypes())
        {
            if (!type.IsClass || type.IsAbstract || !typeof(IIndicator).IsAssignableFrom(type))
            {
                continue;
            }

            RegisterIndicator(type, filePath);
        }
    }

    /// <summary>
    /// Read the implementation's name via the ctor convention and register
    /// it when the name is free. A duplicate name, a ctor the probe cannot
    /// build with or a name the build throws on is warned and skipped.
    /// </summary>
    private void RegisterIndicator(Type type, string filePath)
    {
        var periodCtor = FindPeriodCtor(type);
        var parameterlessCtor = FindParameterlessCtor(type);

        // The name needs an instance: probe the period ctor first (period is
        // the common configurable shape), fall back to the parameterless.
        IIndicator? probe;
        if (periodCtor is not null)
        {
            try
            {
                probe = (IIndicator)periodCtor.Invoke([ProbePeriod])!;
            }
            catch
            {
                probe = TryParameterlessProbe(parameterlessCtor, type, filePath);
            }
        }
        else
        {
            probe = TryParameterlessProbe(parameterlessCtor, type, filePath);
        }

        if (probe is null)
        {
            return;
        }

        var name = probe.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            _logger.LogWarning(
                "Indicator type {Type} ({Path}) exposes an empty name and is skipped",
                type.FullName,
                filePath);
            return;
        }

        if (!_descriptors.TryAdd(name, new PluginDescriptor(name, periodCtor, parameterlessCtor)))
        {
            _logger.LogWarning(
                "Indicator name '{Name}' is already registered; the duplicate from {Path} is skipped",
                name,
                filePath);
        }
    }

    private static ConstructorInfo? FindPeriodCtor(Type type) =>
        type.GetConstructors()
            .FirstOrDefault(constructor =>
                constructor.GetParameters().Length == 1 &&
                constructor.GetParameters()[0].ParameterType == typeof(int));

    private static ConstructorInfo? FindParameterlessCtor(Type type) =>
        type.GetConstructors()
            .FirstOrDefault(constructor => constructor.GetParameters().Length == 0);

    private IIndicator? TryParameterlessProbe(
        ConstructorInfo? parameterlessCtor,
        Type type,
        string filePath)
    {
        if (parameterlessCtor is null)
        {
            _logger.LogWarning(
                "Indicator type {Type} ({Path}) offers no documented ctor (a public ctor(int) or a parameterless one) and is skipped",
                type.FullName,
                filePath);
            return null;
        }

        try
        {
            return (IIndicator)parameterlessCtor.Invoke([])!;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Indicator type {Type} ({Path}) failed to build and is skipped",
                type.FullName,
                filePath);
            return null;
        }
    }

    #endregion

    #region Descriptor

    /// <summary>
    /// One registered plugin implementation: the name it registers under and
    /// the ctor it builds through (the period ctor preferred, the
    /// parameterless otherwise).
    /// </summary>
    private sealed record PluginDescriptor(
        string Name,
        ConstructorInfo? PeriodCtor,
        ConstructorInfo? ParameterlessCtor)
    {
        /// <summary>
        /// Build for one settings row: the period reaches the period ctor
        /// when the plugin offers one; the parameterless build cannot be
        /// configured by the row (documented convention).
        /// </summary>
        public IIndicator Build(int period) =>
            PeriodCtor is not null
                ? (IIndicator)PeriodCtor.Invoke([period])!
                : (IIndicator)ParameterlessCtor!.Invoke([])!;
    }

    #endregion
}
