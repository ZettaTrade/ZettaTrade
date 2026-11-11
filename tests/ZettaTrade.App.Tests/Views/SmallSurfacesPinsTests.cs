namespace ZettaTrade.App.Tests.Views;

using System;
using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

/// <summary>
/// The small-surfaces polish pins (T-161): the wizard's theme picker rides
/// the settings picker's own swatch shape (the same converter, the same
/// preview geometry — the two pickers cannot disagree) and speaks the walk's
/// position through the localized step counter; the hotkey sheet renders its
/// gestures as kbd-style keycap chips instead of a trailing text; the
/// palette's blank page prints the pool headers above their rows (the rows'
/// own visuals — the flat keyboard route never changes). The repo has no
/// headless render harness yet (the T-144 lane is queued), so the visual
/// facts ride the source of truth here — recorded, not silently absorbed.
/// </summary>
public sealed class SmallSurfacesPinsTests
{
    private static readonly string RepoRoot = FindRepositoryRoot();

    /// <summary>The wizard window source, normalized to LF.</summary>
    private static string WizardXaml =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "FirstRunWizardWindow.axaml"))
            .Replace("\r\n", "\n");

    /// <summary>The settings window source, normalized to LF.</summary>
    private static string SettingsXaml =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "SettingsWindow.axaml"))
            .Replace("\r\n", "\n");

    /// <summary>The main window source, normalized to LF.</summary>
    private static string MainWindowXaml =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "MainWindow.axaml"))
            .Replace("\r\n", "\n");

    /// <summary>The English string table.</summary>
    private static string StringsEn =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.UI", "Resources", "Strings.resx"));

    /// <summary>The Russian string table.</summary>
    private static string StringsRu =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.UI", "Resources", "Strings.ru.resx"));

    [Fact]
    public void TheWizardThemePicker_RidesTheSettingsSwatchShape()
    {
        var wizard = WizardXaml;
        var settings = SettingsXaml;

        // The same converter: the wizard previews the theme file's
        // previewColors the settings picker previews — one truth, one lane.
        wizard.Should().Contain(
            "themes:PreviewColorsToBrushesConverter.Instance",
            "the wizard's theme pick previews through the settings picker's own converter");

        // The same swatch geometry as the settings picker: a 10x10,
        // radius-2 preview chip. The pin walks BOTH sources so a one-sided
        // retune fails by name.
        foreach (var (name, source) in new[] { ("wizard", wizard), ("settings", settings) })
        {
            var swatches = Regex.Matches(
                source,
                @"<Border\s+Width=""10""\s+Height=""10""\s+CornerRadius=""2""\s+Background=""\{Binding\}""\s*/>");
            swatches.Should().HaveCount(
                1,
                "the {0}'s theme pick previews the same swatch shape — one honest picker shape", name);
        }
    }

    [Fact]
    public void TheWizardSpeaks_TheLocalizedStepCounter()
    {
        var wizard = WizardXaml;

        wizard.Should().Contain(
            "Text=\"{Binding StepCounter}\"",
            "the walk's position reads above every step body, without the captions");

        foreach (var (table, name) in new[] { (StringsEn, "en"), (StringsRu, "ru") })
        {
            table.Should().Contain(
                "name=\"Wizard.Step.Counter\"",
                "the {0} table carries the step counter's parameterized recipe", name);
        }
    }

    [Fact]
    public void TheHotkeySheet_RendersGestures_AsKeycapChips()
    {
        var main = MainWindowXaml;

        // The sheet's rows bind the gesture's keycap parts, not the raw
        // gesture string — "Ctrl+K" reads as the two keys it is.
        main.Should().Contain(
            "ItemsSource=\"{Binding GestureParts}\"",
            "the sheet renders one chip per key part");

        // The trailing-text shape stays dropped: the raw gesture string
        // never prints in the sheet again.
        main.Should().NotContain(
            "Text=\"{Binding Gesture}\"",
            "the gesture as one trailing text was the shape this task replaced");

        // The chips are kbd-style palette shapes: the window's own backdrop
        // fill inside a Border stroke on the Surface card — no hex, no
        // system brushes.
        var chipPattern = @"Background=""\{DynamicResource Background\}""\s+BorderBrush=""\{DynamicResource Border\}""\s+BorderThickness=""1""\s+CornerRadius=""4""";
        new Regex(chipPattern).IsMatch(main).Should().BeTrue(
            "a keycap is a recessed palette chip: the backdrop fill inside a Border stroke");
    }

    [Fact]
    public void ThePaletteBlankPage_PrintsThePoolHeaders_OverTheirRows()
    {
        var main = MainWindowXaml;

        // Each pool's header rides its own row flag (the VM computes the
        // heads; the template prints the section above the head row) and
        // its own localized caption — live re-localizing through the
        // markup extension.
        foreach (var (flag, key) in new[]
                 {
                     ("IsRecentsHead", "Palette.Group.Recents"),
                     ("IsFavoritesHead", "Palette.Group.Favorites"),
                     ("IsOtherHead", "Palette.Group.Other"),
                 })
        {
            var pattern = $@"IsVisible=""{{Binding {flag}}}""\s+Text=""{{loc:Localized {key}}}""";
            new Regex(pattern).IsMatch(main).Should().BeTrue(
                "the {0} pool's header prints through its own flag and caption", key);
        }

        foreach (var (table, name) in new[] { (StringsEn, "en"), (StringsRu, "ru") })
        {
            foreach (var key in new[] { "Palette.Group.Recents", "Palette.Group.Favorites", "Palette.Group.Other" })
            {
                table.Should().Contain(
                    $"name=\"{key}\"",
                    "the {0} table carries the {1} caption", name, key);
            }
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
}
