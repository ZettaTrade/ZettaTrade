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
/// themes to hit. The battery grew a mirror lane (T-180): a theme that
/// carries the fourth preview swatch must mirror its live Accent — the
/// picker's strip never advertises a color the theme no longer owns.
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

    /// <summary>
    /// The A11Y audit's mirror rule (T-180): the fourth preview swatch
    /// mirrors the theme's live Accent — the T-151 re-tune left the dark,
    /// light and oled strips advertising retired colors. A theme with
    /// fewer than four swatches (or no Accent) advertises nothing, so the
    /// mirror binds only themes that carry the fourth chip.
    /// </summary>
    [Theory]
    [MemberData(nameof(AccentGoldenKeys))]
    public void EveryShippedTheme_PreviewsTheAccentMirror(string themeName)
    {
        var theme = ParseTheme(Path.Combine(ThemesDirectory, $"{themeName}.json"));
        var violations = PreviewMirrorViolations(themeName, theme);

        violations.Should().BeEmpty(
            "the '{0}' theme's fourth preview swatch must mirror its Accent (the picker's strip never sells a color the theme no longer owns): {1}",
            themeName,
            string.Join("; ", violations));
    }

    /// <summary>
    /// The negative fixture: the pre-fix dark theme — its fourth swatch
    /// advertised the retired hover blue while the Accent had moved on.
    /// The pin must fail naming the file and the pair, so a stale mirror
    /// cannot sneak through the gate again.
    /// </summary>
    [Fact]
    public void TheStaleDarkMirror_FailsThePinNamingTheFileAndThePair()
    {
        var theme = ParseTheme(Path.Combine(ThemesDirectory, "dark.json"));
        var stale = new ThemeDefinition
        {
            Name = theme.Name,
            Colors = new Dictionary<string, string>(theme.Colors!),
            PreviewColors = ["#0a0a0f", "#1a1a2e", "#16213e", "#0f3460"],
        };

        var violations = PreviewMirrorViolations("dark", stale);

        violations.Should().ContainSingle().Which.Should().Contain("themes/dark.json")
            .And.Contain("#0f3460")
            .And.Contain("#2563eb");
    }

    /// <summary>The mirror rule itself: the fourth preview swatch equals
    /// the Accent (case-insensitive — colors, not strings). Returns the
    /// violation lines (empty when the theme carries no fourth chip, no
    /// Accent, or an honest mirror) so the negative fixture can name the
    /// file and the pair it caught.</summary>
    private static IReadOnlyList<string> PreviewMirrorViolations(string themeName, ThemeDefinition theme)
    {
        var violations = new List<string>();
        var accent = theme.Colors is null ? null : theme.Colors.GetValueOrDefault("Accent");
        if (theme.PreviewColors is not { Length: >= 4 } previews || accent is null)
        {
            return violations;
        }

        var swatch = previews[3];
        if (!string.Equals(swatch, accent, StringComparison.OrdinalIgnoreCase))
        {
            violations.Add(
                $"themes/{themeName}.json: the fourth preview swatch '{swatch}' does not mirror the Accent '{accent}'");
        }

        return violations;
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

            // The mirror lane rides the same promise (T-180): the sample's
            // fourth preview swatch mirrors its live Accent.
            PreviewMirrorViolations("sample-custom", theme).Should().BeEmpty(
                "the auto-created sample must mirror its Accent in the fourth preview swatch");
        }
        finally
        {
            if (Directory.Exists(userDirectory))
            {
                Directory.Delete(userDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void EveryShippedTheme_ServesTheChangeTintTextLeg()
    {
        // The change-tint text leg (T-159): the watchlist's 24h change text
        // and the sparkline strokes ride CandleUp/CandleDown directly over
        // the window Background — the candle keys took on a TEXT duty, so
        // every shipped palette must read them at the WCAG AA text minimum
        // (the T-151 battery pins the role pairs; this pins the tint's own
        // leg by name, the rolled-back value fails by name).
        foreach (var themeFile in Directory.GetFiles(ThemesDirectory, "*.json"))
        {
            var theme = ParseTheme(themeFile);

            foreach (var role in new[] { "CandleUp", "CandleDown" })
            {
                var ratio = Contrast.Ratio(
                    theme.Colors![role],
                    theme.Colors["Background"]);

                ratio.Should().BeGreaterThanOrEqualTo(
                    PaletteContract.TextPairMinimum,
                    "the '{0}' theme's {1} tint reads as text over its background ({2:F2} < {3:F1})",
                    theme.Name,
                    role,
                    ratio,
                    PaletteContract.TextPairMinimum);
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
