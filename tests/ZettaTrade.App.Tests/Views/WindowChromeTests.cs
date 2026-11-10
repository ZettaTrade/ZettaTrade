namespace ZettaTrade.App.Tests.Views;

using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Templates;
using Avalonia.Styling;
using Avalonia.Threading;
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
/// root walk is deterministic for test and gate runs alike). The library
/// style facts ride the compiled package surface (T-130): the pins walk the
/// object graph the StyleInclude serves; the source-text pins live in the
/// Livery repo's own suite now.
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
        { "BalancesPanelWindow", "chrome" },
        { "OrderHistoryWindow", "chrome" },
        { "DetachedChartWindow", "chrome" },
        { "DetachedBookWindow", "chrome" },
        { "DetachedAssistantWindow", "chrome" },
        { "NewOrderDialogWindow", "chrome-compact" },
        { "ConfirmationDialogWindow", "chrome-compact" },
        { "FirstRunWizardWindow", "chrome-compact" },
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
    public async Task TheLibraryStyles_DeclareBothChromeVariantsWithTheAvalonia12Mechanism()
    {
        // The chrome pair: extended client area + BorderOnly decorations (the
        // Avalonia 12 way: resize borders/AeroSnap survive, the system caption
        // does not) and both styles keep the same mechanism so the compact
        // variant stays consistent with the full one. Since T-130 the sources
        // live in the Livery repo: the pin walks the compiled styles the
        // package serves through the StyleInclude URI - a bad publish fails
        // here, not in a window.
        var styles = await CompiledLibraryStyles.Value;

        var chromeStyles = styles.OfType<Style>()
            .Where(s => s.Selector?.ToString() is "Window.chrome" or "Window.chrome-compact")
            .ToList();
        chromeStyles.Select(s => s.Selector!.ToString()).Should().BeEquivalentTo(
            new[] { "Window.chrome", "Window.chrome-compact" },
            "the chrome pair ships in the package styles");

        foreach (var style in chromeStyles)
        {
            var setters = style.Setters.OfType<Setter>().ToList();

            setters.Should().Contain(
                s => s.Property != null && s.Property.Name == "ExtendClientAreaToDecorationsHint" && Equals(s.Value, true),
                "{0} must carry the extended-client-area mechanism", style.Selector);
            setters.Should().Contain(
                s => s.Property != null && s.Property.Name == "WindowDecorations" && s.Value != null && s.Value.ToString() == "BorderOnly",
                "{0} must carry BorderOnly decorations", style.Selector);
            setters.Should().Contain(
                s => s.Property != null && s.Property.Name == "Template" && s.Value is ControlTemplate,
                "{0} must carry its window template", style.Selector);
        }
    }

    [Fact]
    public async Task EveryChromeTemplate_IsACompiledWindowTemplate()
    {
        // Avalonia 12 presents ContentControl.Content only through the
        // explicit Content template bindings in the template itself: a bare
        // PART_ContentPresenter never receives the window's content and the
        // window renders as the bare background (the black-window
        // regression). Both chrome variants must ship a real window template
        // through the package; the binding-level behavioral fact is the
        // Livery suite's pin (the template materializes there).
        var styles = await CompiledLibraryStyles.Value;

        foreach (var chrome in new[] { "Window.chrome", "Window.chrome-compact" })
        {
            var template = styles.OfType<Style>()
                .Single(s => s.Selector?.ToString() == chrome)
                .Setters.OfType<Setter>()
                .Where(s => s.Property?.Name == "Template")
                .Select(s => s.Value)
                .OfType<ControlTemplate>()
                .FirstOrDefault();

            template.Should().NotBeNull("the {0} template must ride the package", chrome);
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
                .Should().NotBeNullOrEmpty("'{0}' must localize into the ru catalog", key);
        }
    }

    /// <summary>The compiled library styles (T-130), loaded once per test
    /// run: the same object graph the application's StyleInclude serves.
    /// AppBuilder.Setup refuses to run without a windowing subsystem, and
    /// the smallest one (headless) binds its compositor to the dispatcher
    /// owner thread - so the pin stands it up ON the pump thread (the
    /// T-138 dispatcher world) through the pump's own drain path: no extra
    /// thread, and no interference with the facts' frozen dispatcher world
    /// (the setup and the load are one posted job, executed only during a
    /// requested drain, never spontaneously).</summary>
    private static readonly Lazy<Task<Styles>> CompiledLibraryStyles = new(
        LoadCompiledLibraryStyles,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static async Task<Styles> LoadCompiledLibraryStyles()
    {
        AvaloniaDispatcherPump.EnsureInstalled();

        Styles? styles = null;
        Exception? failure = null;

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                // Avalonia 12 refuses a second AppBuilder setup ("Setup was
                // already called on one of AppBuilder instances"): reuse the
                // process-singleton application world when another test
                // class (the headless publish pins, T-150) already stood it
                // up, and only build the bare windowing world when none
                // exists. The load itself needs just the platform, not the
                // application's own styles.
                if (Application.Current is null)
                {
                    AppBuilder.Configure<Application>()
                        .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                        .SetupWithoutStarting();
                }

                styles = (Styles)AvaloniaXamlLoader.Load(new Uri(StyleIncludeUri));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        await AvaloniaDispatcherPump.DrainAsync();

        if (failure is not null)
        {
            throw failure;
        }

        styles.Should().NotBeNull("the package must serve its compiled styles");
        return styles!;
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
