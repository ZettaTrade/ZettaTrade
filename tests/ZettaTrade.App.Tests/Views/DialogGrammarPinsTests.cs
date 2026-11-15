namespace ZettaTrade.App.Tests.Views;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

/// <summary>
/// The dialog-grammar pins (T-234): the window family rides the settings'
/// variant-A grammar (the T-222/T-223 approved reference). The fixed-frame
/// era ends app-wide — no view ships <c>CanResize="False"</c> and every
/// dialog declares the resizable one; the paired confirm strips speak the
/// settings footer's shape (a right-aligned row riding its own spacing,
/// the quiet button first, the ONE primary last, the 90 strip) with the
/// confirmation gate the honest equal-weight exception (the T-183
/// discipline: a safety gate never steers); the strip-gap margin hacks
/// are gone (the rows clear their gaps through the parent spacing); the
/// annotation prompt joins the styled chrome; the carded surfaces (the
/// wizard's steps, the ticket's quick-size card) speak the section-card
/// voice with the uppercase header captions in both cultures; the command
/// toolbars ride the T-212 widths. The facts ride the source of truth
/// (the T-145/T-161 precedent) beside the headless materialization facts
/// in the Headless lane.
/// </summary>
public sealed class DialogGrammarPinsTests
{
    private static readonly string RepoRoot = FindRepositoryRoot();

    private static readonly string AppProjectDirectory =
        Path.Combine(RepoRoot, "src", "ZettaTrade.App");

    /// <summary>
    /// The dialog family the task unified — every window that shipped the
    /// fixed frame before T-234.
    /// </summary>
    private static readonly string[] DialogFamily =
    [
        "AmendOrderDialogWindow",
        "ConfirmationDialogWindow",
        "CrashDialogWindow",
        "DisclaimerDialogWindow",
        "FirstRunWizardWindow",
        "KillSwitchReportWindow",
        "LicenseActivationWindow",
        "NewOrderDialogWindow",
        "PaperResetDialogWindow",
        "ProtectiveLevelsDialogWindow",
        "TextAnnotationWindow",
        "TradeNoteDialogWindow",
    ];

    /// <summary>
    /// The paired confirm strips and the primary class their LAST button
    /// carries. The confirmation gate names none: the strip unifies the
    /// shape while the buttons stay equal-weight (the T-183 discipline).
    /// </summary>
    private static readonly (string Window, string PrimaryClass)[] PairedStrips =
    [
        ("AmendOrderDialogWindow", "primary"),
        ("TradeNoteDialogWindow", "primary"),
        ("PaperResetDialogWindow", "primary"),
        ("ProtectiveLevelsDialogWindow", "primary"),
        ("LicenseActivationWindow", "primary"),
        ("NewOrderDialogWindow", "cta"),
        ("ConfirmationDialogWindow", ""),
    ];

    /// <summary>
    /// The carded surfaces: window → the card count the section grammar
    /// speaks there (the wizard's three steps, the ticket's quick-size
    /// card).
    /// </summary>
    private static readonly (string Window, int Cards)[] CardedSurfaces =
    [
        ("FirstRunWizardWindow", 3),
        ("NewOrderDialogWindow", 1),
    ];

    /// <summary>The card headers ride the section voice: the tracked
    /// uppercase caption ships uppercase in both cultures.</summary>
    private static readonly string[] CardHeaderKeys =
    [
        "Wizard.Theme.Header",
        "Wizard.Adapter.Header",
        "Wizard.Symbol.Header",
        "NewOrder.SizerBalanceCaption",
    ];

    private static string ViewSource(string window) =>
        File.ReadAllText(Path.Combine(AppProjectDirectory, "Views", $"{window}.axaml"));

    private static IEnumerable<string> AllViewSources() =>
        Directory
            .EnumerateFiles(AppProjectDirectory, "*.axaml", SearchOption.AllDirectories)
            .Select(File.ReadAllText);

    [Fact]
    public void TheFixedFrameEra_IsOver()
    {
        foreach (var source in AllViewSources())
        {
            source.Should().NotContain(
                "CanResize=\"False\"",
                "the settings pin declared the fixed-frame era over at T-222 — every window resizes");
        }

        foreach (var window in DialogFamily)
        {
            ViewSource(window).Should().Contain(
                "CanResize=\"True\"",
                "the {0} declares the resizable frame explicitly (the tool windows' shape)", window);
        }
    }

    [Fact]
    public void TheConfirmStrips_RideTheSettingsShape()
    {
        foreach (var (window, primaryClass) in PairedStrips)
        {
            var source = ViewSource(window);

            source.Should().Contain(
                "<StackPanel Orientation=\"Horizontal\" HorizontalAlignment=\"Right\" Spacing=\"{StaticResource SpaceM}\">",
                "the {0}'s confirm strip is the settings footer's shape: one right-aligned row riding its own spacing", window);

            var strip = StripRegionOf(source);
            var buttons = ButtonAttributesOf(strip).ToList();
            buttons.Should().HaveCountGreaterThanOrEqualTo(
                2,
                "the {0}'s confirm strip is a paired row", window);

            // The 90 strip: every strip button rides the settings width.
            foreach (var button in buttons)
            {
                button.Should().Contain(
                    "MinWidth=\"90\"",
                    "the {0}'s strip rides the 90 width (the settings footer's step)", window);
            }

            if (primaryClass.Length == 0)
            {
                // The safety gate: equal weight, and the quiet cancel still
                // leads the row (the strip's own grammar).
                var first = buttons[0];
                first.Should().NotContain("primary", "the confirmation gate carries no primary (the T-183 discipline)");
                buttons[^1].Should().NotContain("primary", "the confirmation gate carries no primary (the T-183 discipline)");
                first.Should().NotContain("cta");
            }
            else
            {
                // Quiet first, the ONE primary last.
                buttons[0].Should().NotContain(
                    primaryClass,
                    "the {0}'s quiet button leads the strip", window);
                buttons[^1].Should().MatchRegex(
                    $"Classes=\"[^\"]*\\b{primaryClass}\\b",
                    "the {0}'s ONE primary action closes the strip", window);
            }
        }
    }

    [Fact]
    public void TheStripGapHacks_AreGone()
    {
        foreach (var source in AllViewSources())
        {
            source.Should().NotContain(
                "Margin=\"0,8,0,0\"",
                "a pre-strip gap rides the parent row's spacing, never a composite margin (T-234)");
            source.Should().NotContain(
                "Margin=\"0,0,8,0\"",
                "an intra-pair gap rides the strip's spacing, never a composite margin (T-234)");
        }
    }

    [Fact]
    public void TheAnnotationPrompt_JoinsTheStyledChrome()
    {
        foreach (var source in AllViewSources())
        {
            source.Should().NotContain(
                "WindowDecorations=\"Full\"",
                "the styled chrome owns the window decorations (the chrome-compact lane)");
        }

        var annotation = ViewSource("TextAnnotationWindow");
        annotation.Should().Contain(
            "Classes=\"chrome-compact\"",
            "the annotation prompt rides the compact chrome every dialog speaks");
    }

    [Fact]
    public void TheCardedSurfaces_SpeakTheSectionGrammar()
    {
        foreach (var (window, cards) in CardedSurfaces)
        {
            var source = ViewSource(window);

            Regex.Matches(source, "Classes=\"section-card\"")
                .Should().HaveCount(
                    cards,
                    "the {0} wraps its field groups in the settings' card voice", window);
            Regex.Matches(source, "Classes=\"section-tick\"")
                .Should().HaveCount(cards, "every card speaks the tick-and-caption header");
            Regex.Matches(source, "Classes=\"section-header\"")
                .Should().HaveCount(cards, "every card speaks the tick-and-caption header");
            source.Should().Contain(
                "avares://ZettaTrade.App/Themes/SectionCards.axaml",
                "the {0} includes the section-card dictionary once", window);
        }
    }

    [Fact]
    public void TheCardHeaders_ShipUppercase_InBothCultures()
    {
        foreach (var (table, name) in new[] { (ResxPath("Strings.resx"), "en"), (ResxPath("Strings.ru.resx"), "ru") })
        {
            var tableText = File.ReadAllText(table);
            foreach (var key in CardHeaderKeys)
            {
                var value = ResxValueOf(tableText, key);
                value.Should().NotBeNullOrEmpty("the {0} table carries the {1} header", name, key);
                value.Should().Be(
                    value.ToUpperInvariant(),
                    "the {0}'s {1} header rides the section voice: the tracked uppercase caption", name, key);
            }
        }
    }

    [Fact]
    public void TheWizardNav_RidesOneStrip_QuietWalksAndTheOneFinish()
    {
        var wizard = ViewSource("FirstRunWizardWindow");

        // The walk's four verbs ride the 90 strip.
        Regex.Matches(wizard, "MinWidth=\"90\"")
            .Should().HaveCount(
                4,
                "the wizard's back/skip/next/finish all ride the 90 strip");
        wizard.Should().Contain(
            "<Grid ColumnDefinitions=\"Auto,*,Auto\">",
            "the nav row is one strip: the quiet walks at the left edge, the advance and the ONE primary finish at the right");
        wizard.Should().Contain(
            "<Button Classes=\"primary\"",
            "the finish is the walk's ONE primary");
        Regex.Matches(wizard, "Classes=\"primary\"")
            .Should().HaveCount(
                1,
                "the wizard carries exactly one primary (the finish)");
    }

    [Fact]
    public void TheCommandToolbars_RideTheT212Widths()
    {
        var alerts = ViewSource("AlertsWindow");

        // T-235: the editor's combos joined the MinWidth vocabulary (the
        // content-width floors), so the toolbar widths count their own
        // strip — the T-212 language is the toolbar's, the editor's floors
        // are another lane (the FieldGrammarPinsTests hold those).
        var alertsToolbar = ToolbarRegionOf(alerts);
        Regex.Matches(alertsToolbar, "MinWidth=\"110\"")
            .Should().HaveCount(
                4,
                "the alerts toolbar's commands (create, update, snooze, delete) ride the 110 command width");
        Regex.Matches(alertsToolbar, "MinWidth=\"90\"")
            .Should().HaveCount(1, "the alerts' close rides the 90 quiet width");
        alertsToolbar.Should().NotContain(
            "MinWidth=\"100\"",
            "the scattered 100 widths are the drift this task retired");
        alerts.Should().NotContain(
            "Margin=\"8,0,0,0\"",
            "the toolbar's gap rides the row's own column spacing, never a composite margin");

        // The fresh T-212 backtest reference stays the pinned shape.
        var backtest = ViewSource("BacktestWindow");
        Regex.Matches(backtest, "MinWidth=\"110\"")
            .Should().HaveCount(5, "the backtest's start/sweep/walk-forward/Monte Carlo/cancel commands ride the 110 command width (T-254 adds the walk-forward and Monte Carlo starts beside the run/sweep/cancel trio)");
        Regex.Matches(backtest, "MinWidth=\"90\"")
            .Should().HaveCount(1, "the backtest's close rides the 90 quiet width");
    }

    /// <summary>
    /// The confirm-strip region: from the settings-shaped opener to its
    /// closing tag (the strips carry no nested StackPanel, so the first
    /// close closes the strip).
    /// </summary>
    private static string StripRegionOf(string source)
    {
        var open = source.IndexOf(
            "<StackPanel Orientation=\"Horizontal\" HorizontalAlignment=\"Right\" Spacing=\"{StaticResource SpaceM}\">",
            StringComparison.Ordinal);
        open.Should().BeGreaterThanOrEqualTo(0, "the confirm strip must exist");
        var close = source.IndexOf("</StackPanel>", open, StringComparison.Ordinal);
        return source[open..close];
    }

    /// <summary>
    /// The command toolbar region: from the T-212 widths grid's opening
    /// tag to its closing tag (the window-wide MinWidth counts went
    /// brittle when the T-235 editor combos joined the MinWidth
    /// vocabulary — the toolbar pins count their own strip, the editor's
    /// content-width floors are another lane).
    /// </summary>
    private static string ToolbarRegionOf(string source)
    {
        var open = source.IndexOf(
            "<Grid Grid.Row=\"3\" ColumnDefinitions=\"*,Auto,Auto,Auto,Auto,Auto\"",
            StringComparison.Ordinal);
        open.Should().BeGreaterThanOrEqualTo(0, "the command toolbar must exist");
        var close = source.IndexOf("</Grid>", open, StringComparison.Ordinal);
        return source[open..close];
    }

    /// <summary>The opening-tag text of every Button inside the region.</summary>
    private static IEnumerable<string> ButtonAttributesOf(string region) =>
        Regex.Matches(region, "<Button\\b[^>]*>")
            .Select(match => match.Value);

    private static string ResxPath(string fileName) =>
        Path.Combine(RepoRoot, "src", "ZettaTrade.UI", "Resources", fileName);

    /// <summary>The one value the named resx key carries.</summary>
    private static string ResxValueOf(string table, string key)
    {
        var matches = Regex.Matches(
            table,
            $"name=\"{Regex.Escape(key)}\" xml:space=\"preserve\">\\s*<value>([^<]*)</value>");
        matches.Should().HaveCount(1, "the resx carries {0} exactly once", key);
        return matches[0].Groups[1].Value;
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
