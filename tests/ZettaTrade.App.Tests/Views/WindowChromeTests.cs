namespace ZettaTrade.App.Tests.Views;

using System.Text.Json;
using FluentAssertions;
using Livery.Themes;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;
using ZettaTrade.UI.Resources;

/// <summary>
/// The window-chrome coverage (T-066): every window opts into the styled
/// chrome (full for the resizable shell windows, compact for the fixed-size
/// dialogs), the library styles ship and the chrome theme keys (TitleBar /
/// TitleBarText) exist in every built-in theme file, the generated sample
/// theme and the localization catalog. The window views have no headless
/// render harness, so the view pins ride the source-of-truth files (the repo
/// root walk is deterministic for test and gate runs alike).
/// </summary>
public sealed class WindowChromeTests
{
    private const string StyleIncludeUri = "avares://Livery/Themes/Controls.axaml";

    private static readonly string RepoRoot = FindRepositoryRoot();
    private static readonly string ViewsDirectory =
        Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views");

    /// <summary>Window file → expected chrome class. Full chrome: drag +
    /// minimize + maximize/restore + close. Compact: close only (fixed-size
    /// dialogs and the settings window would bury their content maximized
    /// and drop out of reach minimized).</summary>
    public static TheoryData<string, string> WindowsWithExpectedChrome => new()
    {
        { "MainWindow", "chrome" },
        { "PositionsPanelWindow", "chrome" },
        { "OrderHistoryWindow", "chrome" },
        { "NewOrderDialogWindow", "chrome-compact" },
        { "ConfirmationDialogWindow", "chrome-compact" },
        { "SettingsWindow", "chrome-compact" },
    };

    [Theory]
    [MemberData(nameof(WindowsWithExpectedChrome))]
    public void EveryWindow_CarriesTheChromeClass(string windowName, string expectedClass)
    {
        var source = File.ReadAllText(Path.Combine(ViewsDirectory, $"{windowName}.axaml"));

        source.Should().Contain($"Classes=\"{expectedClass}\"",
            "{0} must opt into the styled chrome", windowName);
    }

    [Fact]
    public void TheApp_CarriesTheLibraryStyleInclude()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "App.axaml"));

        source.Should().Contain($"Source=\"{StyleIncludeUri}\"",
            "the chrome styles ride the application so every window inherits them");
    }

    [Fact]
    public void TheLibraryStyles_DeclareBothChromeVariantsWithTheAvalonia12Mechanism()
    {
        // The chrome pair: extended client area + BorderOnly decorations (the
        // Avalonia 12 way: resize borders/AeroSnap survive, the system caption
        // does not) and both styles keep the same mechanism so the compact
        // variant stays consistent with the full one.
        var source = File.ReadAllText(Path.Combine(RepoRoot, "src", "Livery", "Themes", "Controls.axaml"));

        source.Should().Contain("<Style Selector=\"Window.chrome\">")
            .And.Contain("<Style Selector=\"Window.chrome-compact\">");

        foreach (var setter in new[]
                 {
                     "<Setter Property=\"ExtendClientAreaToDecorationsHint\" Value=\"True\" />",
                     "<Setter Property=\"WindowDecorations\" Value=\"BorderOnly\" />",
                 })
        {
            System.Text.RegularExpressions.Regex
                .Matches(source, System.Text.RegularExpressions.Regex.Escape(setter))
                .Should().HaveCount(2, "both chrome styles must carry the full mechanism");
        }
    }

    [Fact]
    public void EveryBuiltInTheme_CarriesTheChromeColorKeys()
    {
        var themeFiles = Directory.GetFiles(Path.Combine(RepoRoot, "themes"), "*.json");
        themeFiles.Should().HaveCount(5, "the built-in set is dark, light, midnight, oled, trading-pro");

        foreach (var themeFile in themeFiles)
        {
            var definition = ParseTheme(themeFile);

            definition.Colors.Should().ContainKey("TitleBar").WhoseValue.Should().StartWith("#");
            definition.Colors.Should().ContainKey("TitleBarText").WhoseValue.Should().StartWith("#");
        }
    }

    [Fact]
    public async Task TheGeneratedSampleTheme_CarriesTheChromeColorKeys()
    {
        // First initialize creates the settings directory and writes the
        // sample theme file into it (it registers on the next load, the same
        // flow the app's first run takes).
        var builtIn = NewTempDirectory();
        var user = Path.Combine(Path.GetTempPath(), $"chrome-test-{Guid.NewGuid():N}");
        try
        {
            var manager = new ThemeManager(
                Substitute.For<ILogger>(),
                user,
                builtIn);

            await manager.InitializeAsync();
            manager.Dispose();

            var sampleFile = Path.Combine(user, "sample-custom.json");
            File.Exists(sampleFile).Should().BeTrue("InitializeAsync must create the sample theme file");

            var definition = ParseTheme(sampleFile);
            definition.Colors.Should().ContainKeys("TitleBar", "TitleBarText");
        }
        finally
        {
            Directory.Delete(builtIn, recursive: true);
            Directory.Delete(user, recursive: true);
        }
    }

    /// <summary>The loader's parse contract: camelCase JSON, case-insensitive
    /// property matching (the same options ThemeManager deserializes with).</summary>
    private static ThemeDefinition ParseTheme(string path) =>
        JsonSerializer.Deserialize<ThemeDefinition>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Fact]
    public void TheCatalog_CarriesEveryChromeKeyInBothCultures()
    {
        foreach (var key in new[]
                 {
                     "Chrome.Title", "Chrome.Minimize", "Chrome.Maximize",
                     "Chrome.Restore", "Chrome.Close",
                 })
        {
            Strings.ResourceManager.GetString(key, System.Globalization.CultureInfo.InvariantCulture)
                .Should().NotBeNullOrEmpty("'{0}' is a user-facing chrome string", key);
            Strings.ResourceManager.GetString(key, System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))
                .Should().NotBeNullOrEmpty("'{0}' must localize into Russian", key);
        }
    }

    /// <summary>
    /// Walk up from the test's base directory to the folder carrying the
    /// solution marker: tests, gate runners and IDE runs all execute inside
    /// the repository, so this resolves identically for every consumer.
    /// </summary>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ZettaTrade.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the test must run inside a ZettaTrade repository checkout");
        return directory!.FullName;
    }

    private static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chrome-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
