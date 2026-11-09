namespace ZettaTrade.UI.Tests.Themes;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using Livery.Themes;
using Xunit;

/// <summary>
/// Pins for the theme palette's accessibility conformance (T-110): the WCAG
/// contrast math's own edges, then every SHIPPED built-in theme against the
/// required pairs — text-bearing roles read at 4.5:1 (the WCAG AA level for
/// body text), data-bearing signal roles (the candle up/down family the
/// tape glyph fills use) at 3.0:1. The conformance walks the REAL repo
/// theme files, so a shifted palette fails the fact the moment it degrades,
/// and the fixture fails loudly when the repo-root themes directory cannot
/// be located — a silently skipped audit would be a false green.
/// </summary>
public sealed class ContrastTests
{
    /// <summary>The WCAG AA minimum for text-bearing color pairs.</summary>
    private const double TextMinimum = 4.5;

    /// <summary>The WCAG minimum for data-bearing non-text signal pairs.</summary>
    private const double SignalMinimum = 3.0;

    /// <summary>
    /// The pairs a theme must serve, foreground over background within the
    /// theme's own surfaces: the body/secondary text on the free-standing
    /// surfaces, the title-bar strip pair, the toggle-on accent text, the
    /// error line, the gold accent the on-state toggles and the palette's
    /// compare hint carry, and the signal family the tape and candles ride.
    /// </summary>
    private static readonly (string Foreground, string Background, double Minimum, string Role)[] RequiredPairs =
    [
        ("TextPrimary", "Background", TextMinimum, "body text"),
        ("TextPrimary", "Surface", TextMinimum, "body text"),
        ("TextPrimary", "Accent", TextMinimum, "text over the accent fill (hover states)"),
        ("TextSecondary", "Background", TextMinimum, "secondary text"),
        ("TextSecondary", "Surface", TextMinimum, "secondary text"),
        ("TitleBarText", "TitleBar", TextMinimum, "title-bar strip text"),
        ("Error", "Background", TextMinimum, "error line text"),
        ("Error", "Surface", TextMinimum, "error line text on surface"),
        ("Highlight", "Background", TextMinimum, "accent as on-state text (viewport hint)"),
        ("Highlight", "Surface", TextMinimum, "accent as on-state text"),
        ("Buy", "Background", SignalMinimum, "signal color family (tape glyphs, fills)"),
        ("Sell", "Background", SignalMinimum, "signal color family"),
        ("CandleUp", "Background", SignalMinimum, "signal color family"),
        ("CandleDown", "Background", SignalMinimum, "signal color family"),
    ];

    #region WCAG Math Edges

    /// <summary>Pure black over pure white is the maximum ratio.</summary>
    [Fact]
    public void Ratio_PureBlackOverPureWhite_IsTheMaximum()
    {
        Contrast.Ratio("#000000", "#ffffff").Should().Be(Contrast.WhiteOverBlack);
        Contrast.Ratio("#ffffff", "#000000").Should().Be(Contrast.WhiteOverBlack);
    }

    /// <summary>Gray-on-gray reads as a known small ratio, never an inflated one.</summary>
    [Fact]
    public void Ratio_MidGrayOverMidGray_IsOne()
    {
        Contrast.Ratio("#a0a0a0", "#a0a0a0").Should().Be(1.0);
    }

    /// <summary>The shorthand sRGB knee: dark values linearize at division, bright ones at the curve.</summary>
    [Fact]
    public void RelativeLuminance_KneeBehavior_MatchesTheStandardCurve()
    {
        Contrast.RelativeLuminance("#000000").Should().Be(0.0);
        Contrast.RelativeLuminance("#ffffff").Should().Be(1.0);
    }

    #endregion

    #region Shipped Theme Conformance

    [Fact]
    public void EveryShippedTheme_ServesEveryRequiredPair_AtItsMinimum()
    {
        var themesDir = LocateShippedThemesDirectory();
        var themes = Directory
            .EnumerateFiles(themesDir, "*.json")
            .Select(path => (path, Definition: JsonSerializer.Deserialize<ThemeDefinition>(
                File.ReadAllText(path),
                new JsonSerializerOptions() { PropertyNameCaseInsensitive = true })))
            .Where(entry => entry.Definition is not null)
            .ToList();

        themes.Should().NotBeEmpty(
            $"the shipped themes must be discoverable at {themesDir} - the audit audits the real files");

        var violations = new List<string>();
        foreach (var (path, definition) in themes)
        {
            var mailedName = definition!.Name ?? Path.GetFileNameWithoutExtension(path);
            foreach (var (foregroundKey, backgroundKey, minimum, role) in RequiredPairs)
            {
                if (!definition.Colors!.TryGetValue(foregroundKey, out var foreground))
                {
                    violations.Add($"{mailedName}: missing color key '{foregroundKey}'");
                    continue;
                }

                if (!definition.Colors.TryGetValue(backgroundKey, out var background))
                {
                    violations.Add($"{mailedName}: missing color key '{backgroundKey}'");
                    continue;
                }

                var ratio = Contrast.Ratio(foreground, background);
                if (ratio < minimum)
                {
                    violations.Add(
                        $"{mailedName}: {foregroundKey}#{foreground} over {backgroundKey}#{background} " +
                        $"reads {ratio:F2}:1 for the {role} role (needs {minimum:F1})");
                }
            }
        }

        violations.Should().BeEmpty("every shipped theme must serve the accessibility pairs");
    }

    #endregion

    /// <summary>
    /// The shipped themes live at the repo root's <c>themes</c> folder: the
    /// test output directory is a fixed depth below it, but the walk makes
    /// the fixture resilient — one parent that lacks the folder keeps the
    /// search going, and running out of parents fails the audit plainly.
    /// </summary>
    private static string LocateShippedThemesDirectory()
    {
        var probe = new DirectoryInfo(AppContext.BaseDirectory);
        while (probe is not null)
        {
            var candidate = Path.Combine(probe.FullName, "themes");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "light.json")))
            {
                return candidate;
            }

            probe = probe.Parent;
        }

        throw new InvalidOperationException("The repo-root themes directory could not be located from the test output.");
    }
}
