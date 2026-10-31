namespace ZettaTrade.UI.Tests.Themes;

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;
using ZettaTrade.UI.Tests.Support;
using ZettaTrade.UI.Themes;

/// <summary>
/// Tests for theme hot-reload: both themes directories are watched after
/// initialization; a saved file replaces its registration in place and, when
/// it belongs to the active theme, is re-applied live. Deletions never remove
/// a registration and a malformed file must not stop the pipeline.
/// </summary>
public sealed class ThemeManagerHotReloadTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SettleWait = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task ModifiedActiveThemeFile_ReplacesRegistrationAndRepublishes()
    {
        using var builtInDir = new TempDirectory();
        using var userDir = new TempDirectory();
        var themeFile = Path.Combine(builtInDir.FolderPath, "night-shade.json");
        WriteThemeFile(themeFile, "night-shade", "Night Shade");
        var manager = CreateManager(builtInDir, userDir);
        var published = new List<string>();
        using var subscription = manager.ThemeChanged.Subscribe(published.Add);

        await manager.InitializeAsync();
        await manager.ApplyThemeAsync("night-shade");
        published.Should().Contain("night-shade");
        var appliedCount = published.Count;

        File.WriteAllText(themeFile, ThemeJson("night-shade", "Night Shade Bright"));

        (await WaitUntilAsync(() =>
            manager.AvailableThemes.Any(t =>
                t.Name == "night-shade" &&
                t.DisplayName == "Night Shade Bright")))
            .Should().BeTrue("a saved file must replace its registration in place");

        (await WaitUntilAsync(() => published.Count > appliedCount))
            .Should().BeTrue("hot-reloading the active theme must re-apply it");
        published.Skip(appliedCount).Should().Contain("night-shade");
    }

    [Fact]
    public async Task CreatedThemeFileInUserDirectory_RegistersNewTheme()
    {
        using var builtInDir = new TempDirectory();
        using var userDir = new TempDirectory();
        WriteThemeFile(Path.Combine(builtInDir.FolderPath, "night-shade.json"), "night-shade", "Night Shade");
        var manager = CreateManager(builtInDir, userDir);

        await manager.InitializeAsync();

        WriteThemeFile(Path.Combine(userDir.FolderPath, "aurora.json"), "aurora", "Aurora");
        (await WaitUntilAsync(() => manager.AvailableThemes.Any(t => t.Name == "aurora")))
            .Should().BeTrue("a new user theme file must register without a restart");
    }

    [Fact]
    public async Task DeletedThemeFile_KeepsLastLoadedRegistration()
    {
        using var builtInDir = new TempDirectory();
        using var userDir = new TempDirectory();
        var themeFile = Path.Combine(builtInDir.FolderPath, "night-shade.json");
        WriteThemeFile(themeFile, "night-shade", "Night Shade");
        var manager = CreateManager(builtInDir, userDir);

        await manager.InitializeAsync();
        File.Delete(themeFile);
        await Task.Delay(SettleWait);

        manager.AvailableThemes.Should()
            .ContainSingle(t => t.Name == "night-shade")
            .Which.DisplayName.Should().Be("Night Shade");
    }

    [Fact]
    public async Task MalformedThemeFile_KeepsRegistrationAndPipelineAlive()
    {
        using var builtInDir = new TempDirectory();
        using var userDir = new TempDirectory();
        var themeFile = Path.Combine(builtInDir.FolderPath, "night-shade.json");
        WriteThemeFile(themeFile, "night-shade", "Night Shade");
        var manager = CreateManager(builtInDir, userDir);

        await manager.InitializeAsync();

        File.WriteAllText(themeFile, "{ not valid json");
        await Task.Delay(SettleWait);
        manager.AvailableThemes.Should()
            .ContainSingle(t => t.Name == "night-shade")
            .Which.DisplayName.Should().Be("Night Shade");

        // The hot-reload pipeline must survive the malformed file.
        WriteThemeFile(Path.Combine(builtInDir.FolderPath, "aurora.json"), "aurora", "Aurora");
        (await WaitUntilAsync(() => manager.AvailableThemes.Any(t => t.Name == "aurora")))
            .Should().BeTrue("the watcher must recognize later files after a bad one");
    }

    [Fact]
    public async Task DisposedManager_IgnoresFileChangesAndDisposeIsIdempotent()
    {
        using var builtInDir = new TempDirectory();
        using var userDir = new TempDirectory();
        var themeFile = Path.Combine(builtInDir.FolderPath, "night-shade.json");
        WriteThemeFile(themeFile, "night-shade", "Night Shade");
        var manager = CreateManager(builtInDir, userDir);
        await manager.InitializeAsync();

        manager.Dispose();
        manager.Dispose();

        File.WriteAllText(themeFile, ThemeJson("night-shade", "Renamed After Dispose"));
        await Task.Delay(SettleWait);
        manager.AvailableThemes.Should()
            .ContainSingle(t => t.Name == "night-shade")
            .Which.DisplayName.Should().Be("Night Shade");
    }

    [Fact]
    public async Task ReloadedActiveTheme_KeepsRegistrationOrderStable()
    {
        using var builtInDir = new TempDirectory();
        using var userDir = new TempDirectory();
        WriteThemeFile(Path.Combine(builtInDir.FolderPath, "night-shade.json"), "night-shade", "Night Shade");
        WriteThemeFile(Path.Combine(builtInDir.FolderPath, "aurora.json"), "aurora", "Aurora");
        var manager = CreateManager(builtInDir, userDir);
        await manager.InitializeAsync();

        var orderBefore = string.Join(",", manager.AvailableThemes.Select(t => t.Name));
        var themeFile = Path.Combine(builtInDir.FolderPath, "aurora.json");

        File.WriteAllText(themeFile, ThemeJson("aurora", "Aurora Neon"));
        (await WaitUntilAsync(() =>
            manager.AvailableThemes.Any(t => t.Name == "aurora" && t.DisplayName == "Aurora Neon")))
            .Should().BeTrue();

        string.Join(",", manager.AvailableThemes.Select(t => t.Name))
            .Should().Be(orderBefore, "a reload must replace in place, never reorder");
    }

    private static ThemeManager CreateManager(TempDirectory builtInDir, TempDirectory userDir) =>
        new(Substitute.For<ILogger>(), builtInDir.FolderPath, userDir.FolderPath);

    private static void WriteThemeFile(
        string filePath,
        string name,
        string displayName)
    {
        File.WriteAllText(filePath, ThemeJson(name, displayName));
    }

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

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + PollTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(PollInterval);
        }

        return condition();
    }
}
