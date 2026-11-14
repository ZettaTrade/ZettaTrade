namespace ZettaTrade.Infrastructure.Tests.Backup;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Backup;

/// <summary>
/// Tests for the profile backup (T-215): the manifest-plus-snapshots archive
/// round-trip, the absent-entry honesty (a backup lists only what entered, a
/// restore touches only what the archive listed), the no-secrets pin, and
/// every typed refusal with its nothing-replaced outcome (a malformed,
/// foreign, foreign-versioned, manifest-lying, declined, locked or empty
/// archive never rewrites a byte of the live profile).
/// </summary>
public sealed class ProfileBackupServiceTests
{
    #region Test Constants

    /// <summary>The manifest's archive paths, in their canonical order (the service's own list).</summary>
    private static readonly string[] CanonicalStorePaths =
    [
        "stores/orders-history.db",
        "stores/pnl.db",
        "stores/candles.db",
        "stores/chart-drawings.db",
        "stores/chat-history.db",
        "stores/portfolio-value.db",
    ];

    /// <summary>The store file names, in the same order as <see cref="CanonicalStorePaths"/>.</summary>
    private static readonly string[] CanonicalStoreFiles =
    [
        "orders-history.db",
        "pnl.db",
        "candles.db",
        "chart-drawings.db",
        "chat-history.db",
        "portfolio-value.db",
    ];

    /// <summary>The draft rows' label keys, in the canonical order (settings first, then the stores).</summary>
    private static readonly string[] CanonicalLabelKeys =
    [
        "Backup.Entry.Settings",
        "Backup.Entry.Orders",
        "Backup.Entry.Pnl",
        "Backup.Entry.Candles",
        "Backup.Entry.Drawings",
        "Backup.Entry.Chat",
        "Backup.Entry.PortfolioValue",
    ];

    /// <summary>A settings text distinct enough to prove round-trips (the restore swaps it whole).</summary>
    private const string SettingsTextA = "{\"App\":{\"Language\":\"en\",\"TrayClose\":\"MinimizeToTray\"}}";

    private const string SettingsTextB = "{\"App\":{\"Language\":\"ru\",\"TrayClose\":\"Exit\"}}";

    #endregion Test Constants

    #region Tests

    [Fact]
    public async Task Backup_WritesManifestAndSnapshots_ThatRoundTrip()
    {
        await WithTempRootAsync(async root =>
        {
            var sandbox = await CreateProfileAsync(root, "sandbox-a", SettingsTextA, CanonicalStorePaths, rowsPerStore: 3);
            var service = CreateService(sandbox.ToPaths());
            var archive = Path.Combine(root, "backup.ztprofile");

            await service.BackupToFileAsync(archive);

            File.Exists(archive).Should().BeTrue("the archive lands at the destination.");

            using var stream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var entryNames = zip.Entries.Select(candidate => candidate.FullName).ToArray();
            entryNames.Should().Contain("manifest.json", "the manifest is the archive's head entry.");

            using var manifest = ReadManifest(zip);
            manifest.RootElement.TryGetProperty("schema", out var schema).Should().BeTrue();
            schema.GetString().Should().Be(ProfileBackupService.SchemaMarker);
            manifest.RootElement.TryGetProperty("version", out var version).Should().BeTrue();
            version.GetInt32().Should().Be(ProfileBackupService.SchemaVersion);
            manifest.RootElement.TryGetProperty("createdAtUtc", out var createdAt).Should().BeTrue();
            createdAt.GetString().Should().NotBeNullOrEmpty();
            manifest.RootElement.TryGetProperty("appVersion", out _).Should().BeTrue();
            manifest.RootElement.TryGetProperty("entries", out var entries).Should().BeTrue();
            entries.ValueKind.Should().Be(JsonValueKind.Array);
            entries.EnumerateArray().Count().Should().Be(7, "the full profile enters: settings plus six stores.");

            foreach (var name in CanonicalStorePaths.Append("settings/appsettings.json"))
            {
                entryNames.Should().Contain(name);
                zip.GetEntry(name)!.Length.Should().BeGreaterThan(0, "an empty snapshot is not a backup.");
            }

            // The snapshot is a real SQLite database: the marker rows survive the vacuum round-trip.
            var snapshotPath = Path.Combine(root, "orders-snapshot-copy.db");
            using (var snapshotEntry = zip.GetEntry("stores/orders-history.db")!.Open())
            using (var copy = new FileStream(snapshotPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await snapshotEntry.CopyToAsync(copy);
            }

            (await ReadMarkerRowsAsync(snapshotPath)).Should().Be(3, "the vacuum snapshot carries the store's rows.");
        });
    }

    [Fact]
    public async Task Backup_ListsOnlyWhatEntered_AndStaysRepeatable()
    {
        await WithTempRootAsync(async root =>
        {
            // A sparse profile: the settings file plus ONE store (the pnl store).
            var present = new[] { "stores/pnl.db" };
            var sandbox = await CreateProfileAsync(root, "sandbox-a", SettingsTextA, present, rowsPerStore: 1);
            var service = CreateService(sandbox.ToPaths());
            var first = Path.Combine(root, "one.ztprofile");
            var second = Path.Combine(root, "two.ztprofile");

            await service.BackupToFileAsync(first);
            await service.BackupToFileAsync(second);

            var firstEntries = ListArchivePaths(first);
            firstEntries.Should().BeEquivalentTo(
                new[] { "manifest.json", "settings/appsettings.json", "stores/pnl.db" },
                "absent stores simply stay absent.");

            ListArchivePaths(second).Should().BeEquivalentTo(firstEntries, "the entry set is repeatable.");

            // The manifest of the second run lists exactly the same paths.
            using (var stream = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            using (var manifest = ReadManifest(zip))
            {
                var listed = manifest.RootElement.GetProperty("entries")
                    .EnumerateArray()
                    .Select(entry => entry.GetProperty("path").GetString())
                    .ToArray();
                listed.Should().BeEquivalentTo(
                    new[] { "settings/appsettings.json", "stores/pnl.db" },
                    "the manifest records exactly what entered.");
            }
        });
    }

    [Fact]
    public async Task Backup_NeverCarriesSecrets()
    {
        await WithTempRootAsync(async root =>
        {
            var sandbox = await CreateProfileAsync(root, "sandbox-a", SettingsTextA, CanonicalStorePaths, rowsPerStore: 1);
            // The decoy: a secrets file beside the stores. The service's
            // entry list is explicit, so a directory-walk can never sweep it in.
            var secretsFile = Path.Combine(root, "data", "secrets.enc");
            await File.WriteAllTextAsync(secretsFile, "top-secret-bytes");
            var service = CreateService(sandbox.ToPaths());
            var archive = Path.Combine(root, "backup.ztprofile");

            await service.BackupToFileAsync(archive);

            ListArchivePaths(archive).Should().NotContain(
                name => name.Contains("secret", StringComparison.OrdinalIgnoreCase),
                "the secrets file is not on the explicit entry list.");
        });
    }

    [Fact]
    public async Task Restore_RoundTrips_IntoAnotherProfile()
    {
        await WithTempRootAsync(async sourceRoot =>
        {
            var source = await CreateProfileAsync(sourceRoot, "sandbox-a", SettingsTextA, CanonicalStorePaths, rowsPerStore: 3);
            var service = CreateService(source.ToPaths());
            var archive = Path.Combine(sourceRoot, "backup.ztprofile");
            await service.BackupToFileAsync(archive);

            await WithTempRootAsync(async targetRoot =>
            {
                // A live target profile with DIFFERENT content: the restore must replace it wholesale.
                var target = await CreateProfileAsync(targetRoot, "sandbox-b", SettingsTextB, CanonicalStorePaths, rowsPerStore: 1);
                var restoreService = CreateService(target.ToPaths());
                var plan = restoreService.DraftRestore(archive);
                plan.Rows.Select(row => row.LabelKey).Should().Equal(CanonicalLabelKeys, "the preview lists the canonical order.");

                var confirmations = CreateConfirmedGate(true);
                var ran = await restoreService.RestoreAsync(plan, confirmations);

                ran.Should().BeTrue("the confirmed restore ran.");
                await confirmations.Received(1).RequestConfirmationAsync(
                    Arg.Is<ConfirmationRequest>(request => request.MessageKey == ProfileBackupService.RestoreConfirmMessageKey),
                    Arg.Any<CancellationToken>());

                (await File.ReadAllTextAsync(target.SettingsFile)).Should().Be(
                    SettingsTextA,
                    "the settings file is replaced whole.");

                // Every store now carries the SOURCE's rows (3 per store, tagged sandbox-a).
                foreach (var storeFile in CanonicalStorePaths.Select(path => target.StoreFiles[path]))
                {
                    (await ReadMarkerRowsAsync(storeFile)).Should().Be(
                        3,
                        $"the store {Path.GetFileName(storeFile)} carries the archive's rows.");
                }
            });
        });
    }

    [Fact]
    public async Task Restore_CreatesAbsentLiveStore_AndLeavesUntouchedEntriesAlone()
    {
        await WithTempRootAsync(async sourceRoot =>
        {
            // The source carries the settings plus TWO stores — the archive
            // then lists exactly those, and the chat store is absent from it.
            var source = await CreateProfileAsync(
                sourceRoot,
                "sandbox-a",
                SettingsTextA,
                new[] { "stores/orders-history.db", "stores/pnl.db" },
                rowsPerStore: 2);
            var service = CreateService(source.ToPaths());
            var archive = Path.Combine(sourceRoot, "backup.ztprofile");
            await service.BackupToFileAsync(archive);

            await WithTempRootAsync(async targetRoot =>
            {
                // The target carries the settings and ONE store; the pnl
                // store is absent (a fresh install's shape) and the chat
                // store holds local data the archive does not list.
                var target = await CreateProfileAsync(
                    targetRoot,
                    "sandbox-b",
                    SettingsTextB,
                    new[] { "stores/orders-history.db" },
                    rowsPerStore: 1);
                var createdStoreFile = Path.Combine(targetRoot, "data", "pnl.db");
                var untouchedFile = await CreateStoreFileAsync(
                    Path.Combine(targetRoot, "data", "chat-history.db"),
                    "sandbox-b-local",
                    rows: 7);
                var restoreService = CreateService(target.ToPaths());
                var plan = restoreService.DraftRestore(archive);

                var confirmations = CreateConfirmedGate(true);
                var ran = await restoreService.RestoreAsync(plan, confirmations);

                ran.Should().BeTrue("the confirmed restore ran.");
                File.Exists(createdStoreFile).Should().BeTrue("the absent live store is created by the restore.");
                (await ReadMarkerRowsAsync(createdStoreFile)).Should().Be(2, "the created store carries the archive's rows.");

                // The archive does not list the chat store (the source lacked it):
                // the live file survives with its local rows, untouched.
                (await ReadMarkerRowsAsync(untouchedFile)).Should().Be(
                    7,
                    "an entry the archive does not carry is never touched by the restore.");
            });
        });
    }

    [Fact]
    public async Task Restore_MalformedFile_IsRefused()
    {
        await WithTempRootAsync(async root =>
        {
            var sandbox = await CreateProfileAsync(root, "sandbox-a", SettingsTextA, new[] { "stores/pnl.db" }, rowsPerStore: 1);
            var service = CreateService(sandbox.ToPaths());
            var archive = Path.Combine(root, "garbage.ztprofile");
            await File.WriteAllTextAsync(archive, "this is not a zip archive at all");
            var before = await File.ReadAllTextAsync(sandbox.SettingsFile);

            Action act = () => service.DraftRestore(archive);

            act.Should().Throw<ProfileBackupRestoreException>()
                .Which.Reason.Should().Be(ProfileBackupErrorReason.MalformedArchive);
            (await File.ReadAllTextAsync(sandbox.SettingsFile)).Should().Be(before, "a draft never writes.");
        });
    }

    [Fact]
    public async Task Restore_ForeignSchema_IsRefused()
    {
        await WithTempRootAsync(async root =>
        {
            var sandbox = await CreateProfileAsync(root, "sandbox-a", SettingsTextA, new[] { "stores/pnl.db" }, rowsPerStore: 1);
            var service = CreateService(sandbox.ToPaths());
            var archive = await WriteForeignArchiveAsync(
                root,
                schema: "some-other-product-backup",
                version: ProfileBackupService.SchemaVersion,
                entries: [("settings/appsettings.json", 30)]);

            Action act = () => service.DraftRestore(archive);

            act.Should().Throw<ProfileBackupRestoreException>()
                .Which.Reason.Should().Be(ProfileBackupErrorReason.UnknownSchema);
        });
    }

    [Fact]
    public async Task Restore_WrongVersion_IsRefused()
    {
        await WithTempRootAsync(async root =>
        {
            var sandbox = await CreateProfileAsync(root, "sandbox-a", SettingsTextA, new[] { "stores/pnl.db" }, rowsPerStore: 1);
            var service = CreateService(sandbox.ToPaths());
            var archive = await WriteForeignArchiveAsync(
                root,
                schema: ProfileBackupService.SchemaMarker,
                version: 999,
                entries: [("settings/appsettings.json", 30)]);

            Action act = () => service.DraftRestore(archive);

            act.Should().Throw<ProfileBackupRestoreException>()
                .Which.Reason.Should().Be(ProfileBackupErrorReason.UnsupportedVersion);
        });
    }

    [Fact]
    public async Task Restore_TraversalPath_IsRefused()
    {
        await WithTempRootAsync(async root =>
        {
            var sandbox = await CreateProfileAsync(root, "sandbox-a", SettingsTextA, new[] { "stores/pnl.db" }, rowsPerStore: 1);
            var service = CreateService(sandbox.ToPaths());
            var archive = await WriteForeignArchiveAsync(
                root,
                schema: ProfileBackupService.SchemaMarker,
                version: ProfileBackupService.SchemaVersion,
                entries: [("..\\evil.db", 30)]);

            Action act = () => service.DraftRestore(archive);

            act.Should().Throw<ProfileBackupRestoreException>()
                .Which.Reason.Should().Be(ProfileBackupErrorReason.ManifestMismatch, "an unknown manifest path is a manifest mismatch.");
        });
    }

    [Fact]
    public async Task Restore_SizeLyingManifest_IsRefused()
    {
        await WithTempRootAsync(async root =>
        {
            var sandbox = await CreateProfileAsync(root, "sandbox-a", SettingsTextA, new[] { "stores/pnl.db" }, rowsPerStore: 1);
            var service = CreateService(sandbox.ToPaths());
            var archive = await WriteForeignArchiveAsync(
                root,
                schema: ProfileBackupService.SchemaMarker,
                version: ProfileBackupService.SchemaVersion,
                entries: [("settings/appsettings.json", 30)],
                entryPayloads: new Dictionary<string, string>
                {
                    ["settings/appsettings.json"] = "{\"App\":{}}",
                });

            Action act = () => service.DraftRestore(archive);

            act.Should().Throw<ProfileBackupRestoreException>()
                .Which.Reason.Should().Be(ProfileBackupErrorReason.ManifestMismatch, "the manifest's bytes must match the zip's.");
        });
    }

    [Fact]
    public async Task Restore_ForeignZipEntry_IsRefused()
    {
        await WithTempRootAsync(async root =>
        {
            var sandbox = await CreateProfileAsync(root, "sandbox-a", SettingsTextA, new[] { "stores/pnl.db" }, rowsPerStore: 1);
            var service = CreateService(sandbox.ToPaths());
            var archive = await WriteForeignArchiveAsync(
                root,
                schema: ProfileBackupService.SchemaMarker,
                version: ProfileBackupService.SchemaVersion,
                entries: [("settings/appsettings.json", 9)],
                entryPayloads: new Dictionary<string, string>
                {
                    ["settings/appsettings.json"] = "{\"App\":{}}",
                    ["stores/foreign.db"] = "unlisted-payload",
                });

            Action act = () => service.DraftRestore(archive);

            act.Should().Throw<ProfileBackupRestoreException>()
                .Which.Reason.Should().Be(ProfileBackupErrorReason.ManifestMismatch, "the archive carries an entry the manifest does not list.");
        });
    }

    [Fact]
    public async Task Restore_EmptyArchive_IsRefusedAsNothingRestorable()
    {
        await WithTempRootAsync(async root =>
        {
            var sandbox = await CreateProfileAsync(root, "sandbox-a", SettingsTextA, new[] { "stores/pnl.db" }, rowsPerStore: 1);
            var service = CreateService(sandbox.ToPaths());
            var archive = await WriteForeignArchiveAsync(
                root,
                schema: ProfileBackupService.SchemaMarker,
                version: ProfileBackupService.SchemaVersion,
                entries: []);

            Action act = () => service.DraftRestore(archive);

            act.Should().Throw<ProfileBackupRestoreException>()
                .Which.Reason.Should().Be(ProfileBackupErrorReason.NothingRestorable);
        });
    }

    [Fact]
    public async Task Restore_DeclinedConfirmation_WritesNothing()
    {
        await WithTempRootAsync(async sourceRoot =>
        {
            var source = await CreateProfileAsync(sourceRoot, "sandbox-a", SettingsTextA, CanonicalStorePaths, rowsPerStore: 3);
            var service = CreateService(source.ToPaths());
            var archive = Path.Combine(sourceRoot, "backup.ztprofile");
            await service.BackupToFileAsync(archive);

            await WithTempRootAsync(async targetRoot =>
            {
                var target = await CreateProfileAsync(targetRoot, "sandbox-b", SettingsTextB, CanonicalStorePaths, rowsPerStore: 1);
                var restoreService = CreateService(target.ToPaths());
                var plan = restoreService.DraftRestore(archive);

                var confirmations = CreateConfirmedGate(false);
                var ran = await restoreService.RestoreAsync(plan, confirmations);

                ran.Should().BeFalse("a declined restore is an honest no-op.");
                (await File.ReadAllTextAsync(target.SettingsFile)).Should().Be(SettingsTextB, "nothing was replaced.");
                foreach (var storeFile in CanonicalStorePaths.Select(path => target.StoreFiles[path]))
                {
                    (await ReadMarkerRowsAsync(storeFile)).Should().Be(1, "nothing was replaced.");
                }
            });
        });
    }

    [Fact]
    public async Task Restore_LockedStore_IsRefused_BeforeAnythingReplaced()
    {
        await WithTempRootAsync(async sourceRoot =>
        {
            var source = await CreateProfileAsync(sourceRoot, "sandbox-a", SettingsTextA, CanonicalStorePaths, rowsPerStore: 3);
            var service = CreateService(source.ToPaths());
            var archive = Path.Combine(sourceRoot, "backup.ztprofile");
            await service.BackupToFileAsync(archive);

            await WithTempRootAsync(async targetRoot =>
            {
                var target = await CreateProfileAsync(targetRoot, "sandbox-b", SettingsTextB, CanonicalStorePaths, rowsPerStore: 1);
                var restoreService = CreateService(target.ToPaths());
                var plan = restoreService.DraftRestore(archive);

                var confirmations = CreateConfirmedGate(true);

                // One live store is held open: an operation is in flight.
                await using (var held = new FileStream(
                    target.StoreFiles["stores/pnl.db"],
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.None))
                {
                    Func<Task> act = async () => await restoreService.RestoreAsync(plan, confirmations);

                    (await act.Should().ThrowAsync<ProfileBackupRestoreException>())
                        .Which.Reason.Should().Be(ProfileBackupErrorReason.LiveStoreLocked);
                }

                (await File.ReadAllTextAsync(target.SettingsFile)).Should().Be(
                    SettingsTextB,
                    "the pre-flight refused before the first replacement.");
                foreach (var storeFile in CanonicalStorePaths.Select(path => target.StoreFiles[path]))
                {
                    (await ReadMarkerRowsAsync(storeFile)).Should().Be(1, "the pre-flight refused before the first replacement.");
                }
            });
        });
    }

    [Fact]
    public async Task Restore_ArchiveEditedBetweenPreviewAndApply_IsRefused()
    {
        await WithTempRootAsync(async sourceRoot =>
        {
            var source = await CreateProfileAsync(sourceRoot, "sandbox-a", SettingsTextA, new[] { "stores/pnl.db" }, rowsPerStore: 2);
            var service = CreateService(source.ToPaths());
            var archive = Path.Combine(sourceRoot, "backup.ztprofile");
            await service.BackupToFileAsync(archive);

            await WithTempRootAsync(async targetRoot =>
            {
                var target = await CreateProfileAsync(targetRoot, "sandbox-b", SettingsTextB, new[] { "stores/pnl.db" }, rowsPerStore: 1);
                var restoreService = CreateService(target.ToPaths());
                var plan = restoreService.DraftRestore(archive);

                // The archive's content changes after the preview: the
                // source's settings file gains different bytes and a fresh
                // backup rewrites the SAME archive path (the vacuum
                // snapshots of identical content stay byte-identical, so the
                // settings entry is the honest differ).
                await File.WriteAllTextAsync(source.SettingsFile, "{\"App\":{\"Language\":\"de\"}}");
                await service.BackupToFileAsync(archive);

                var confirmations = CreateConfirmedGate(true);
                Func<Task> act = async () => await restoreService.RestoreAsync(plan, confirmations);

                (await act.Should().ThrowAsync<ProfileBackupRestoreException>())
                    .Which.Reason.Should().Be(ProfileBackupErrorReason.ManifestMismatch, "the apply re-validates the SAME plan.");
                (await File.ReadAllTextAsync(target.SettingsFile)).Should().Be(SettingsTextB, "nothing was replaced.");
            });
        });
    }

    #endregion Tests

    #region Helpers

    private static ProfileBackupService CreateService(ProfileBackupPaths paths) => new(
        paths,
        NullLogger<ProfileBackupService>.Instance);

    /// <summary>The confirmed/unconfirmed typed gate the restore rides.</summary>
    private static IConfirmationService CreateConfirmedGate(bool confirmed)
    {
        var gate = Substitute.For<IConfirmationService>();
        gate.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(confirmed);
        return gate;
    }

    /// <summary>Create and clean one sandbox root (config + data directories).</summary>
    private static async Task WithTempRootAsync(Func<string, Task> body)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"zettatrade-backup-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "config"));
        Directory.CreateDirectory(Path.Combine(root, "data"));
        try
        {
            await body(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Populate one sandbox: the settings text plus the chosen store files
    /// (each carrying <paramref name="rowsPerStore"/> tagged rows). The
    /// sandbox record lists which stores exist and where everything lives.
    /// </summary>
    private static async Task<ProfileSandbox> CreateProfileAsync(
        string root,
        string tag,
        string settingsText,
        string[] presentStorePaths,
        int rowsPerStore)
    {
        var settingsFile = Path.Combine(root, "config", "appsettings.json");
        await File.WriteAllTextAsync(settingsFile, settingsText);

        var storeFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (archivePath, fileName) in CanonicalStorePaths.Zip(CanonicalStoreFiles, (path, file) => (path, file)))
        {
            if (!presentStorePaths.Contains(archivePath, StringComparer.Ordinal))
            {
                continue;
            }

            storeFiles[archivePath] = await CreateStoreFileAsync(
                Path.Combine(root, "data", fileName),
                tag,
                rowsPerStore);
        }

        return new ProfileSandbox(settingsFile, storeFiles, root);
    }

    /// <summary>Create one real SQLite store file carrying tagged marker rows.</summary>
    private static async Task<string> CreateStoreFileAsync(string storeFile, string tag, int rows)
    {
        // Pooling=false: after the connection closes, its SQLite file unlocks
        // (the sandbox cleanup deletes the whole tree).
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = storeFile, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE IF NOT EXISTS marker (id INTEGER PRIMARY KEY, note TEXT);" +
            $"INSERT INTO marker (note) VALUES {string.Join(",", Enumerable.Range(1, rows).Select(_ => $"('{tag}')"))}";
        await command.ExecuteNonQueryAsync();
        return storeFile;
    }

    /// <summary>Count the marker rows of one store file (the round-trip probe).</summary>
    private static async Task<int> ReadMarkerRowsAsync(string storeFile)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = storeFile, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM marker";
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    /// <summary>Parse the archive's manifest.json (the caller disposes the document).</summary>
    private static JsonDocument ReadManifest(ZipArchive zip)
    {
        using var stream = zip.GetEntry("manifest.json")!.Open();
        return JsonDocument.Parse(stream);
    }

    /// <summary>List one archive's entry names (a fresh read-only open, disposed inside).</summary>
    private static string[] ListArchivePaths(string archivePath)
    {
        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        return zip.Entries.Select(candidate => candidate.FullName).ToArray();
    }

    /// <summary>
    /// Write a hand-built archive for the refusal battery: a manifest with the
    /// caller's schema/version/entries plus the (optional) matching payloads.
    /// </summary>
    private static async Task<string> WriteForeignArchiveAsync(
        string root,
        string schema,
        int version,
        (string Path, int Bytes)[] entries,
        Dictionary<string, string>? entryPayloads = null)
    {
        var archivePath = Path.Combine(root, $"foreign-{Guid.NewGuid():N}.ztprofile");
        await using (var stream = new FileStream(
            archivePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var manifest = JsonSerializer.Serialize(new
            {
                schema,
                version,
                createdAtUtc = "2026-11-14T00:00:00.0000000Z",
                appVersion = "test",
                entries = entries.Select(candidate => new { path = candidate.Path, bytes = candidate.Bytes }),
            });
            using (var manifestEntry = zip.CreateEntry("manifest.json").Open())
            using (var writer = new StreamWriter(manifestEntry))
            {
                await writer.WriteAsync(manifest);
            }

            foreach (var payload in entryPayloads ?? new Dictionary<string, string>())
            {
                using var entry = zip.CreateEntry(payload.Key).Open();
                using (var writer = new StreamWriter(entry))
                {
                    await writer.WriteAsync(payload.Value);
                }
            }
        }

        return archivePath;
    }

    /// <summary>One populated sandbox profile: its settings file and the stores that exist (by archive path).</summary>
    private sealed record ProfileSandbox(
        string SettingsFile,
        IReadOnlyDictionary<string, string> StoreFiles,
        string Root)
    {
        /// <summary>The profile paths the service consumes (absent stores map to their canonical file names).</summary>
        public ProfileBackupPaths ToPaths()
        {
            var dataDirectory = Path.Combine(Root, "data");
            return new ProfileBackupPaths(
                SettingsFile,
                ResolveStore("stores/orders-history.db", dataDirectory),
                ResolveStore("stores/pnl.db", dataDirectory),
                ResolveStore("stores/candles.db", dataDirectory),
                ResolveStore("stores/chart-drawings.db", dataDirectory),
                ResolveStore("stores/chat-history.db", dataDirectory),
                ResolveStore("stores/portfolio-value.db", dataDirectory));

            string ResolveStore(string archivePath, string data) =>
                StoreFiles.TryGetValue(archivePath, out var file)
                    ? file
                    : Path.Combine(data, Path.GetFileName(archivePath));
        }
    }

    #endregion Helpers
}
