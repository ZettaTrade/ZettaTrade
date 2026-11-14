namespace ZettaTrade.App.Tests.Views;

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

/// <summary>
/// The field-cell grammar pins (T-232): the editors' caption-over-field
/// cells ride ONE shared class — the FieldGrammar dictionary owns the
/// caption gap (the SpaceS step) and the caption size, the windows include
/// it once (the RowGrammar precedent), and the alerts editor carries no
/// caption copies (the FontSize copies and the captions sitting on their
/// fields are gone). The editor's combos ride content widths (MinWidth
/// over Width): a combo's chrome eats ~45px of any fixed width, so "Falls
/// below" and the ru trigger labels clipped at every culture — the fixed
/// widths die, the numeric wells keep their deliberate ones.
/// </summary>
public sealed class FieldGrammarPinsTests
{
    private static readonly string RepoRoot = FindRepositoryRoot();

    /// <summary>The shared field-cell dictionary, normalized to LF.</summary>
    private static string GrammarXaml =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Themes", "FieldGrammar.axaml"))
            .Replace("\r\n", "\n");

    private static string AlertsXaml =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "AlertsWindow.axaml"))
            .Replace("\r\n", "\n");

    private static string SettingsXaml =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "SettingsWindow.axaml"))
            .Replace("\r\n", "\n");

    [Fact]
    public void TheFieldGrammarDictionary_OwnsTheCellShape()
    {
        ExtractStyleBlock(GrammarXaml, "StackPanel.field-cell").Should().Contain(
            "Property=\"Spacing\" Value=\"{StaticResource SpaceS}\"",
            "the caption gap is the small step — a label never sits on its field");
        ExtractStyleBlock(GrammarXaml, "StackPanel.field-cell > TextBlock").Should().Contain(
            "Property=\"FontSize\" Value=\"{StaticResource CaptionFontSize}\"",
            "the caption treatment rides the class too (the per-cell copies die)");
    }

    [Fact]
    public void TheAlertsEditor_RidesTheSharedCells_AndTheContentWidths()
    {
        AlertsXaml.Should().Contain(
            "<StyleInclude Source=\"avares://ZettaTrade.App/Themes/FieldGrammar.axaml\" />",
            "the editor's cells ride the shared grammar, never a window-local copy");

        // Eleven cells on the facts row (the kind through the note), three
        // on the fire-mechanics row (the re-arm, the cooldown, the sound).
        Regex.Matches(AlertsXaml, "Classes=\"field-cell\"").Should().HaveCount(
            14, "every editor cell is the one shared caption-over-field class");

        // The caption copies died with the class: exactly one CaptionFontSize
        // remains in the window — the manager table's LastFired column.
        Regex.Matches(AlertsXaml, "FontSize=\"\\{StaticResource CaptionFontSize\\}\"")
            .Should().HaveCount(
                1, "the table's secondary column keeps the caption size; the editor cells lost their copies");

        // The content widths: the five clipped-combo candidates ride MinWidth
        // (kind 130, trigger 100, indicator 110, source 120, re-arm 150, and
        // the sound 100 joins for the same grammar), and NO combo in the
        // window carries a fixed width — a combo's chrome eats ~45px of one,
        // so every culture's longest label clips exactly there.
        foreach (var width in new[] { "130", "100", "110", "120", "150" })
        {
            AlertsXaml.Should().Contain(
                $"MinWidth=\"{width}\"",
                "the combo {0} rides the content width — the fixed width clipped the labels", width);
        }

        Regex.Matches(AlertsXaml, "ComboBox Width=").Should().BeEmpty(
            "no editor combo fixes its width — the localized items decide it");
    }

    [Fact]
    public void ThePairsTransfers_ReadAsButtonsAtRest()
    {
        Regex.Matches(SettingsXaml, "Classes=\"icon framed\"").Should().HaveCount(
            2, "the transfer pair between the lists reads as a button at rest through the framed composition");
    }

    /// <summary>Extract the one style block whose selector names <paramref name="selector"/>.</summary>
    private static string ExtractStyleBlock(string grammar, string selector)
    {
        var match = Regex.Match(
            grammar,
            $"<Style Selector=\"{Regex.Escape(selector)}\">(?:.*?\n)*?    </Style>");
        match.Success.Should().BeTrue("the grammar must carry the {0} style", selector);
        return match.Value;
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
