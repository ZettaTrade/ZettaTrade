namespace ZettaTrade.UI.Tests.Themes;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using Livery.Themes;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;
using ZettaTrade.UI.Tests.Support;

/// <summary>
/// The palette contract battery over the REAL theme files (T-151): every
/// shipped built-in answers the role pairs the library's styles serve —
/// the hover fill and the focus stroke read at 3.0:1 (the WCAG 1.4.11
/// non-text level), text over the interactive fill at 4.5:1, the chip
/// fills at 3.0:1. The shipped accent goldens are pinned as literals, so
/// a rolled-back value fails the gate by name, and the generated sample
/// theme (the auto-created user theme the first run writes) answers the
/// same contract — the "on load" path the review demanded the custom
/// themes to hit.
/// </summary>
public sealed class PaletteContractTests
{
    private static readonly string ThemesDirectory =
        Path.Combine(RepoRoot, "themes");

    /// <summary>The shipped accent goldens (T-151's re-tune): every value
    /// an interactive-strength token — the dark and OLED baselines share
    /// the interactive blue, the light baseline carries the black-text leg
    /// of the contract.</summary>
    private static readonly IReadOnlyDictionary<string, string> AccentGoldens =
        new Dictionary<string, string>
        {
            ["dark"] = "#2563eb",
            ["light"] = "#3b82f6",
            ["midnight"] = "#1f6feb",
            ["oled"] = "#2563eb",
            ["trading-pro"] = "#047857",
        };

    private static string RepoRoot => FindRepositoryRoot();

    [Fact]
    public void EveryShippedTheme_ServesTheWholeContract()
    {
        var themeFiles = Directory.GetFiles(ThemesDirectory, "*.json");
        themeFiles.Select(Path.GetFileNameWithoutExtension).Should().BeEquivalentTo(AccentGoldens.Keys,
            "the battery walks the five shipped built-ins");

        foreach (var themeFile in themeFiles)
        {
            var theme = ParseTheme(themeFile);
            var violations = PaletteContract.Violations(theme);

            violations.Should().BeEmpty(
                "the shipped '{0}' must serve the whole role contract: {1}",
                theme.Name,
                string.Join("; ", violations.Select(v => $"{v.Pair} ({v.Ratio:F2} < {v.Minimum:F1}: {v.Foreground} over {v.Background})")));
        }
    }

    [Theory]
    [MemberData(nameof(AccentGoldenKeys))]
    public void EveryShippedTheme_CarriesTheInteractiveAccentGolden(string themeName)
    {
        var theme = ParseTheme(Path.Combine(ThemesDirectory, $"{themeName}.json"));

        theme.Colors.Should().ContainKey("Accent").WhoseValue.Should().Be(
            AccentGoldens[themeName],
            "the '/{0}' accent is the interactive-strength golden — a rolled-back value fails by name", themeName);
    }

    [Fact]
    public void TheRolledBackDarkAccent_FailsTheBatteryNamingThePairs()
    {
        // The negative fixture: the pre-T-151 dark accent (which shipped at
        // ~1.4:1 hover and ~1.6:1 focus) — the battery must name BOTH
        // broken pairs loudly, so a rolled-back theme cannot sneak through.
        var theme = ParseTheme(Path.Combine(ThemesDirectory, "dark.json"));
        var broken = new Dictionary<string, string>(theme.Colors!)
        {
            ["Accent"] = "#0f3460",
        };

        var violations = PaletteContract.Violations(new ThemeDefinition { Colors = broken });

        violations.Select(v => v.Pair).Should().Contain(new[]
        {
            "interactive hover fill over surface",
            "focus stroke over background",
        });
    }

    [Fact]
    public async Task TheGeneratedSampleTheme_ServesTheContractOnLoad()
    {
        // The custom-user path: the first run writes the sample theme into
        // the user themes directory; the same file the ThemeManager loads
        // must answer the role battery — a user theme the app itself
        // provides never ships an invisible hover or focus state. The
        // directory must NOT pre-exist: the sample creation is the
        // first-run branch (an existing-empty directory loads nothing).
        var userDirectory = Path.Combine(
            Path.GetTempPath(),
            $"zettatrade-tests-{Guid.NewGuid():N}");
        Directory.Exists(userDirectory).Should().BeFalse(
            "the sample rides the first-run path: the user themes directory is created by InitializeAsync");

        try
        {
            var manager = new ThemeManager(Substitute.For<ILogger>(), userDirectory);

            await manager.InitializeAsync();
            manager.Dispose();

            var sampleFile = Path.Combine(userDirectory, "sample-custom.json");
            File.Exists(sampleFile).Should().BeTrue("InitializeAsync must create the sample theme file");

            var theme = ParseTheme(sampleFile);
            var violations = PaletteContract.Violations(theme);

            violations.Should().BeEmpty(
                "the auto-created sample must serve the whole role contract: {0}",
                string.Join("; ", violations.Select(v => $"{v.Pair} ({v.Ratio:F2} < {v.Minimum:F1}: {v.Foreground} over {v.Background})")));
        }
        finally
        {
            if (Directory.Exists(userDirectory))
            {
                Directory.Delete(userDirectory, recursive: true);
            }
        }
    }

    private static ThemeDefinition ParseTheme(string path) =>
        JsonSerializer.Deserialize<ThemeDefinition>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    /// <summary>The theory data source: the five shipped built-ins carry a
    /// golden accent each — the data source turns the golden dictionary's
    /// keys into the theory's rows.</summary>
    public static IEnumerable<object[]> AccentGoldenKeys() =>
        AccentGoldens.Keys.Select(key => (object[])[key]);

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
}
