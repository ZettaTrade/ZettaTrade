namespace ZettaTrade.UI.Tests.Themes;

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;
using Livery.Themes;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.UI.Tests.Support;

/// <summary>
/// Tests for file-driven theme registration: themes are JSON files loaded
/// through LoadCustomThemeAsync (the same loader the manager uses for the
/// shipped built-ins and the user themes directory), so a theme file replaces
/// an earlier registration with the same name and malformed files are skipped
/// without breaking startup.
/// </summary>
public sealed class ThemeManagerTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [Fact]
    public async Task LoadCustomThemeAsync_WithValidFile_RegistersThemeFromFile()
    {
        using var dir = new TempDirectory();
        var file = WriteTheme(dir, "trading-pro", "Trading Pro");
        var manager = CreateManager(dir);

        await manager.LoadCustomThemeAsync(file);

        manager.AvailableThemes.Should()
            .ContainSingle(t => t.Name == "trading-pro")
            .Which.Should()
            .Match<ThemeInfo>(t =>
                t.DisplayName == "Trading Pro" &&
                !t.IsBuiltIn &&
                t.Colors != null &&
                t.Colors.ContainsKey("Background"));
    }

    [Fact]
    public async Task LoadCustomThemeAsync_WithSameNameAsRegistered_ReplacesInPlace()
    {
        using var dir = new TempDirectory();
        var first = WriteTheme(dir, "dark", "Dark");
        var second = WriteTheme(dir, "dark", "Dark Edited");
        var manager = CreateManager(dir);

        await manager.LoadCustomThemeAsync(first);
        await manager.LoadCustomThemeAsync(second);

        var matching = manager.AvailableThemes.Where(t => t.Name == "dark").ToList();
        matching.Should().ContainSingle();
        matching[0].DisplayName.Should().Be("Dark Edited");
    }

    [Fact]
    public async Task LoadCustomThemeAsync_WithMissingDisplayName_FallsBackToName()
    {
        using var dir = new TempDirectory();
        var json = JsonSerializer.Serialize(new ThemeDefinition { Name = "bare", Colors = new Dictionary<string, string>() }, JsonOptions);
        var file = Path.Combine(dir.FolderPath, "bare.json");
        await File.WriteAllTextAsync(file, json);
        var manager = CreateManager(dir);

        await manager.LoadCustomThemeAsync(file);

        manager.AvailableThemes.Should()
            .ContainSingle(t => t.Name == "bare")
            .Which.DisplayName.Should().Be("bare");
    }

    [Fact]
    public async Task LoadCustomThemeAsync_WithMalformedFile_LogsInsteadOfThrowing()
    {
        using var dir = new TempDirectory();
        var file = Path.Combine(dir.FolderPath, "broken.json");
        await File.WriteAllTextAsync(file, "{ not valid json");
        var manager = CreateManager(dir);

        var act = async () => await manager.LoadCustomThemeAsync(file);

        await act.Should().NotThrowAsync();
        manager.AvailableThemes.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadCustomThemeAsync_WithoutName_IsIgnored()
    {
        using var dir = new TempDirectory();
        var json = JsonSerializer.Serialize(new ThemeDefinition { DisplayName = "Nameless" }, JsonOptions);
        var file = Path.Combine(dir.FolderPath, "nameless.json");
        await File.WriteAllTextAsync(file, json);
        var manager = CreateManager(dir);

        await manager.LoadCustomThemeAsync(file);

        manager.AvailableThemes.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyThemeAsync_WithRegisteredTheme_PublishesCanonicalNameOnThemeChanged()
    {
        // Application resources are not available in unit tests; the manager
        // must still switch the current theme and publish the change.
        using var dir = new TempDirectory();
        var file = WriteTheme(dir, "night-shade", "Night Shade");
        var manager = CreateManager(dir);
        await manager.LoadCustomThemeAsync(file);

        var published = new List<string>();
        using var subscription = manager.ThemeChanged.Subscribe(published.Add);
        await manager.ApplyThemeAsync("night-shade");

        manager.CurrentTheme.Should().Be("night-shade");
        published.Should().HaveCount(2).And.Contain("night-shade");
    }

    [Fact]
    public async Task ApplyThemeAsync_WithDisplayNameOnly_DoesNotApply()
    {
        // Name is the single identifier: a display name is not the lookup key.
        // "Night Shade" must be rejected like any unknown name and change
        // nothing — otherwise two themes sharing a display name would be
        // ambiguous.
        using var dir = new TempDirectory();
        var file = WriteTheme(dir, "night-shade", "Night Shade");
        var manager = CreateManager(dir);
        await manager.LoadCustomThemeAsync(file);

        var published = new List<string>();
        using var subscription = manager.ThemeChanged.Subscribe(published.Add);
        await manager.ApplyThemeAsync("Night Shade");

        manager.CurrentTheme.Should().Be("dark");
        published.Should().ContainSingle().Which.Should().Be("dark");
    }

    [Fact]
    public async Task ApplyThemeAsync_WithDuplicateDisplayNames_MatchesByCanonicalName()
    {
        using var dir = new TempDirectory();
        var first = WriteTheme(dir, "dark", "Dark");
        var second = WriteTheme(dir, "user-dark", "Dark");
        var manager = CreateManager(dir);
        await manager.LoadCustomThemeAsync(first);
        await manager.LoadCustomThemeAsync(second);

        await manager.ApplyThemeAsync("user-dark");

        manager.CurrentTheme.Should().Be("user-dark");
        manager.AvailableThemes.Count(t => t.DisplayName == "Dark").Should().Be(2);
        manager.AvailableThemes.Count(t => t.Name == "user-dark").Should().Be(1);
    }

    [Fact]
    public async Task ApplyThemeAsync_WithUnknownTheme_KeepsCurrentTheme()
    {
        using var dir = new TempDirectory();
        var manager = CreateManager(dir);

        var published = new List<string>();
        using var subscription = manager.ThemeChanged.Subscribe(published.Add);
        await manager.ApplyThemeAsync("missing-theme");

        manager.CurrentTheme.Should().Be("dark");
        published.Should().ContainSingle().Which.Should().Be("dark");
    }

    /// <summary>
    /// The user themes directory is a required constructor argument; every
    /// test passes its own temporary directory so nothing reaches the real
    /// user configuration.
    /// </summary>
    [Fact]
    public void Constructor_WithBlankUserThemesDirectory_Throws()
    {
        // The lib never resolves application paths itself: the user themes
        // directory is a required argument and a blank one is a wiring bug,
        // not a silent fallback onto Infrastructure's config resolver.
        foreach (var blank in new[] { string.Empty, "   " })
        {
            var act = () => new ThemeManager(Substitute.For<ILogger>(), blank);

            act.Should().Throw<ArgumentException>();
        }
    }

    private static ThemeManager CreateManager(TempDirectory dir) =>
        new(Substitute.For<ILogger>(), dir.FolderPath);

    private static string WriteTheme(TempDirectory dir, string name, string displayName)
    {
        var definition = new ThemeDefinition
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
        };
        var file = Path.Combine(dir.FolderPath, $"{name}.json");
        File.WriteAllText(file, JsonSerializer.Serialize(definition, JsonOptions));
        return file;
    }
}

