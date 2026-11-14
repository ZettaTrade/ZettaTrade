namespace ZettaTrade.Infrastructure.Backup;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ZettaTrade.Infrastructure.Configuration;

/// <summary>
/// Why a restore was rejected — never coerced, never defaulted: the restore
/// refuses the whole archive instead of touching anything (the settings
/// port's discipline applied to the whole profile).
/// </summary>
public enum ProfileBackupErrorReason
{
    /// <summary>The picked file is not a readable zip archive at all.</summary>
    MalformedArchive,

    /// <summary>The archive does not carry the backup's own schema marker.</summary>
    UnknownSchema,

    /// <summary>The manifest's schema version is absent or not the exactly-shipped one.</summary>
    UnsupportedVersion,

    /// <summary>
    /// The archive's payload does not match its manifest: a listed entry is
    /// missing, a foreign entry rides, a size disagrees, or the payload
    /// changed between the preview and the apply.
    /// </summary>
    ManifestMismatch,

    /// <summary>The archive validates but carries nothing to restore.</summary>
    NothingRestorable,

    /// <summary>
    /// A store the archive would replace is held open (an operation in
    /// flight); the pre-flight check refused before anything was replaced.
    /// </summary>
    LiveStoreLocked,

    /// <summary>The restore's own I/O failed mid-flight; some entries may have landed (each file is whole).</summary>
    RestoreFailed,
}

/// <summary>
/// A rejected restore. The reason rides a typed enum; the message carries the
/// English log detail only — the user-facing text is keyed off the reason
/// through the localization system.
/// </summary>
public sealed class ProfileBackupRestoreException : Exception
{
    /// <summary>Create a rejection with its reason and an English log detail.</summary>
    public ProfileBackupRestoreException(ProfileBackupErrorReason reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>Create a rejection with its reason, an English log detail and the inner fault.</summary>
    public ProfileBackupRestoreException(ProfileBackupErrorReason reason, string message, Exception innerException)
        : base(message, innerException)
    {
        Reason = reason;
    }

    /// <summary>The typed reason the error text is localized from.</summary>
    public ProfileBackupErrorReason Reason { get; }
}

/// <summary>
/// One restorable entry of a validated archive: the archive-internal path,
/// the live profile file it replaces and the archive's byte size (the
/// preview's number).
/// </summary>
/// <param name="ArchivePath">The entry's path inside the archive.</param>
/// <param name="LabelKey">The localization key naming the entry for the user.</param>
/// <param name="LiveFile">The absolute live profile path the entry replaces.</param>
/// <param name="Bytes">The entry's uncompressed byte size.</param>
public sealed record ProfileBackupRestoreRow(
    string ArchivePath,
    string LabelKey,
    string LiveFile,
    long Bytes);

/// <summary>
/// A fully validated restore: the archive opened, the manifest parsed, and
/// every zip entry matched its manifest row. The preview shows
/// <see cref="Rows"/>; the apply re-validates the SAME archive path and
/// replaces exactly what it described (an archive edited between the preview
/// and the apply is refused, never adapted).
/// </summary>
/// <param name="ArchivePath">The absolute archive path the plan was drafted from.</param>
/// <param name="Rows">The restorable entries in manifest order.</param>
public sealed record ProfileBackupRestorePlan(
    string ArchivePath,
    IReadOnlyList<ProfileBackupRestoreRow> Rows);

/// <summary>
/// The seven live profile files the backup knows, in their canonical order:
/// the settings file first, then the six SQLite stores. The record exists so
/// the tests can point the service at a sandbox profile — the production
/// composition resolves <see cref="FromResolver"/>.
/// </summary>
/// <param name="SettingsFile">The main settings file (appsettings.json).</param>
/// <param name="OrdersStoreFile">The order history database.</param>
/// <param name="PnlStoreFile">The realized-trade accounting database.</param>
/// <param name="CandleCacheStoreFile">The candle cache database.</param>
/// <param name="ChartDrawingsStoreFile">The chart drawings database.</param>
/// <param name="ChatHistoryStoreFile">The AI chat history database.</param>
/// <param name="PortfolioValueStoreFile">The portfolio-value history database.</param>
public sealed record ProfileBackupPaths(
    string SettingsFile,
    string OrdersStoreFile,
    string PnlStoreFile,
    string CandleCacheStoreFile,
    string ChartDrawingsStoreFile,
    string ChatHistoryStoreFile,
    string PortfolioValueStoreFile)
{
    /// <summary>The production paths from <see cref="ConfigPathResolver"/>.</summary>
    public static ProfileBackupPaths FromResolver() => new(
        ConfigPathResolver.MainSettingsFile,
        ConfigPathResolver.OrderHistoryDatabaseFile,
        ConfigPathResolver.PnlDatabaseFile,
        ConfigPathResolver.CandleCacheDatabaseFile,
        ConfigPathResolver.ChartDrawingsDatabaseFile,
        ConfigPathResolver.ChatHistoryDatabaseFile,
        ConfigPathResolver.PortfolioValueDatabaseFile);
}

/// <summary>
/// The profile backup (T-215): a one-shot, versioned archive of the user's
/// stored profile — the settings file and the SQLite stores — plus a restore
/// that puts the archive's snapshots back behind a pre-restore summary and a
/// typed human confirmation.
/// Secrets are structurally absent: the archive is built from an EXPLICIT
/// entry list (never a directory walk), the service addresses no
/// <c>ISecretStore</c> — no credential can ever reach an archive, and the
/// encrypted secret file the profiles carry (secrets.enc and its platform
/// siblings) is simply not on the list (documented in SECURITY.md).
/// The versioned shape follows the settings port's: a schema marker plus an
/// exactly-supported version inside the archive, an unparsable or foreign
/// archive refused whole (nothing partial ever lands).
/// </summary>
/// <remarks>
/// The stores' SQLite snapshots ride <c>VACUUM INTO</c>: one consistent,
/// compact snapshot per store even while the app keeps running (the stores
/// open their contexts per operation, so the files are free between
/// operations). The restore replaces each live file atomically
/// (<c>File.Replace</c> from a staging file beside it — same directory, same
/// volume): the settings service's file watcher re-applies the restored
/// settings live, and the stores' next per-operation contexts simply open the
/// restored files. A store held open at restore time fails the pre-flight
/// check before anything is replaced.
/// </remarks>
public sealed class ProfileBackupService
{
    /// <summary>The archive's schema marker (the manifest's "schema" value).</summary>
    public const string SchemaMarker = "zettatrade-profile-backup";

    /// <summary>The exactly-supported manifest version (any other is refused, never adapted).</summary>
    public const int SchemaVersion = 1;

    /// <summary>The localization key of the typed restore confirmation (the destructive gate).</summary>
    public const string RestoreConfirmMessageKey = "Backup.RestoreConfirm";

    /// <summary>The manifest's entry name inside the archive.</summary>
    private const string ManifestEntryName = "manifest.json";

    /// <summary>The manifest's root property names, in their fixed order.</summary>
    private const string SchemaPropertyName = "schema";
    private const string VersionPropertyName = "version";
    private const string CreatedAtPropertyName = "createdAtUtc";
    private const string AppVersionPropertyName = "appVersion";
    private const string EntriesPropertyName = "entries";
    private const string PathPropertyName = "path";
    private const string BytesPropertyName = "bytes";

    /// <summary>The manifest's marker of an absent entry (the value is a JSON null).</summary>
    private const string NoDetail = "";

    private readonly IReadOnlyList<Entry> _entries;

    private readonly ILogger<ProfileBackupService> _logger;

    /// <summary>
    /// Create the service over one profile's paths. The paths default to the
    /// production resolver; the tests point them at a sandbox.
    /// </summary>
    /// <param name="paths">The live profile paths, or null for the production resolver.</param>
    /// <param name="logger">The logger.</param>
    public ProfileBackupService(ProfileBackupPaths? paths = null, ILogger<ProfileBackupService>? logger = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var resolved = paths ?? ProfileBackupPaths.FromResolver();
        _entries =
        [
            new Entry("settings/appsettings.json", resolved.SettingsFile, "Backup.Entry.Settings", IsStore: false),
            new Entry("stores/orders-history.db", resolved.OrdersStoreFile, "Backup.Entry.Orders", IsStore: true),
            new Entry("stores/pnl.db", resolved.PnlStoreFile, "Backup.Entry.Pnl", IsStore: true),
            new Entry("stores/candles.db", resolved.CandleCacheStoreFile, "Backup.Entry.Candles", IsStore: true),
            new Entry("stores/chart-drawings.db", resolved.ChartDrawingsStoreFile, "Backup.Entry.Drawings", IsStore: true),
            new Entry("stores/chat-history.db", resolved.ChatHistoryStoreFile, "Backup.Entry.Chat", IsStore: true),
            new Entry("stores/portfolio-value.db", resolved.PortfolioValueStoreFile, "Backup.Entry.PortfolioValue", IsStore: true),
        ];
    }

    /// <summary>One profile file the archive knows: its archive path, its live file and its label key.</summary>
    /// <param name="ArchivePath">The entry's path inside the archive.</param>
    /// <param name="LiveFile">The absolute live profile path.</param>
    /// <param name="LabelKey">The localization key naming the entry for the user.</param>
    /// <param name="IsStore">Whether the entry is a SQLite store (the backup vacuums it, the restore locks it).</param>
    private sealed record Entry(string ArchivePath, string LiveFile, string LabelKey, bool IsStore);

    #region Backup

    /// <summary>
    /// Write the profile backup archive: every live entry file enters (the
    /// stores as consistent VACUUM INTO snapshots, the settings file copied
    /// byte-exact), absent files simply stay absent, and the manifest records
    /// exactly what entered. The archive is written to a sibling temporary
    /// file and moved into place only when complete — a failed backup leaves
    /// the destination untouched, never a half archive.
    /// </summary>
    /// <param name="archivePath">The absolute destination path.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task BackupToFileAsync(string archivePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            throw new ArgumentException("The profile backup needs a destination path.", nameof(archivePath));
        }

        var stagingDirectory = Path.Combine(
            Path.GetTempPath(),
            "zettatrade-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        var temporaryArchive = archivePath + ".tmp";
        try
        {
            var manifestEntries = new List<(string ArchivePath, long Bytes)>();
            foreach (var entry in _entries)
            {
                if (!File.Exists(entry.LiveFile))
                {
                    continue;
                }

                var stagedPath = Path.Combine(stagingDirectory, Path.GetFileName(entry.ArchivePath));
                if (entry.IsStore)
                {
                    await VacuumStoreIntoAsync(entry.LiveFile, stagedPath, ct).ConfigureAwait(false);
                }
                else
                {
                    File.Copy(entry.LiveFile, stagedPath, overwrite: true);
                }

                manifestEntries.Add((entry.ArchivePath, new FileInfo(stagedPath).Length));
            }

            var manifestPath = Path.Combine(stagingDirectory, ManifestEntryName);
            await File.WriteAllTextAsync(manifestPath, ComposeManifest(manifestEntries), ct).ConfigureAwait(false);

            await using (var archiveStream = new FileStream(
                temporaryArchive,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None))
            await using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create))
            {
                AddArchiveEntry(archive, manifestPath, ManifestEntryName);
                foreach (var (archiveEntryPath, _) in manifestEntries)
                {
                    AddArchiveEntry(
                        archive,
                        Path.Combine(stagingDirectory, Path.GetFileName(archiveEntryPath)),
                        archiveEntryPath);
                }
            }

            File.Move(temporaryArchive, archivePath, overwrite: true);

            _logger.LogInformation(
                "Profile backup written: {EntryCount} entr(ies), {TotalBytes} bytes, at {Path}",
                manifestEntries.Count,
                manifestEntries.Sum(pair => pair.Bytes),
                archivePath);
        }
        finally
        {
            TryCleanupStaging(stagingDirectory);
            TryDeleteFile(temporaryArchive);
        }
    }

    /// <summary>
    /// One store's consistent snapshot: a short-lived connection runs
    /// <c>VACUUM INTO</c> — the compaction writes the snapshot to the target
    /// path while the source is only read (the app keeps running beside it).
    /// The target must not exist (a fresh staging directory guarantees it).
    /// </summary>
    private static async Task VacuumStoreIntoAsync(string liveFile, string snapshotPath, CancellationToken ct)
    {
        // One-shot and un-pooled: the vacuum connection must release the
        // live store file the moment it closes (a pooled handle would hold
        // the file open behind the app's back — the restore's exclusive
        // pre-check would then refuse a profile nothing is actually using).
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = liveFile,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // The path rides a SQL literal (SQLite doubles single quotes; the
        // backslashes a Windows path carries are literal characters there).
        command.CommandText = $"VACUUM INTO '{snapshotPath.Replace("'", "''")}'";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Compose the manifest JSON: the schema marker, the version, the moment, the app version and the entry list.</summary>
    private static string ComposeManifest(IReadOnlyList<(string ArchivePath, long Bytes)> entries)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString(SchemaPropertyName, SchemaMarker);
            writer.WriteNumber(VersionPropertyName, SchemaVersion);
            writer.WriteString(
                CreatedAtPropertyName,
                DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteString(AppVersionPropertyName, ReadAppVersion());
            writer.WriteStartArray(EntriesPropertyName);
            foreach (var (archivePath, bytes) in entries)
            {
                writer.WriteStartObject();
                writer.WriteString(PathPropertyName, archivePath);
                writer.WriteNumber(BytesPropertyName, bytes);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// The running app's informational version (the exe's baked
    /// <c>0.1.0-alpha.N</c>) — informational only: the manifest names the
    /// build that wrote the archive. A missing attribute degrades to
    /// "unknown" (the test host carries none).
    /// </summary>
    private static string ReadAppVersion() =>
        Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion is { } version
            ? version.Split('+')[0]
            : "unknown";

    /// <summary>Add one staged file to the archive under its archive-internal path.</summary>
    private static void AddArchiveEntry(ZipArchive archive, string stagedPath, string archivePath)
    {
        using var source = File.OpenRead(stagedPath);
        using var zipEntry = archive.CreateEntry(archivePath, CompressionLevel.Optimal).Open();
        source.CopyTo(zipEntry);
    }

    #endregion Backup

    #region Restore

    /// <summary>
    /// Validate the archive and build the pre-restore plan — a pure read pass
    /// over the archive, nothing on disk touched yet. Any reject (a foreign
    /// or unreadable archive, a wrong schema or version, a payload that does
    /// not match its manifest) throws the typed exception.
    /// </summary>
    /// <param name="archivePath">The absolute archive path.</param>
    public ProfileBackupRestorePlan DraftRestore(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            throw new ArgumentException("The profile restore needs an archive path.", nameof(archivePath));
        }

        var rows = ValidateArchive(archivePath);
        if (rows.Count == 0)
        {
            throw new ProfileBackupRestoreException(
                ProfileBackupErrorReason.NothingRestorable,
                "Profile restore rejected: the archive carries nothing to restore");
        }

        return new ProfileBackupRestorePlan(archivePath, rows);
    }

    /// <summary>
    /// Apply a validated plan as the typed destructive act: the restore
    /// confirmation gates it (a decline replaces nothing), the archive is
    /// re-validated against the SAME plan (an archive edited between the
    /// preview and the apply is refused), every store the archive would
    /// replace is pre-checked for exclusive access, and each entry lands
    /// atomically (File.Replace from a staging file beside the live one).
    /// The settings file's restore re-applies live through the settings
    /// service's own file watcher; the stores' next per-operation contexts
    /// open the restored files.
    /// </summary>
    /// <param name="plan">The plan the preview showed (the archive path plus its rows).</param>
    /// <param name="confirmations">The human confirmation service (the destructive gate).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>True when the restore ran, false when the user declined.</returns>
    public async Task<bool> RestoreAsync(
        ProfileBackupRestorePlan plan,
        Core.Interfaces.IConfirmationService confirmations,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(confirmations);

        var confirmed = await confirmations.RequestConfirmationAsync(
            new Core.Interfaces.ConfirmationRequest { MessageKey = RestoreConfirmMessageKey },
            ct).ConfigureAwait(false);
        if (!confirmed)
        {
            _logger.LogInformation("The profile restore was declined");
            return false;
        }

        // The re-validation pins the plan: the apply lands exactly what the
        // preview showed, nothing the archive grew into since.
        var revalidated = ValidateArchive(plan.ArchivePath);
        if (revalidated.Count == 0)
        {
            throw new ProfileBackupRestoreException(
                ProfileBackupErrorReason.NothingRestorable,
                "Profile restore rejected: the archive carries nothing to restore");
        }

        if (!PlanMatches(plan, revalidated))
        {
            throw new ProfileBackupRestoreException(
                ProfileBackupErrorReason.ManifestMismatch,
                "Profile restore rejected: the archive changed between the preview and the apply");
        }

        var staging = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            await ExtractToStagingAsync(plan, staging, ct).ConfigureAwait(false);

            // The pre-flight check: every store the restore would replace
            // must be free (a per-operation store is free between operations;
            // a held file means an operation is in flight). It runs BEFORE
            // anything is replaced, so a busy profile replaces nothing.
            PreCheckExclusiveAccess(plan.Rows, staging);

            foreach (var row in plan.Rows)
            {
                var stagedPath = staging[row.ArchivePath];
                if (File.Exists(row.LiveFile))
                {
                    File.Replace(stagedPath, row.LiveFile, null);
                }
                else
                {
                    File.Move(stagedPath, row.LiveFile);
                }
            }

            _logger.LogInformation(
                "Profile restore applied: {EntryCount} entr(ies) replaced from {Path}",
                plan.Rows.Count,
                plan.ArchivePath);
            return true;
        }
        catch (ProfileBackupRestoreException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The profile restore failed mid-flight");
            throw new ProfileBackupRestoreException(
                ProfileBackupErrorReason.RestoreFailed,
                "Profile restore failed: the I/O failed mid-flight",
                ex);
        }
        finally
        {
            foreach (var stagedPath in staging.Values)
            {
                TryDeleteFile(stagedPath);
            }
        }
    }

    /// <summary>Whether the re-validated rows describe the exact plan the preview showed (path + byte pairs).</summary>
    private static bool PlanMatches(ProfileBackupRestorePlan plan, IReadOnlyList<ProfileBackupRestoreRow> revalidated)
    {
        if (plan.Rows.Count != revalidated.Count)
        {
            return false;
        }

        for (var index = 0; index < plan.Rows.Count; index++)
        {
            var planned = plan.Rows[index];
            var actual = revalidated[index];
            if (!string.Equals(planned.ArchivePath, actual.ArchivePath, StringComparison.Ordinal) ||
                planned.Bytes != actual.Bytes)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Extract every planned entry into a staging file beside its live file
    /// (same directory, so the replace is atomic on the same volume) and
    /// verify each landed byte length against the plan.
    /// </summary>
    private async Task ExtractToStagingAsync(
        ProfileBackupRestorePlan plan,
        Dictionary<string, string> staging,
        CancellationToken ct)
    {
        var stagingId = Guid.NewGuid().ToString("N");
        await using (var archiveStream = new FileStream(
            plan.ArchivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read))
        {
            foreach (var row in plan.Rows)
            {
                var zipEntry = archive.GetEntry(row.ArchivePath)
                    ?? throw new ProfileBackupRestoreException(
                        ProfileBackupErrorReason.ManifestMismatch,
                        $"Profile restore rejected: the archive no longer carries '{row.ArchivePath}'");
                var stagedPath = row.LiveFile + ".restore-" + stagingId;
                await using (var entryStream = zipEntry.Open())
                await using (var target = new FileStream(
                    stagedPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None))
                {
                    await entryStream.CopyToAsync(target, ct).ConfigureAwait(false);
                }

                staging[row.ArchivePath] = stagedPath;
            }
        }

        foreach (var row in plan.Rows)
        {
            var landed = new FileInfo(staging[row.ArchivePath]).Length;
            if (landed != row.Bytes)
            {
                throw new ProfileBackupRestoreException(
                    ProfileBackupErrorReason.ManifestMismatch,
                    $"Profile restore rejected: '{row.ArchivePath}' landed {landed} bytes, the manifest said {row.Bytes}");
            }
        }
    }

    /// <summary>
    /// The pre-flight check: open each live store the restore would replace
    /// with exclusive access (a read handle nobody may share). Any held store
    /// refuses the restore before the first replacement — an operation in
    /// flight is the user's signal to retry, never a torn restore.
    /// </summary>
    private void PreCheckExclusiveAccess(IReadOnlyList<ProfileBackupRestoreRow> rows, Dictionary<string, string> staging)
    {
        foreach (var entry in _entries)
        {
            if (!entry.IsStore)
            {
                continue;
            }

            // The plan lists exactly what the archive carries: an entry the
            // archive does not list is not replaced, so its lock state is
            // irrelevant (the pre-flight checks only planned stores).
            var row = rows.FirstOrDefault(candidate =>
                string.Equals(candidate.ArchivePath, entry.ArchivePath, StringComparison.Ordinal));
            if (row is null || !File.Exists(row.LiveFile))
            {
                continue;
            }

            try
            {
                using var probe = new FileStream(
                    row.LiveFile,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(
                    ex,
                    "The profile restore refused: {Store} is held open by an operation",
                    row.LiveFile);
                throw new ProfileBackupRestoreException(
                    ProfileBackupErrorReason.LiveStoreLocked,
                    "Profile restore refused: a store is held open by an operation");
            }
        }
    }

    /// <summary>
    /// The shared validation core: open the archive, parse its manifest, and
    /// cross-check every zip entry against it (the allowlist discipline — a
    /// known archive path set, no traversal, no foreign payload, no size
    /// disagreement). Returns the restorable rows in manifest order.
    /// </summary>
    private IReadOnlyList<ProfileBackupRestoreRow> ValidateArchive(string archivePath)
    {
        FileStream? archiveStream = null;
        ZipArchive archive;
        try
        {
            archiveStream = new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // The half-opened stream dies with the reject (a ZipArchive
            // constructor failure leaves the handle held otherwise).
            archiveStream?.Dispose();
            _logger.LogWarning(ex, "The profile restore rejected {Path}: not a readable archive", archivePath);
            throw new ProfileBackupRestoreException(
                ProfileBackupErrorReason.MalformedArchive,
                "Profile restore rejected: the file is not a readable archive",
                ex);
        }

        using (archive)
        {
            var manifestEntry = archive.GetEntry(ManifestEntryName)
                ?? throw Reject(
                    ProfileBackupErrorReason.MalformedArchive,
                    $"Profile restore rejected: no {ManifestEntryName} inside {archivePath}");

            string manifestText;
            using (var manifestStream = manifestEntry.Open())
            using (var reader = new StreamReader(manifestStream, System.Text.Encoding.UTF8))
            {
                manifestText = reader.ReadToEnd();
            }

            var (knownPaths, reason, detail) = ParseManifest(manifestText);
            if (knownPaths is null)
            {
                throw Reject(reason!.Value, $"Profile restore rejected: {reason}{(detail.Length > 0 ? $" ({detail})" : string.Empty)}");
            }

            // The zip side: every non-directory entry must be the manifest or
            // a manifest-listed path, with the manifest's byte size.
            var zipEntries = archive.Entries
                .Where(candidate => candidate.FullName != ManifestEntryName)
                .ToDictionary(candidate => candidate.FullName, candidate => candidate.Length, StringComparer.Ordinal);

            if (zipEntries.Keys.Any(path => !knownPaths.ContainsKey(path)))
            {
                throw Reject(
                    ProfileBackupErrorReason.ManifestMismatch,
                    "Profile restore rejected: the archive carries an entry the manifest does not list");
            }

            var rows = new List<ProfileBackupRestoreRow>();
            foreach (var (archiveEntryPath, liveFile, labelKey, _) in _entries)
            {
                if (!knownPaths.TryGetValue(archiveEntryPath, out var expectedBytes))
                {
                    continue;
                }

                if (!zipEntries.TryGetValue(archiveEntryPath, out var actualBytes) || actualBytes != expectedBytes)
                {
                    throw Reject(
                        ProfileBackupErrorReason.ManifestMismatch,
                        $"Profile restore rejected: '{archiveEntryPath}' disagrees with its manifest size");
                }

                rows.Add(new ProfileBackupRestoreRow(archiveEntryPath, labelKey, liveFile, expectedBytes));
            }

            return rows;
        }
    }

    /// <summary>Log-and-build one typed rejection (the caller throws it).</summary>
    private ProfileBackupRestoreException Reject(ProfileBackupErrorReason reason, string message)
    {
        _logger.LogWarning("The profile restore was rejected: {Reason} - {Message}", reason, message);
        return new ProfileBackupRestoreException(reason, message);
    }

    /// <summary>
    /// Parse and validate the manifest: the backup's schema marker, the
    /// exactly-shipped version, and the entries as a path-to-bytes map whose
    /// every path is one of the known archive paths (no traversal, no foreign
    /// payload — the allowlist discipline). Returns the known map on success,
    /// the typed (null, reason, detail) outcome on any reject.
    /// </summary>
    private (IReadOnlyDictionary<string, long>? Paths, ProfileBackupErrorReason? Reason, string Detail)
        ParseManifest(string manifestText)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(manifestText);
        }
        catch (JsonException exception)
        {
            return (null, ProfileBackupErrorReason.MalformedArchive, exception.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty(SchemaPropertyName, out var schema) ||
                schema.ValueKind != JsonValueKind.String ||
                !string.Equals(schema.GetString(), SchemaMarker, StringComparison.Ordinal))
            {
                return (null, ProfileBackupErrorReason.UnknownSchema, NoDetail);
            }

            if (!root.TryGetProperty(VersionPropertyName, out var version) ||
                version.ValueKind != JsonValueKind.Number ||
                version.GetRawText() != SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                return (null, ProfileBackupErrorReason.UnsupportedVersion, NoDetail);
            }

            if (!root.TryGetProperty(EntriesPropertyName, out var entries) ||
                entries.ValueKind != JsonValueKind.Array)
            {
                return (null, ProfileBackupErrorReason.MalformedArchive, "the entries are not an array");
            }

            var knownPaths = _entries.ToDictionary(entry => entry.ArchivePath, entry => entry.ArchivePath, StringComparer.Ordinal);
            var paths = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object ||
                    !entry.TryGetProperty(PathPropertyName, out var path) ||
                    path.ValueKind != JsonValueKind.String ||
                    !entry.TryGetProperty(BytesPropertyName, out var bytes) ||
                    bytes.ValueKind != JsonValueKind.Number ||
                    !bytes.TryGetInt64(out var byteCount) ||
                    byteCount < 0)
                {
                    return (null, ProfileBackupErrorReason.MalformedArchive, "an entry is not a path/bytes object");
                }

                var pathValue = path.GetString()!;
                if (!knownPaths.ContainsKey(pathValue))
                {
                    return (
                        null,
                        ProfileBackupErrorReason.ManifestMismatch,
                        $"'{pathValue}' is not a known archive path");
                }

                paths[pathValue] = byteCount;
            }

            return (paths, null, NoDetail);
        }
    }

    #endregion Restore

    #region Cleanup

    /// <summary>Delete one staging leftover; the cleanup never masks the real outcome.</summary>
    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Remove one staging directory (recursive); the cleanup never masks the real outcome.</summary>
    private static void TryCleanupStaging(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    #endregion Cleanup
}
