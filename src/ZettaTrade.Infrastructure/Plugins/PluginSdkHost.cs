using System.Reflection;
using Livery.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.Infrastructure.Licensing;
using ZettaTrade.Plugins.Abstractions;
using ZettaTrade.Plugins.Abstractions.Extensions;

namespace ZettaTrade.Infrastructure.Plugins;

/// <summary>
/// The blessed loader for out-of-tree SDK plugins (T-131): the plugins
/// directory (the same folder the indicator and strategy catalogs share)
/// is scanned once during the startup walk, every
/// <see cref="ZettaTradePluginAttribute"/>-manifested class gets a
/// validation pass (identity, SemVer, dependencies, cycles — each gate
/// refuses loudly), the survivors start in dependency order and register
/// their extensions into the <see cref="PluginExtensionRegistry"/>, and
/// one report pins every verdict.
/// </summary>
/// <param name="scanDirectory">The plugins directory the scan walks (top level only).</param>
/// <param name="registry">The registry the extensions land in.</param>
/// <param name="hostServices">The host's root provider — the capability window behind the plugin's own layer.</param>
/// <param name="logger">The host logger.</param>
/// <param name="licenseGate">
/// The entitlement gate (T-132) consulted before every load; null (an app
/// shape with the licensing module not registered) leaves every plugin
/// unprotected — the same posture the default empty protected list gives.
/// </param>
public sealed class PluginSdkHost(
    string scanDirectory,
    PluginExtensionRegistry registry,
    IServiceProvider hostServices,
    ILogger<PluginSdkHost> logger,
    ILicenseGate? licenseGate = null) : IAsyncInitializable
{
    /// <summary>The manifest record one candidate carries through the gates.</summary>
    private sealed record PluginCandidate(
        Type Type,
        ZettaTradePluginAttribute Manifest);

    #region Properties

    /// <summary>The registry the loaded plugins' extensions land in (the shell reads it).</summary>
    public PluginExtensionRegistry Extensions => registry;

    /// <summary>The load report from the latest run — empty before the startup walk.</summary>
    public PluginLoadReport Report { get; private set; } = PluginLoadReport.Empty;

    #endregion

    #region Public Methods

    /// <summary>
    /// The one scan-load-register pass. Never throws — a hostile plugins
    /// directory must not take the startup walk down; whatever survived
    /// the gates is what the shell sees.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var loaded = new List<string>();
        var refused = new List<string>();
        var errors = new List<string>();
        var panels = new List<PluginPanelRegistration>();
        var toolbarItems = new List<PluginToolbarRegistration>();
        var settingsSections = new List<PluginSettingsSectionRegistration>();

        try
        {
            var manifests = Scan(errors, refused);

            if (manifests.Count == 0)
            {
                logger.LogInformation(
                    "The plugin SDK found no loadable plugins in '{Directory}'.",
                    scanDirectory);
                registry.Replace(PluginExtensionSnapshot.Empty);
                return;
            }

            var unique = RejectDuplicateIds(manifests, errors, refused);
            var ordered = OrderForStartup(unique, errors, refused);

            if (ordered.Count == 0)
            {
                logger.LogInformation(
                    "The plugin SDK found {Count} manifest(s) and loaded none of them.",
                    manifests.Count);
                registry.Replace(PluginExtensionSnapshot.Empty);
                return;
            }

            var loadable = await GateForLicenseAsync(ordered, errors, refused, cancellationToken);

            if (loadable.Count == 0)
            {
                logger.LogInformation(
                    "The plugin SDK found {Count} manifest(s) and loaded none of them: every candidate refused at a gate.",
                    manifests.Count);
                registry.Replace(PluginExtensionSnapshot.Empty);
                return;
            }

            var loggerFactory = hostServices.GetRequiredService<ILoggerFactory>();

            foreach (var candidate in loadable)
            {
                var id = candidate.Manifest.PluginId;
                try
                {
                    await LoadSingleAsync(
                        candidate,
                        id,
                        loggerFactory,
                        cancellationToken,
                        loaded,
                        errors,
                        refused,
                        panels,
                        toolbarItems,
                        settingsSections);
                }
                catch (Exception fault)
                {
                    refused.Add(id);
                    errors.Add($"The plugin '{id}' refused at load: {FaultLine(fault)}");
                    logger.LogError(
                        fault,
                        "Plugin '{PluginId}' refused at load.",
                        id);
                }
            }

            registry.Replace(new PluginExtensionSnapshot(
                [.. panels],
                [.. toolbarItems],
                [.. settingsSections]));
        }
        catch (Exception fault)
        {
            // A hostile directory must not take the startup walk down: log
            // loudly and serve whatever made it through the gates.
            logger.LogError(
                fault,
                "The plugin SDK load pass failed; the partial results stand.");
            errors.Add($"The load pass itself failed: {FaultLine(fault)}");
            registry.Replace(PluginExtensionSnapshot.Empty);
        }
        finally
        {
            Report = new PluginLoadReport(
                [.. loaded.Order(StringComparer.Ordinal)],
                [.. refused.Order(StringComparer.Ordinal)],
                [.. errors.Order(StringComparer.Ordinal)]);
        }

        logger.LogInformation(
            "The plugin SDK loaded {LoadedCount} of {CandidateCount} plugin(s): {LoadedIds}",
            loaded.Count,
            loaded.Count + refused.Count,
            string.Join(", ", loaded.Order(StringComparer.Ordinal)));
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// The license entitlement stage (T-132): every candidate consults the
    /// gate before any load leg runs; an entitled candidate keeps its
    /// place, a refused one lands in the report with an explicit error
    /// line (the gate's absence — licensing not registered — protects
    /// nothing, matching the default empty protected list).
    /// </summary>
    private async Task<List<PluginCandidate>> GateForLicenseAsync(
        List<PluginCandidate> ordered,
        List<string> errors,
        List<string> refused,
        CancellationToken cancellationToken)
    {
        if (licenseGate is null)
        {
            return ordered;
        }

        var loadable = new List<PluginCandidate>();
        foreach (var candidate in ordered)
        {
            var id = candidate.Manifest.PluginId;
            LicenseVerdict verdict;
            try
            {
                verdict = await licenseGate.EvaluateAsync(id, cancellationToken);
            }
            catch (Exception fault)
            {
                refused.Add(id);
                errors.Add($"The plugin '{id}' refused at the license gate: the entitlement check failed ({FaultLine(fault)}) — refused.");
                logger.LogError(
                    fault,
                    "The plugin '{PluginId}' failed its license entitlement check; refused.",
                    id);
                continue;
            }

            if (verdict.Entitled)
            {
                loadable.Add(candidate);
                continue;
            }

            refused.Add(id);
            errors.Add($"The plugin '{id}' refused at the license gate: {verdict.Status} — no valid entitlement, refused.");
            logger.LogError(
                "The plugin '{PluginId}' carries no valid license entitlement ({Status}); refused.",
                id,
                verdict.Status);
        }

        return loadable;
    }

    /// <summary>
    /// One plugin's full load leg: construct, shape its own DI layer (its
    /// registrations first, the host's <see cref="IPluginContext"/> seat it
    /// cannot shadow), start it once, collect what it registered.
    /// </summary>
    private async Task LoadSingleAsync(
        PluginCandidate candidate,
        string pluginId,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken,
        List<string> loaded,
        List<string> errors,
        List<string> refused,
        List<PluginPanelRegistration> panels,
        List<PluginToolbarRegistration> toolbarItems,
        List<PluginSettingsSectionRegistration> settingsSections)
    {
        var instance = (IZettaTradePlugin)(Activator.CreateInstance(candidate.Type)
            ?? throw new InvalidOperationException(
                "The plugin constructor answered null."));

        // The plugin's own layer: its registrations land in a child
        // collection and resolve first; the host's root provider sits
        // behind it. The context joins the layer automatically — the
        // plugin only ever consumes it in its own factories.
        var childServices = new ServiceCollection();
        instance.ConfigureServices(childServices);
        childServices.AddSingleton(instance);
        childServices.AddSingleton<IPluginContext>(serviceProvider => new PluginContext(
            pluginId,
            serviceProvider,
            hostServices,
            loggerFactory));

        var pluginLayer = childServices.BuildServiceProvider();
        var context = pluginLayer.GetRequiredService<IPluginContext>();

        await instance.InitializeAsync(context, cancellationToken);

        foreach (var panel in pluginLayer.GetServices<PluginPanelDescriptor>())
        {
            panels.Add(new PluginPanelRegistration(pluginId, panel));
        }

        foreach (var item in pluginLayer.GetServices<PluginToolbarItemDescriptor>())
        {
            toolbarItems.Add(new PluginToolbarRegistration(pluginId, item));
        }

        foreach (var section in pluginLayer.GetServices<PluginSettingsSectionDescriptor>())
        {
            settingsSections.Add(new PluginSettingsSectionRegistration(pluginId, section));
        }

        loaded.Add(pluginId);
        logger.LogInformation(
            "Plugin '{PluginId}' ({Version}) loaded: {PanelCount} panel(s), {ToolCount} toolbar item(s), {SectionCount} settings section(s).",
            pluginId,
            candidate.Manifest.Version,
            panels.Count(p => p.PluginId == pluginId),
            toolbarItems.Count(p => p.PluginId == pluginId),
            settingsSections.Count(p => p.PluginId == pluginId));
    }

    /// <summary>
    /// The scan stage: every readable dll at the scan directory's top
    /// level contributes itsSDK candidates; one manifest lie refuses the
    /// type right here.
    /// </summary>
    private List<PluginCandidate> Scan(
        List<string> errors,
        List<string> refused)
    {
        var candidates = new List<PluginCandidate>();
        if (!Directory.Exists(scanDirectory))
        {
            // A missing directory is not a fault: an installation carries
            // none until the first plugin ships.
            return candidates;
        }

        foreach (var filePath in Directory
            .EnumerateFiles(scanDirectory, "*.dll", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.OrdinalIgnoreCase))
        {
            Assembly assembly;
            try
            {
                assembly = Assembly.LoadFrom(filePath);
            }
            catch (Exception fault) when (
                fault is BadImageFormatException
                    or IOException
                    or UnauthorizedAccessException)
            {
                logger.LogWarning(
                    fault,
                    "The plugin SDK skipped '{FilePath}': not a readable assembly.",
                    filePath);
                continue;
            }

            foreach (var type in ReadTypes(assembly, filePath))
            {
                if (type is not { IsClass: true, IsAbstract: false } pluginType ||
                    !typeof(IZettaTradePlugin).IsAssignableFrom(pluginType))
                {
                    continue;
                }

                if (pluginType.GetCustomAttribute<ZettaTradePluginAttribute>() is not { } manifest)
                {
                    refused.Add(pluginType.FullName ?? pluginType.Name);
                    errors.Add($"The plugin type '{pluginType.FullName}' implements IZettaTradePlugin without the ZettaTradePlugin manifest — no identity, refused.");
                    logger.LogError(
                        "The plugin type '{PluginType}' implements IZettaTradePlugin without the ZettaTradePlugin manifest; refused.",
                        pluginType.FullName);
                    continue;
                }

                if (ValidateManifest(pluginType, manifest, errors, refused))
                {
                    candidates.Add(new PluginCandidate(pluginType, manifest));
                }
            }
        }

        return candidates;
    }

    /// <summary>
    /// The manifest integrity gate: the id, the display name, the version
    /// shape and the dependency names — one lie refuses the plugin.
    /// </summary>
    private bool ValidateManifest(
        Type pluginType,
        ZettaTradePluginAttribute manifest,
        List<string> errors,
        List<string> refused)
    {
        var id = manifest.PluginId?.Trim();
        if (string.IsNullOrWhiteSpace(id))
        {
            refused.Add(pluginType.FullName ?? pluginType.Name);
            errors.Add($"The plugin type '{pluginType.FullName}' carries a blank plugin id — refused.");
            logger.LogError(
                "The plugin type '{PluginType}' carries a blank plugin id; refused.",
                pluginType.FullName);
            return false;
        }

        if (string.IsNullOrWhiteSpace(manifest.DisplayName))
        {
            refused.Add(id);
            errors.Add($"The plugin '{id}' carries a blank display name — refused.");
            logger.LogError("The plugin '{PluginId}' carries a blank display name; refused.", id);
            return false;
        }

        if (!PluginSemVer.TryParse(manifest.Version, out _))
        {
            refused.Add(id);
            errors.Add($"The plugin '{id}' carries the version '{manifest.Version}', which is not SemVer 2 — refused.");
            logger.LogError(
                "The plugin '{PluginId}' carries the version '{Version}', which is not SemVer 2; refused.",
                id,
                manifest.Version);
            return false;
        }

        foreach (var dependency in manifest.Dependencies)
        {
            if (string.IsNullOrWhiteSpace(dependency))
            {
                refused.Add(id);
                errors.Add($"The plugin '{id}' declares a blank dependency — refused.");
                logger.LogError("The plugin '{PluginId}' declares a blank dependency; refused.", id);
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The identity gate: one id, one plugin. Every carrier of a duplicated
    /// id loses the load and lands in the report (deterministic — no
    /// scan-order FirstWins race).
    /// </summary>
    private List<PluginCandidate> RejectDuplicateIds(
        List<PluginCandidate> manifests,
        List<string> errors,
        List<string> refused)
    {
        var survivors = new List<PluginCandidate>();
        foreach (var group in manifests
            .GroupBy(candidate => candidate.Manifest.PluginId, StringComparer.Ordinal))
        {
            if (group.Count() == 1)
            {
                survivors.Add(group.Single());
                continue;
            }

            var carriers = group
                .Select(candidate => candidate.Type.FullName ?? candidate.Type.Name)
                .Order(StringComparer.Ordinal)
                .ToArray();
            errors.Add($"The plugin id '{group.Key}' is claimed by {carriers.Length} types ({string.Join(", ", carriers)}) — duplicate id, every carrier refused.");
            logger.LogError(
                "The plugin id '{PluginId}' is claimed by {Count} types; every carrier refused.",
                group.Key,
                carriers.Length);
            refused.Add(group.Key);
        }

        return survivors;
    }

    /// <summary>
    /// The dependency gates: a declared id nothing produced refuses the
    /// dependent; a cycle refuses every member. The survivors order
    /// dependency-first through a deterministic Kahn walk (sorted queue,
    /// sorted dependents).
    /// </summary>
    private List<PluginCandidate> OrderForStartup(
        List<PluginCandidate> manifests,
        List<string> errors,
        List<string> refused)
    {
        var byId = manifests.ToDictionary(
            candidate => candidate.Manifest.PluginId,
            StringComparer.Ordinal);

        var started = new List<PluginCandidate>();
        var readyBeforeOrder = byId.Values.ToList();
        foreach (var candidate in readyBeforeOrder)
        {
            var missing = candidate.Manifest.Dependencies
                .Where(dependency => !byId.ContainsKey(dependency))
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (missing.Length == 0)
            {
                started.Add(candidate);
                continue;
            }

            refused.Add(candidate.Manifest.PluginId);
            errors.Add($"The plugin '{candidate.Manifest.PluginId}' declares the dependency '{missing.First()}' which no scan produces — refused.");
            logger.LogError(
                "The plugin '{PluginId}' declares the dependency '{MissingDependency}', which no scan produces; refused.",
                candidate.Manifest.PluginId,
                missing.First());
        }

        // Kahn over the survivors: the unresolved count counts every
        // remaining id, a resolved dependency decrements the dependents.
        var unresolvedCount = started.ToDictionary(
            candidate => candidate.Manifest.PluginId,
            candidate => candidate.Manifest.Dependencies.Distinct(StringComparer.Ordinal).Count());
        var dependents = started.ToDictionary(
            candidate => candidate.Manifest.PluginId,
            _ => new List<string>());
        foreach (var candidate in started)
        {
            foreach (var dependency in candidate.Manifest.Dependencies.Distinct(StringComparer.Ordinal))
            {
                // A self edge can never unlock: the count stays positive
                // and the cycle pass below refuses it.
                if (dependency != candidate.Manifest.PluginId)
                {
                    dependents[dependency].Add(candidate.Manifest.PluginId);
                }
            }
        }

        var ready = unresolvedCount
            .Where(pair => pair.Value == 0)
            .Select(pair => pair.Key)
            .Order(StringComparer.Ordinal)
            .ToList();

        var ordered = new List<PluginCandidate>();
        while (ready.Count > 0)
        {
            var id = ready[0];
            ready.RemoveAt(0);
            ordered.Add(byId[id]);

            dependents[id].Sort(StringComparer.Ordinal);
            foreach (var dependent in dependents[id])
            {
                unresolvedCount[dependent]--;
                if (unresolvedCount[dependent] == 0)
                {
                    ready.Add(dependent);
                    ready.Sort(StringComparer.Ordinal);
                }
            }
        }

        foreach (var id in unresolvedCount
            .Where(pair => pair.Value > 0)
            .Select(pair => pair.Key)
            .Order(StringComparer.Ordinal))
        {
            refused.Add(id);
            errors.Add($"The plugin '{id}' sits on a dependency cycle — refused.");
            logger.LogError(
                "The plugin '{PluginId}' sits on a dependency cycle; refused.",
                id);
        }

        return ordered;
    }

    /// <summary>The types of one assembly, tolerant of partially failed reads.</summary>
    private Type[] ReadTypes(
        Assembly assembly,
        string filePath)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException fault)
        {
            logger.LogWarning(
                fault,
                "The plugin SDK's type read of '{FilePath}' partially failed: {FaultCount} loader fault(s); the readable types are scanned, the rest are skipped.",
                filePath,
                fault.LoaderExceptions.Length);
            return fault.Types.Where(t => t is not null).ToArray()!;
        }
    }

    /// <summary>The single, human-readable fault line the report carries.</summary>
    private static string FaultLine(Exception fault) =>
        $"{fault.GetType().FullName}: {fault.Message}";

    #endregion
}
