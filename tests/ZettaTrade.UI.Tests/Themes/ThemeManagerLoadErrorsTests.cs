namespace ZettaTrade.UI.Tests.Themes;

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;
using ZettaTrade.UI.Tests.Support;
using ZettaTrade.UI.Themes;

/// <summary>
/// Tests for the ThemeManager diagnostics surface: theme files whose load
/// failed are listed with their path and reason, a later successful load of
/// the same file clears its entry (and only its entry), a missing file
/// records "read failed", and the error stream replays its current snapshot
/// on subscribe.
/// </summary>
public sealed class ThemeManagerLoadErrorsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [Fact]
    public async Task Initialize_WithBrokenFiles_ListsPathAndReasonAndPublishes()
    {
        using var builtInDir = new TempDirectory();
        using var userDir = new TempDirectory();
        File.WriteAllText(Path.Combine(userDir.FolderPath, "bad.json"), "{ not valid json");
        File.WriteAllText(Path.Combine(userDir.FolderPath, "nameless.json"), "{}");
        var manager = CreateManager(builtInDir, userDir);
        var snapshots = new List<IReadOnlyList<ThemeFileLoadError>>();
        using var subscription = manager.LoadErrorsChanged.Subscribe(snapshots.Add);

        await manager.InitializeAsync();

        manager.LoadErrors.Should().HaveCount(2);
        manager.LoadErrors.Should()
            .Contain(e => e.FileName == "bad.json" && e.Reason == ThemeFileLoadError.InvalidJson);
        manager.LoadErrors.Should()
            .Contain(e => e.FileName == "nameless.json" && e.Reason == ThemeFileLoadError.MissingName);
        snapshots.Should().HaveCount(3); // empty replay + one publish per broken file
        snapshots[^1].Should().HaveCount(2, "the last snapshot must hold all broken files");
    }

    [Fact]
    public async Task SuccessfulReloadOfSameFile_ClearsOnlyItsError()
    {
        using var builtInDir = new TempDirectory();
        using var userDir = new TempDirectory();
        var badFile = Path.Combine(userDir.FolderPath, "bad.json");
        var otherFile = Path.Combine(userDir.FolderPath, "other.json");
        File.WriteAllText(badFile, "{ not valid json");
        File.WriteAllText(otherFile, "{}");
        var manager = CreateManager(builtInDir, userDir);
        await manager.InitializeAsync();

        manager.LoadErrors.Should().HaveCount(2);

        File.WriteAllText(badFile, ThemeJson("bad", "Bad Fixed"));
        await manager.LoadCustomThemeAsync(badFile);

        manager.LoadErrors.Should().HaveCount(1, "the fixed file must clear its own error");
        manager.LoadErrors.Should().ContainSingle().Which.FileName.Should().Be("other.json");
        manager.AvailableThemes.Should().Contain(t => t.Name == "bad" && t.DisplayName == "Bad Fixed");
    }

    [Fact]
    public async Task LoadingMissingFile_RecordsReadFailed()
    {
        using var builtInDir = new TempDirectory();
        using var userDir = new TempDirectory();
        var manager = CreateManager(builtInDir, userDir);
        await manager.InitializeAsync();

        await manager.LoadCustomThemeAsync(Path.Combine(userDir.FolderPath, "missing.json"));

        manager.LoadErrors.Should().ContainSingle().Which.FileName.Should().Be("missing.json");
        manager.LoadErrors.Should().ContainSingle().Which.Reason.Should().Be(ThemeFileLoadError.ReadFailed);
    }

    [Fact]
    public async Task LoadErrorsChanged_ReplaysCurrentSnapshotOnSubscribe()
    {
        using var builtInDir = new TempDirectory();
        using var userDir = new TempDirectory();
        File.WriteAllText(Path.Combine(userDir.FolderPath, "bad.json"), "{ not valid json");
        var manager = CreateManager(builtInDir, userDir);
        await manager.InitializeAsync();
        var snapshots = new List<IReadOnlyList<ThemeFileLoadError>>();

        using var subscription = manager.LoadErrorsChanged.Subscribe(snapshots.Add);

        var snapshot = snapshots.Should().ContainSingle().Which;
        snapshot.Should().ContainSingle().Which.FileName.Should().Be("bad.json");
    }

    private static ThemeManager CreateManager(TempDirectory builtInDir, TempDirectory userDir) =>
        new(Substitute.For<ILogger>(), builtInDir.FolderPath, userDir.FolderPath);

    private static string ThemeJson(
        string name,
        string displayName) => JsonSerializer.Serialize(new ThemeDefinition
        {
            Name = name,
            DisplayName = displayName,
            BaseTheme = "Dark",
            Colors = new Dictionary<string, string>
            {
                ["Background"] = "#101010",
                ["TextPrimary"] = "#ffffff",
            },
            PreviewColors = ["#101010"],
        }, JsonOptions);
}
