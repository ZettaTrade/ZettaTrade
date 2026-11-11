namespace ZettaTrade.App.Tests.Views;

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

/// <summary>
/// The table grammar pins (T-157): the positions panel's three sections
/// and the order-history window speak one row scaffold — the emphasized
/// header rows over a hairline, the hairline-separated rows, the side
/// chips in the state colors and the three-weight row-action hierarchy —
/// all carried once by the shared RowGrammar dictionary, never per-window
/// copies. The repo has no headless render harness yet (the T-144 lane is
/// queued), so the "layout survives the T-144 headless bounds pin"
/// acceptance rides that queued lane honestly (recorded in canon, not
/// faked); the facts here walk the sources of truth (the tokens/chrome
/// pins' precedent).
/// </summary>
public sealed class TableGrammarPinsTests
{
    private static readonly string RepoRoot = FindRepositoryRoot();

    /// <summary>The shared row grammar dictionary, normalized to LF.</summary>
    private static string GrammarXaml =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Themes", "RowGrammar.axaml"))
            .Replace("\r\n", "\n");

    /// <summary>The positions panel source, normalized to LF.</summary>
    private static string PositionsXaml =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "PositionsPanelWindow.axaml"))
            .Replace("\r\n", "\n");

    /// <summary>The order history window source, normalized to LF.</summary>
    private static string HistoryXaml =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "OrderHistoryWindow.axaml"))
            .Replace("\r\n", "\n");

    private static string EnglishResx =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.UI", "Resources", "Strings.resx"));

    private static string RussianResx =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.UI", "Resources", "Strings.ru.resx"));

    [Fact]
    public void TheWindows_IncludeTheSharedGrammar()
    {
        foreach (var (name, source) in new[] { ("positions panel", PositionsXaml), ("order history", HistoryXaml) })
        {
            source.Should().Contain(
                "<StyleInclude Source=\"avares://ZettaTrade.App/Themes/RowGrammar.axaml\" />",
                "the {0} table rides the shared scaffold, never a window-local copy", name);
        }

        // The grammar owns every class the windows bind: the header rows,
        // the hairline rows, the side-chip states and the action weights.
        foreach (var selector in new[]
                 {
                     "Border.table-header-row", "Border.table-row",
                     "Border.chip.buy", "Border.chip.sell",
                     "Button.row-action", "Button.row-action.secondary",
                     "Button.row-action.destructive",
                 })
        {
            GrammarXaml.Should().Contain(
                $"<Style Selector=\"{selector}\">",
                "the {0} shape lives once in the shared grammar", selector);
        }
    }

    [Fact]
    public void ThePositionsPanel_SplitsItsThreeSectionsByTabs()
    {
        var source = PositionsXaml;

        Regex.Matches(source, "<TabControl").Should().HaveCount(
            1, "the stacked one-scroll scaffold is gone");
        Regex.Matches(source, "<TabItem ").Should().HaveCount(
            3, "the three sections (positions / open orders / session journal) own a tab each");

        // The tab captions are the resx vocabulary (en + ru); the three
        // section headers they replace stay dropped — the tab IS the
        // section caption now.
        foreach (var resx in new[] { EnglishResx, RussianResx })
        {
            foreach (var key in new[] { "Positions.TabPositions", "Positions.TabOpenOrders", "Positions.TabRecent" })
            {
                resx.Should().Contain(
                    $"\"{key}\"",
                    "the tab captions ride the resx in both cultures");
            }

            foreach (var dropped in new[] { "Positions.PositionsHeader", "Positions.OpenOrdersHeader", "Positions.RecentHeader" })
            {
                resx.Should().NotContain(
                    $"\"{dropped}\"",
                    "the in-tab section header is redundant with the tab caption");
            }
        }
    }

    [Fact]
    public void TheSideCells_AreStateChips()
    {
        // One chip per row surface: the three positions-panel sections and
        // the history table; each binds both state classes to the row's
        // computed side state (the VM fact rides the view-model tests).
        Regex.Matches(PositionsXaml, "Classes.buy=\"\\{Binding IsBuySide\\}\"")
            .Should().HaveCount(3, "the positions panel renders one side chip per section (T-157)");
        Regex.Matches(PositionsXaml, "Classes.sell=\"\\{Binding IsSellSide\\}\"")
            .Should().HaveCount(3, "the chips carry both side states");
        Regex.Matches(HistoryXaml, "Classes.buy=\"\\{Binding IsBuySide\\}\"")
            .Should().HaveCount(1, "the history table's side cell is the same chip");
        Regex.Matches(HistoryXaml, "Classes.sell=\"\\{Binding IsSellSide\\}\"")
            .Should().HaveCount(1, "the chips carry both side states");

        // The state fills are the SAME pair the ticket's segmented side
        // speaks — the keys the T-152 contrast publisher scores, so the
        // derived readable caption stays legible under every palette.
        ExtractStyleBlock(GrammarXaml, "Border.chip.buy").Should().Contain(
            "<Setter Property=\"Background\" Value=\"{DynamicResource CandleUp}\" />",
            "the long chip rides the up-candle fill");
        ExtractStyleBlock(GrammarXaml, "Border.chip.sell").Should().Contain(
            "<Setter Property=\"Background\" Value=\"{DynamicResource Error}\" />",
            "the short chip rides the danger fill");
    }

    [Fact]
    public void TheHeaderRowsAndRowSeparators_RideTheGrammar()
    {
        // One emphasized header row and one row scaffold per table surface.
        Regex.Matches(PositionsXaml, "<Border Classes=\"table-header-row\"")
            .Should().HaveCount(3, "each of the three sections emphasizes its header row");
        Regex.Matches(PositionsXaml, "<Border Classes=\"table-row\"")
            .Should().HaveCount(3, "each section's rows ride the hairline scaffold");
        Regex.Matches(HistoryXaml, "<Border Classes=\"table-header-row\"")
            .Should().HaveCount(1, "the history table emphasizes its header row");
        Regex.Matches(HistoryXaml, "<Border Classes=\"table-row\"")
            .Should().HaveCount(1, "the history rows ride the hairline scaffold");

        // The hairline separators ride the palette's Border key in both
        // shapes (header strip + data rows), so a user theme retints them.
        foreach (var selector in new[] { "Border.table-header-row", "Border.table-row" })
        {
            var block = ExtractStyleBlock(GrammarXaml, selector);
            block.Should().Contain(
                "<Setter Property=\"BorderThickness\" Value=\"0,0,0,1\" />",
                "the {0} carries the bottom hairline", selector);
            block.Should().Contain(
                "<Setter Property=\"BorderBrush\" Value=\"{DynamicResource Border}\" />",
                "the {0}'s hairline rides the palette Border key", selector);
        }
    }

    [Fact]
    public void TheNumericColumns_RightAlignOnTheSharedStack()
    {
        // Alignment rides the cells directly beside the T-155 NumericFont
        // attribute (the token test owns the font counts): the positions
        // panel carries 10 numeric data cells (3 + 3 + 4) and 10 matching
        // header cells; the history window 4 data + 4 headers.
        Regex.Matches(PositionsXaml, "TextAlignment=\"Right\"")
            .Should().HaveCount(20, "every numeric column (data + header) right-aligns: 3 positions + 3 open orders + 4 session journal cells and their 10 headers");
        Regex.Matches(HistoryXaml, "TextAlignment=\"Right\"")
            .Should().HaveCount(8, "the history's opened/quantity/price/fee columns right-align, data and headers alike");

        // The acceptance is the pairing: every NumericFont CELL in these
        // tables also carries the right alignment (the reduce input is a
        // field, not a column — it rides the stack without the alignment).
        foreach (var (name, source, numericCells, inputFields) in new[]
                 {
                     ("positions panel", PositionsXaml, 10, 1),
                     ("order history", HistoryXaml, 4, 0),
                 })
        {
            var fontLines = Regex.Matches(source, "<TextBlock [^>]*FontFamily=\"\\{StaticResource NumericFont\\}\"[^>]*/>")
                .Select(m => m.Value)
                .ToList();
            fontLines.Should().HaveCount(
                numericCells,
                "the {0}'s numeric cells are self-closing text blocks on the shared stack", name);
            fontLines.Count(line => line.Contains("TextAlignment=\"Right\""))
                .Should().Be(numericCells, "every numeric CELL of the {0} right-aligns", name);

            var inputFieldsCount = Regex.Matches(source, "<TextBox [^>]*FontFamily=\"\\{StaticResource NumericFont\\}\"")
                .Count;
            inputFieldsCount.Should().Be(inputFields, "the {0}'s numeric INPUT fields ride the stack without the column alignment", name);
        }
    }

    [Fact]
    public void TheActionHierarchy_KeepsTheDestructiveSubdued()
    {
        var source = PositionsXaml;

        // Three weights on the rows: the default carries the row's main
        // move (the Close, the two row Cancels and the bulk cancel), the
        // secondary is the quiet outlined sibling (the Reduce, the two
        // Amends), and the destructive is EXACTLY one — the two-step gated
        // Reverse. One destructive per panel means the subdued ghost is
        // reserved for the flip; the confirmation gate itself is the
        // view-model's, and the action command tests own that fact.
        Regex.Matches(source, "Classes=\"row-action\"")
            .Should().HaveCount(4, "the default weight: Close, the two row Cancels, the bulk cancel");
        Regex.Matches(source, "Classes=\"row-action secondary\"")
            .Should().HaveCount(3, "the secondary weight: Reduce, the two Amends");
        Regex.Matches(source, "Classes=\"row-action destructive\"")
            .Should().HaveCount(1, "only the Reverse is destructive — never one of a crowd");

        // Subdued means no state fill ever: the destructive stays a ghost
        // whose danger reads in the text tint only — Reverse cannot be the
        // busiest element on its row because no style paints it loud. The
        // setters read across the whole family (the hover and the pressed
        // blocks only restate what the rest state already owns).
        var destructiveBlocks = Regex.Matches(
                GrammarXaml,
                "<Style Selector=\"Button\\.row-action\\.destructive[^\"]*\">(?:.*?\n)*?    </Style>")
            .Select(m => m.Value)
            .ToList();
        destructiveBlocks.Should().HaveCount(
            4, "the destructive family: the rest state, the hover, the pressed and the focus re-arm");

        var backgroundSetters = destructiveBlocks
            .SelectMany(block => Regex.Matches(block, "<Setter Property=\"Background\" Value=\"([^\"]+)\""))
            .Select(m => m.Groups[1].Value)
            .ToList();
        backgroundSetters.Should().NotBeEmpty("the family always owns its fill plan")
            .And.OnlyContain(
                value => value == "Transparent",
                "the destructive is a subdued ghost: no state fill ever");

        var borderSetters = destructiveBlocks
            .SelectMany(block => Regex.Matches(block, "<Setter Property=\"BorderBrush\" Value=\"([^\"]+)\""))
            .Select(m => m.Groups[1].Value)
            .ToList();
        borderSetters.Should().NotBeEmpty("the family always owns its border plan")
            .And.OnlyContain(
                value => value == "Transparent" || value == "{DynamicResource Highlight}",
                "the borders stay invisible at rest; only the T-110 focus ring re-arms them");

        foreach (var block in destructiveBlocks)
        {
            block.Should().NotContain(
                "{DynamicResource Accent}",
                "the destructive never takes the hover fill");
        }

        // The rest state is the quiet text; the hover tints the text with
        // the danger color (the same reading the strip-close icon has).
        ExtractStyleBlock(GrammarXaml, "Button.row-action.destructive").Should().Contain(
            "<Setter Property=\"Foreground\" Value=\"{DynamicResource TextSecondary}\" />",
            "the destructive rests as quiet text");
        ExtractStyleBlock(GrammarXaml, "Button.row-action.destructive:pointerover").Should().Contain(
            "<Setter Property=\"Foreground\" Value=\"{DynamicResource Error}\" />",
            "the danger reads on hover only");
        ExtractStyleBlock(GrammarXaml, "Button.row-action.destructive:focus").Should().Contain(
            "<Setter Property=\"BorderBrush\" Value=\"{DynamicResource Highlight}\" />",
            "the transparent rest border keeps the T-110 focus ring reachable");
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
