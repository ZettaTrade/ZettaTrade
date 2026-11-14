namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Livery.Localization;
using Livery.Themes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.AI.Mcp;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Charting;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Backup;
using ZettaTrade.Infrastructure.Configuration;
using ZettaTrade.Infrastructure.Shell;

/// <summary>The half-day-agnostic status byte ReactiveUI.Primitives names RxVoid.</summary>
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// The settings window's profile backup section (T-215) over a real service
/// and a real sandbox profile: a validated archive drafts a pre-localized
/// preview that unlocks the apply, a rejected archive surfaces the localized
/// honesty row and touches nothing, the apply runs behind the typed human
/// confirmation (a decline writes nothing), and the backup reports its
/// destination.
/// </summary>
public sealed class SettingsViewModelBackupTests
{
    #region The section's honest posture

    /// <summary>Without the backup surface the section simply does not exist (the honest DI posture).</summary>
    [Fact]
    public void WithoutTheBackupSurface_TheSectionStaysAway()
    {
        using var kit = new SettingsViewModelBackupKit(withBackup: false);

        kit.ViewModel.IsBackupAvailable.Should().BeFalse();
        kit.ViewModel.IsRestorePreviewVisible.Should().BeFalse();
        kit.ViewModel.HasRestoreError.Should().BeFalse();
        kit.ViewModel.HasBackupStatusText.Should().BeFalse();
    }

    /// <summary>With the service and the confirmation injected, the section exists.</summary>
    [Fact]
    public void WithTheBackupSurface_TheSectionIsAvailable()
    {
        using var kit = new SettingsViewModelBackupKit(withBackup: true);

        kit.ViewModel.IsBackupAvailable.Should().BeTrue();
    }

    #endregion The section's honest posture

    #region The backup

    /// <summary>The backup writes the archive at the picked destination and reports it.</summary>
    [Fact]
    public async Task TheBackup_WritesTheArchive_AndReportsItsDestination()
    {
        using var kit = new SettingsViewModelBackupKit(withBackup: true);

        await kit.ViewModel.BackupToFileAsync(kit.ArchivePath);

        File.Exists(kit.ArchivePath).Should().BeTrue("the archive lands at the picked destination.");
        kit.ViewModel.BackupStatusText.Should().Be("Backup.Status.BackupDone");
        kit.ViewModel.HasBackupStatusText.Should().BeTrue();
    }

    #endregion The backup

    #region The draft: the pre-localized preview

    /// <summary>
    /// A validated archive drafts its pre-localized preview rows and unlocks
    /// the apply, and the live profile stays behind until the user's apply.
    /// </summary>
    [Fact]
    public async Task AValidRestoreDraft_FillsThePreview_UnlocksTheApply_AndWritesNothingYet()
    {
        using var kit = new SettingsViewModelBackupKit(withBackup: true);
        await kit.ViewModel.BackupToFileAsync(kit.ArchivePath);
        File.WriteAllText(kit.Paths.SettingsFile, "{\"App\":{\"Mutated\":\"yes\"}}");

        await kit.ViewModel.DraftRestoreFromFileAsync(kit.ArchivePath);

        kit.ViewModel.RestorePreview.Select(row => row.Label).Should().Equal(
        [
            "Backup.Entry.Settings",
            "Backup.Entry.Orders",
            "Backup.Entry.Pnl",
            "Backup.Entry.Candles",
            "Backup.Entry.Drawings",
            "Backup.Entry.Chat",
            "Backup.Entry.PortfolioValue",
        ]);
        kit.ViewModel.RestorePreview.Select(row => row.SizeText).Should().OnlyContain(
            text => text == "Backup.Row.Bytes",
            "the kit's localization answers with the keys themselves.");
        kit.ViewModel.IsRestorePreviewVisible.Should().BeTrue("the plan is pending the user's apply");
        kit.ViewModel.HasRestoreError.Should().BeFalse();
        (await kit.ViewModel.ApplyRestoreCommand.CanExecute.FirstAsync())
            .Should().BeTrue("a pending plan unlocks the apply");

        File.ReadAllText(kit.Paths.SettingsFile).Should().Contain(
            "Mutated",
            "the draft writes nothing before the apply");
    }

    /// <summary>
    /// A rejected draft surfaces the localized honesty row with the typed
    /// reason's key, keeps the preview away and the apply locked.
    /// </summary>
    [Fact]
    public async Task ARejectedRestoreDraft_AnswersTheHonestyRow_AndLocksTheApply()
    {
        using var kit = new SettingsViewModelBackupKit(withBackup: true);

        await kit.ViewModel.DraftRestoreFromFileAsync(
            Path.Combine(kit.Directory, "no-such-archive.ztprofile"));

        kit.ViewModel.HasRestoreError.Should().BeTrue("the malformed archive is refused.");
        kit.ViewModel.RestoreErrorText.Should().Be("Backup.Error.Malformed");
        kit.ViewModel.IsRestorePreviewVisible.Should().BeFalse();
        (await kit.ViewModel.ApplyRestoreCommand.CanExecute.FirstAsync())
            .Should().BeFalse("a rejected draft locks the apply");
    }

    #endregion The draft: the pre-localized preview

    #region The apply: the confirmed destructive batch

    /// <summary>
    /// The confirmed apply restores the archive over the mutated profile and
    /// reports the done line; the restored content is live.
    /// </summary>
    [Fact]
    public async Task TheConfirmedApply_RestoresTheArchive_AndReportsDone()
    {
        using var kit = new SettingsViewModelBackupKit(withBackup: true);
        kit.Confirmations
            .RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(true);
        await kit.ViewModel.BackupToFileAsync(kit.ArchivePath);
        File.WriteAllText(kit.Paths.SettingsFile, "{\"App\":{\"Mutated\":\"yes\"}}");
        kit.RewriteStoreFile("stores/pnl.db", "mutated-rows", rows: 5);

        await kit.ViewModel.DraftRestoreFromFileAsync(kit.ArchivePath);
        await kit.ViewModel.ApplyRestoreCommand.Execute(RxVoid.Default).ToTask();

        kit.ViewModel.BackupStatusText.Should().Be("Backup.Status.RestoreDone");
        File.ReadAllText(kit.Paths.SettingsFile).Should().Be(
            SettingsViewModelBackupKit.SeededSettingsText,
            "the confirmed restore replaced the mutated settings file.");
        (await kit.ReadStoreRowsAsync("stores/pnl.db")).Should().Be(
            SettingsViewModelBackupKit.SeededStoreRows,
            "the confirmed restore replaced the mutated store.");
        kit.ViewModel.IsRestorePreviewVisible.Should().BeFalse("the plan died after applying");
    }

    /// <summary>
    /// A declined apply writes nothing and answers the honest declined line.
    /// </summary>
    [Fact]
    public async Task TheDeclinedApply_WritesNothing_AndAnswersTheDeclinedLine()
    {
        using var kit = new SettingsViewModelBackupKit(withBackup: true);
        kit.Confirmations
            .RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(false);
        await kit.ViewModel.BackupToFileAsync(kit.ArchivePath);
        File.WriteAllText(kit.Paths.SettingsFile, "{\"App\":{\"Mutated\":\"yes\"}}");

        await kit.ViewModel.DraftRestoreFromFileAsync(kit.ArchivePath);
        await kit.ViewModel.ApplyRestoreCommand.Execute(RxVoid.Default).ToTask();

        kit.ViewModel.BackupStatusText.Should().Be("Backup.Status.RestoreDeclined");
        File.ReadAllText(kit.Paths.SettingsFile).Should().Be(
            "{\"App\":{\"Mutated\":\"yes\"}}",
            "a declined restore is an honest no-op.");
    }

    #endregion The apply: the confirmed destructive batch
}

/// <summary>
/// One wired backup-tab kit (T-215): a real store over a fresh temp
/// directory (its own settings file and its six sandbox stores), the real
/// profile backup over them, substitute managers and dialogs, and a
/// localization whose answers are the keys themselves (every outcome row is
/// assertable by its key).
/// </summary>
internal sealed class SettingsViewModelBackupKit : IDisposable
{
    /// <summary>The settings text the kit seeds (the restore round-trip proves it returns).</summary>
    public const string SeededSettingsText = "{\"App\":{\"Language\":\"en\",\"Seeded\":\"kit\"}}";

    /// <summary>The rows every sandbox store carries (the store round-trip probe).</summary>
    public const int SeededStoreRows = 2;

    /// <summary>The canonical store file names, by archive path.</summary>
    private static readonly (string ArchivePath, string FileName)[] StoreFiles =
    [
        ("stores/orders-history.db", "orders-history.db"),
        ("stores/pnl.db", "pnl.db"),
        ("stores/candles.db", "candles.db"),
        ("stores/chart-drawings.db", "chart-drawings.db"),
        ("stores/chat-history.db", "chat-history.db"),
        ("stores/portfolio-value.db", "portfolio-value.db"),
    ];

    /// <summary>Create the kit: the sandbox profile, the backup service and the ViewModel over them.</summary>
    public SettingsViewModelBackupKit(bool withBackup)
    {
        Directory = Path.Combine(Path.GetTempPath(), "t215-vm-" + Guid.NewGuid().ToString("N"));
        DataDirectory = Path.Combine(Directory, "data");
        System.IO.Directory.CreateDirectory(DataDirectory);

        Paths = new ProfileBackupPaths(
            Path.Combine(Directory, "settings.json"),
            Path.Combine(DataDirectory, "orders-history.db"),
            Path.Combine(DataDirectory, "pnl.db"),
            Path.Combine(DataDirectory, "candles.db"),
            Path.Combine(DataDirectory, "chart-drawings.db"),
            Path.Combine(DataDirectory, "chat-history.db"),
            Path.Combine(DataDirectory, "portfolio-value.db"));
        Backup = new ProfileBackupService(Paths, NullLogger<ProfileBackupService>.Instance);
        Confirmations = Substitute.For<IConfirmationService>();

        File.WriteAllText(Paths.SettingsFile, SeededSettingsText);
        foreach (var store in StoreFiles)
        {
            CreateStoreFile(store.ArchivePath, "kit-seed", SeededStoreRows);
        }

        Settings = new SettingsService(
            Path.Combine(Directory, "settings.json"),
            NullLogger<SettingsService>.Instance);

        var themeManager = Substitute.For<IThemeManager>();
        themeManager.CurrentTheme.Returns("Dark");
        themeManager.AvailableThemes.Returns([new ThemeInfo("dark", "Dark", true, [])]);
        var localization = Substitute.For<ILocalizationManager>();
        localization.CurrentLanguage.Returns("en");
        localization.AvailableLanguages.Returns([new LanguageInfo("en", "English", "English", true)]);
        localization.GetString(Arg.Any<string>()).Returns(callInfo => callInfo.ArgAt<string>(0));
        localization.GetString(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(callInfo => callInfo.ArgAt<string>(0));

        var adapter = Substitute.For<IMarketDataAdapter>();
        adapter.Id.Returns("demo");
        var activeExchanges = new ActiveExchangeService(
            [adapter],
            Substitute.For<ISettingsService>(),
            NullLogger<ActiveExchangeService>.Instance);

        var clients = Substitute.For<IMcpClientManager>();
        clients.StatesChanged.Returns(Observable.Empty<IReadOnlyList<McpClientState>>());

        ViewModel = new SettingsViewModel(
            Settings,
            themeManager,
            localization,
            Substitute.For<IShellUtils>(),
            new McpHostService(
                Substitute.For<ISettingsService>(),
                Substitute.For<IMcpServerHost>(),
                NullLogger<McpHostService>.Instance),
            activeExchanges,
            new SymbolListProvider(activeExchanges, NullLogger<SymbolListProvider>.Instance),
            Substitute.For<IIndicatorCatalog>(),
            clients,
            NullLogger<SettingsViewModel>.Instance,
            settingsPort: null,
            confirmations: withBackup ? Confirmations : null,
            profileBackup: withBackup ? Backup : null);
    }

    /// <summary>The temp directory the sandbox profile lives in (removed on dispose).</summary>
    public string Directory { get; }

    /// <summary>The sandbox's data directory (the stores live here).</summary>
    public string DataDirectory { get; }

    /// <summary>The sandbox's profile paths (the service and the VM ride them).</summary>
    public ProfileBackupPaths Paths { get; }

    /// <summary>The real profile backup service.</summary>
    public ProfileBackupService Backup { get; }

    /// <summary>The typed confirmation substitute the apply gates itself behind.</summary>
    public IConfirmationService Confirmations { get; }

    /// <summary>The real settings store the kit's settings file belongs to.</summary>
    public SettingsService Settings { get; }

    /// <summary>The ViewModel under test.</summary>
    public SettingsViewModel ViewModel { get; }

    /// <summary>The archive path the tests back up to and restore from.</summary>
    public string ArchivePath => Path.Combine(Directory, "kit-backup.ztprofile");

    /// <summary>Overwrite one sandbox store with fresh tagged content (the mutation the restore undoes).</summary>
    public void RewriteStoreFile(string archivePath, string tag, int rows)
    {
        File.Delete(ResolveStoreFile(archivePath));
        CreateStoreFile(archivePath, tag, rows);
    }

    /// <summary>Count the marker rows of one sandbox store (the store round-trip probe).</summary>
    public async Task<int> ReadStoreRowsAsync(string archivePath)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = ResolveStoreFile(archivePath),
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM marker";
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    /// <summary>Stop the store's watcher and remove the temp directory.</summary>
    public void Dispose()
    {
        Settings.Dispose();
        System.IO.Directory.Delete(Directory, recursive: true);
    }

    /// <summary>Create one real SQLite store file carrying tagged marker rows (one-shot, un-pooled).</summary>
    private void CreateStoreFile(string archivePath, string tag, int rows)
    {
        var storeFile = ResolveStoreFile(archivePath);
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = storeFile, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE IF NOT EXISTS marker (id INTEGER PRIMARY KEY, note TEXT);" +
            $"INSERT INTO marker (note) VALUES {string.Join(",", Enumerable.Range(1, rows).Select(_ => $"('{tag}')"))}";
        command.ExecuteNonQuery();
    }

    private string ResolveStoreFile(string archivePath) =>
        Path.Combine(DataDirectory, Path.GetFileName(archivePath));
}
