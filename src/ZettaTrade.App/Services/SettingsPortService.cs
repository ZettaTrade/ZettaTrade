namespace ZettaTrade.App.Services;

using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Configuration;

/// <summary>
/// Why an import was rejected — never coerced, never defaulted: the port
/// refuses the whole file instead (the trading-safety rule applied to the
/// user's stored state).
/// </summary>
public enum SettingsPortErrorReason
{
    /// <summary>The text is not JSON at all, or the root is not an object.</summary>
    MalformedJson,

    /// <summary>The root does not carry the port's own schema marker.</summary>
    UnknownSchema,

    /// <summary>The schema version is absent or not the exactly-shipped one.</summary>
    UnsupportedVersion,

    /// <summary>The settings payload carries a section or key the registry does not own.</summary>
    UnknownEntry,

    /// <summary>A stored value does not fit its key's stored-shape kind.</summary>
    WrongPayloadKind,
}

/// <summary>
/// A rejected import. The reason rides a typed enum; the message carries the
/// English log detail only — the user-facing text is keyed off the reason
/// through the localization system.
/// </summary>
public sealed class SettingsPortImportException : Exception
{
    /// <summary>Create a rejection with its reason and an English log detail.</summary>
    public SettingsPortImportException(SettingsPortErrorReason reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>The typed reason the error text is localized from.</summary>
    public SettingsPortErrorReason Reason { get; }
}

/// <summary>What one plan row would do to the stored state.</summary>
public enum PortImportChange
{
    /// <summary>The stored state lacks the key; the import adds it.</summary>
    Added,

    /// <summary>The stored value differs from the imported one; the import replaces it.</summary>
    Changed,
}

/// <summary>One row of the dry-run diff preview: the impact of one imported entry against the current store.</summary>
/// <param name="Section">The entry's section.</param>
/// <param name="Key">The entry's key.</param>
/// <param name="Change">The impact kind (the preview lists only real impacts).</param>
/// <param name="CurrentValueJson">The stored value as JSON compact text, or null when the store lacks the key.</param>
/// <param name="ImportedValueJson">The imported value as JSON compact text.</param>
public sealed record PortImportRow(
    string Section,
    string Key,
    PortImportChange Change,
    string? CurrentValueJson,
    string ImportedValueJson)
{
    /// <summary>Whether the row adds a key the store lacks (the preview's kind label picks its word off this).</summary>
    public bool IsAdded => Change == PortImportChange.Added;

    /// <summary>Whether the row replaces a stored value (see <see cref="IsAdded"/>).</summary>
    public bool IsChanged => Change == PortImportChange.Changed;

    /// <summary>The address label the preview prints the row by.</summary>
    public string Address => $"{Section}.{Key}";
}

/// <summary>
/// A fully validated import: every mutation passed the registry allowlist
/// and its value kind — the only thing left is the user's look at
/// <see cref="Rows"/> and the apply call. The plan previews rows only for
/// real impacts; an entry describing the already-stored state never applies
/// (the same no-op discipline every settings writer keeps).
/// </summary>
/// <param name="Rows">The dry-run diff preview rows (added + changed entries only).</param>
/// <param name="Mutations">The keyed mutations the apply runs as one locked batch.</param>
public sealed record SettingsPortImportPlan(
    IReadOnlyList<PortImportRow> Rows,
    IReadOnlyList<SettingsMutation> Mutations);

/// <summary>
/// The settings port (T-100): export of the user-adjustable settings subset
/// as a versioned JSON file, a validated import with a dry-run diff preview
/// whose apply rides the settings service's locked batch (every change
/// re-applies live through the reactive stream — no restart), and the
/// factory reset behind a typed human confirmation.
/// Secrets are structurally absent: the service addresses the settings
/// service only — no <c>ISecretStore</c> is anywhere in its dependency set,
/// so no credential can ever reach an export, and the reset never touches
/// secrets (the board's reset text says nothing about deleting them). The
/// concrete <see cref="SettingsService"/> is the dependency on purpose: the
/// batch/snapshot primitives are the concrete store's members (the same
/// concrete-over-interface precedent the settings ViewModel follows for
/// <c>McpHostService</c>).
/// </summary>
public sealed class SettingsPortService
{
    /// <summary>The export file's schema marker.</summary>
    public const string SchemaMarker = "zettatrade-settings-port";

    /// <summary>The exactly-supported schema version (an import of any other version is rejected, never adapted).</summary>
    public const int SchemaVersion = 1;

    /// <summary>The localization key of the typed factory-reset confirmation (the destructive gate).</summary>
    public const string ResetConfirmMessageKey = "Port.ResetConfirm";

    /// <summary>The export's root property names, in their fixed, byte-stable order.</summary>
    private const string SchemaPropertyName = "schema";
    private const string VersionPropertyName = "version";
    private const string SettingsPropertyName = "settings";

    private readonly SettingsService _settings;
    private readonly ILogger<SettingsPortService> _logger;

    /// <summary>Create the port service over the app's settings store.</summary>
    public SettingsPortService(SettingsService settings, ILogger<SettingsPortService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <summary>The hidden JSON detail a structural reject carries into the caller's log surface.</summary>
    private const string NoDetail = "";

    /// <summary>
    /// Build the export JSON: the registry-owned stored keys, both levels
    /// sorted ordinally and a fixed root, so two exports of one state are
    /// byte-identical and export∘import∘export round-trips byte-stable.
    /// Schema marker and version ride the file; nothing else ever does.
    /// </summary>
    public async Task<string> ExportAsync(CancellationToken ct = default)
    {
        var snapshot = await _settings.SnapshotAsync(ct);

        var carried = snapshot
            .Where(entry => PortSettingsRegistry.TryGet(entry.Section, entry.Key) is not null)
            .GroupBy(entry => entry.Section, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
                StringComparer.Ordinal);

        using var stream = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString(SchemaPropertyName, SchemaMarker);
            writer.WriteNumber(VersionPropertyName, SchemaVersion);
            writer.WriteStartObject(SettingsPropertyName);
            foreach (var (section, keys) in carried)
            {
                writer.WriteStartObject(section);
                foreach (var (key, value) in keys.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(key);
                    value.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        _logger.LogInformation(
            "Settings exported: {SectionCount} section(s), {EntryCount} registry key(s)",
            carried.Count,
            carried.Sum(pair => pair.Value.Count));

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Write the export JSON to a local path. The file carries the
    /// registry-owned stored keys only — secrets can never ride it (the
    /// service addresses no secret store) — and the caller's store stays
    /// untouched whatever the file system says.
    /// </summary>
    public async Task ExportToFileAsync(string filePath, CancellationToken ct = default)
    {
        var json = await ExportAsync(ct);
        await File.WriteAllTextAsync(filePath, json, ct);
    }

    /// <summary>
    /// Validate an import file and build its dry-run plan — a pure validation
    /// pass over the stored state, nothing touched yet. Any reject
    /// (malformed JSON, foreign schema, wrong version, an unknown section or
    /// key, a value that fails its key's kind) throws the typed exception and
    /// the caller's store stays byte-stable: nothing partial ever lands.
    /// </summary>
    public async Task<SettingsPortImportPlan> DraftImportAsync(string jsonText, CancellationToken ct = default)
    {
        var (imported, reason, detail) = TryParseImport(jsonText);
        if (imported is null)
        {
            var message = reason is null
                ? "Settings import rejected"
                : $"Settings import rejected: {reason}{(detail.Length > 0 ? $" ({detail})" : string.Empty)}";
            throw new SettingsPortImportException(reason!.Value, message);
        }

        var snapshot = await _settings.SnapshotAsync(ct);
        var stored = snapshot.ToDictionary(entry => entry.CompositeKey, entry => entry, StringComparer.Ordinal);

        var rows = new List<PortImportRow>();
        var mutations = new List<SettingsMutation>();

        foreach (var (section, keys) in imported)
        {
            foreach (var (key, value) in keys)
            {
                var registryKey = PortSettingsRegistry.TryGet(section, key);
                if (registryKey is null)
                {
                    throw new SettingsPortImportException(
                        SettingsPortErrorReason.UnknownEntry,
                        $"Settings import rejected: '{section}.{key}' is not a registry-owned settings key");
                }

                if (!PortSettingsRegistry.IsValueValid(registryKey, value))
                {
                    throw new SettingsPortImportException(
                        SettingsPortErrorReason.WrongPayloadKind,
                        $"Settings import rejected: '{section}.{key}' does not fit its stored shape ({registryKey.Kind})");
                }

                var isStored = stored.TryGetValue(registryKey.CompositeKey, out var current);
                if (isStored && JsonElement.DeepEquals(current!.Value, value))
                {
                    // The imported entry describes the stored state: no
                    // mutation, no preview row (the no-op writer canon).
                    continue;
                }

                rows.Add(new PortImportRow(
                    registryKey.Section,
                    registryKey.Key,
                    isStored ? PortImportChange.Changed : PortImportChange.Added,
                    current?.Value.GetRawText(),
                    value.GetRawText()));

                mutations.Add(new SettingsMutation(registryKey.Section, registryKey.Key, value.Clone()));
            }
        }

        return new SettingsPortImportPlan(rows, mutations);
    }

    /// <summary>
    /// Apply a validated plan as ONE locked, one-write batch — the file lands
    /// once, every real difference publishes exactly one change, and every
    /// subscriber that already reacts to settings changes re-applies live
    /// (the T-053 canon; the no restart the acceptance asks for).
    /// </summary>
    public async Task ApplyImportAsync(SettingsPortImportPlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        await _settings.ApplyBatchAsync(plan.Mutations, ct);
        _logger.LogInformation(
            "Settings import applied: {EntryCount} mutation(s)",
            plan.Mutations.Count);
    }

    /// <summary>
    /// The factory reset: every STORED registry-owned key realigns — a
    /// listed default stamps back, an absent-natural default removes —
    /// while keys the store never wrote stay absent (a fresh install writes
    /// nothing there; their reads already resolve the shipped default
    /// through the read-time fallback), and the secrets store is never
    /// touched — the port has no secret-store dependency, so nothing but
    /// user-adjustable settings can move. The destructive act rides the
    /// typed human confirmation (a decline returns false with nothing
    /// written); a confirmed reset of an already-fresh store is a no-op.
    /// </summary>
    /// <returns>True when the reset ran, false when the user declined.</returns>
    public async Task<bool> ResetToDefaultsAsync(
        IConfirmationService confirmations,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(confirmations);

        var confirmed = await confirmations.RequestConfirmationAsync(
            new ConfirmationRequest { MessageKey = ResetConfirmMessageKey },
            ct);
        if (!confirmed)
        {
            _logger.LogInformation("The factory reset was declined");
            return false;
        }

        var snapshot = await _settings.SnapshotAsync(ct);
        var stored = snapshot.ToDictionary(entry => entry.CompositeKey, entry => entry, StringComparer.Ordinal);

        var mutations = new List<SettingsMutation>();

        foreach (var registryKey in PortSettingsRegistry.KeysList)
        {
            // An entry the fresh-install store never wrote stays absent: its
            // absence already resolves to the shipped default, and the reset
            // never litters a pristine store with entries it did not have.
            if (!stored.TryGetValue(registryKey.CompositeKey, out var current))
            {
                continue;
            }

            if (registryKey.Default is { } stampableDefault)
            {
                if (!JsonElement.DeepEquals(current!.Value, stampableDefault))
                {
                    mutations.Add(new SettingsMutation(registryKey.Section, registryKey.Key, stampableDefault.Clone()));
                }
            }
            else
            {
                mutations.Add(new SettingsMutation(registryKey.Section, registryKey.Key, Value: null));
            }
        }

        await _settings.ApplyBatchAsync(mutations, ct);
        _logger.LogInformation(
            "Factory reset applied: {StampCount} stamp(s), {RemoveCount} removal(s)",
            mutations.Count(mutation => mutation.Value is not null),
            mutations.Count(mutation => mutation.Value is null));
        return true;
    }

    /// <summary>
    /// Parse and shallow-validate the import document: root object, the
    /// port's own schema marker, the exactly-shipped version, and the
    /// settings payload as a section-to-key-to-value map. Returns the parsed
    /// section map; the (null, reason, detail) outcome on any structural
    /// reject — the entry-level allowlist and kind checks ride the caller.
    /// </summary>
    private static (IReadOnlyDictionary<string, Dictionary<string, JsonElement>>? Entries, SettingsPortErrorReason? Reason, string Detail)
        TryParseImport(string jsonText)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(jsonText);
        }
        catch (JsonException exception)
        {
            return (null, SettingsPortErrorReason.MalformedJson, exception.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty(SchemaPropertyName, out var schema) ||
                schema.ValueKind != JsonValueKind.String ||
                !string.Equals(schema.GetString(), SchemaMarker, StringComparison.Ordinal))
            {
                return (null, SettingsPortErrorReason.UnknownSchema, NoDetail);
            }

            if (!root.TryGetProperty(VersionPropertyName, out var version) ||
                version.ValueKind != JsonValueKind.Number ||
                version.GetRawText() != SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                return (null, SettingsPortErrorReason.UnsupportedVersion, NoDetail);
            }

            if (!root.TryGetProperty(SettingsPropertyName, out var settings) ||
                settings.ValueKind != JsonValueKind.Object)
            {
                return (null, SettingsPortErrorReason.MalformedJson, NoDetail);
            }

            var entries = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal);
            foreach (var section in settings.EnumerateObject())
            {
                if (section.Value.ValueKind != JsonValueKind.Object)
                {
                    return (null, SettingsPortErrorReason.MalformedJson, $"section '{section.Name}' is not an object");
                }

                var keys = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var key in section.Value.EnumerateObject())
                {
                    keys[key.Name] = key.Value.Clone();
                }

                entries[section.Name] = keys;
            }

            return (entries, null, NoDetail);
        }
    }
}
