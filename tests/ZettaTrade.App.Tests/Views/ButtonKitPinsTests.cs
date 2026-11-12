namespace ZettaTrade.App.Tests.Views;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

/// <summary>
/// The button-kit adoption pins (T-183): the Livery 0.1.11 vocabulary
/// rides the windows — the ONE primary action per window (the adopted set
/// pinned by name, the confirmation gate pinned primary-free by design: a
/// safety gate never steers the user toward the risky choice), the toast
/// rows on the quiet carrier with the chrome living in exactly one of the
/// two layers (the card), the card's own state lanes present, and the
/// adopted surfaces' rounding riding the corner tokens — the count
/// badge keeps its own documented badge shape (a bare radius 6, the
/// T-152 vocabulary, not a button-kit surface) and is the template's
/// only radius literal.
/// </summary>
public sealed class ButtonKitPinsTests
{
    private static readonly string RepoRoot = FindRepositoryRoot();

    private static readonly string AppProjectDirectory =
        Path.Combine(RepoRoot, "src", "ZettaTrade.App");

    /// <summary>The windows whose ONE main action carries the primary class.</summary>
    private static readonly IReadOnlyDictionary<string, string> PrimaryWindows =
        new Dictionary<string, string>
        {
            ["SettingsWindow"] = "the settings' Save",
            ["BacktestWindow"] = "the backtest's Start",
            ["TradeStatsWindow"] = "the stats' Apply",
            ["AmendOrderDialogWindow"] = "the ticket's Amend",
            ["FirstRunWizardWindow"] = "the wizard's Finish",
            ["DisclaimerDialogWindow"] = "the disclaimer's Acknowledge",
            ["CrashDialogWindow"] = "the crash dialog's Dismiss",
            ["LicenseActivationWindow"] = "the license Activate",
        };

    /// <summary>The windows pinned primary-free: the confirmation gate
    /// keeps equal-weight buttons (a safety gate never steers), the main
    /// window carries no modal main action, and the ticket's CTA keeps
    /// its own state-filled trading vocabulary.</summary>
    private static readonly string[] PrimaryFreeWindows =
    [
        "ConfirmationDialogWindow",
        "MainWindow",
        "NewOrderDialogWindow",
    ];

    private static string ViewsPath(string window) =>
        Path.Combine(AppProjectDirectory, "Views", $"{window}.axaml");

    private static string MainWindowSource =>
        File.ReadAllText(ViewsPath("MainWindow"));

    private static int CountPrimaryButtons(string source) =>
        Regex.Matches(source, @"Classes=""(?<classes>[^""]*\bprimary\b[^""]*)""").Count;

    [Fact]
    public void EveryWindow_CarriesAtMostOnePrimaryAction()
    {
        var files = Directory
            .EnumerateFiles(AppProjectDirectory, "*.axaml", SearchOption.AllDirectories)
            .ToList();

        files.Should().HaveCountGreaterThan(
            20,
            "the walk covers the whole App project (a shrunk walk is a false green)");

        var offenders = files
            .Where(file => CountPrimaryButtons(File.ReadAllText(file)) > 1)
            .ToList();

        offenders.Should().BeEmpty(
            "the primary marks ONE main action per window: {0}",
            string.Join(", ", offenders));
    }

    [Fact]
    public void TheAdoptedWindows_CarryExactlyTheirNamedPrimary()
    {
        foreach (var (window, role) in PrimaryWindows)
        {
            var source = File.ReadAllText(ViewsPath(window));

            CountPrimaryButtons(source).Should().Be(
                1,
                "the {0}'s main action ({1}) is the window's one primary", window, role);
        }
    }

    [Fact]
    public void TheSafetyGateStaysEqualWeight_AndTheOtherFreeWindowsCarryNoPrimary()
    {
        foreach (var window in PrimaryFreeWindows)
        {
            var source = File.ReadAllText(ViewsPath(window));

            CountPrimaryButtons(source).Should().Be(
                0,
                "the {0} carries no primary: the gate never steers, the workspace and the ticket keep their own vocabularies",
                window);
        }
    }

    [Fact]
    public void TheToastRows_RideTheQuietCarrierWithTheChromeInTheCard()
    {
        var main = MainWindowSource;

        // The quiet carrier: the toast Button suppresses its own chrome and
        // the card carries the chrome — exactly one of the two layers.
        var template = TemplateRegion(main);
        template.Should().Contain(
            "<Button Classes=\"quiet\"",
            "the toast row's Button is the kit's quiet carrier (chrome-free at every state)");
        template.Should().Contain(
            "<Border Classes=\"toast-card\">",
            "the row's chrome lives in the card layer");

        // One radius literal in the whole template, and it is the badge's
        // own documented shape: the count badge keeps the T-152 vocabulary
        // (radius 6, not a button-kit surface), so the button and card
        // layers carry no bare radius literal — their rounding rides the
        // corner tokens in the window lanes below.
        template.Should().MatchRegex(
            @"CornerRadius=""6""",
            "the count badge keeps its own documented badge shape (the T-152 vocabulary)");
        Regex.Matches(template, @"CornerRadius=""").Should().HaveCount(
            1,
            "the badge's literal is the template's only radius literal: the button/card layers carry no bare radius — their rounding rides the corner tokens");

        // The card's chrome rides the window lanes: the rest fill here, the
        // states beside it, the rounding on the CornerL token.
        var cardBlock = BlockOf(main, "Border.toast-card");
        cardBlock.Should().Contain(
            "<Setter Property=\"Background\" Value=\"{DynamicResource Surface}\" />",
            "the card's rest fill is the planar Surface");
        cardBlock.Should().Contain(
            "<Setter Property=\"BorderBrush\" Value=\"{DynamicResource Border}\" />",
            "the card's rest stroke is the hairline Border");
        cardBlock.Should().Contain(
            "<Setter Property=\"CornerRadius\" Value=\"{StaticResource CornerL}\" />",
            "the card rounding rides the CornerL token");
        cardBlock.Should().Contain(
            "<Setter Property=\"Padding\" Value=\"12,4\" />",
            "the card padding keeps its on-scale steps");

        // The card answers the pointer itself: the derived tints on
        // hover/press, the Highlight ring on keyboard focus.
        BlockOf(main, "Button.quiet:pointerover > Border.toast-card").Should().Contain(
            "<Setter Property=\"Background\" Value=\"{DynamicResource ButtonHoverTint}\" />",
            "the card's hover is the derived soft tint");
        BlockOf(main, "Button.quiet:pressed > Border.toast-card").Should().Contain(
            "<Setter Property=\"Background\" Value=\"{DynamicResource ButtonPressedTint}\" />",
            "the card's press is the stronger tint step");
        BlockOf(main, "Button.quiet:focus > Border.toast-card").Should().Contain(
            "<Setter Property=\"BorderBrush\" Value=\"{DynamicResource Highlight}\" />",
            "the keyboard focus ring lands on the card — the marker the chrome-free button cannot draw itself");
    }

    /// <summary>The toast row's DataTemplate region (the adopted surface).</summary>
    private static string TemplateRegion(string source)
    {
        var open = source.IndexOf(
            "<DataTemplate x:DataType=\"vm:NotificationRow\">", StringComparison.Ordinal);
        open.Should().BeGreaterThanOrEqualTo(0, "the toast template must exist");
        var close = source.IndexOf("</DataTemplate>", open, StringComparison.Ordinal);
        return source[open..close];
    }

    /// <summary>The body of the named Style block, opening tag included.</summary>
    private static string BlockOf(string source, string selector)
    {
        var open = source.IndexOf($"<Style Selector=\"{selector}\">", StringComparison.Ordinal);
        open.Should().BeGreaterThanOrEqualTo(0, $"the block <Style Selector=\"{selector}\"> must exist");
        var end = source.IndexOf("</Style>", open, StringComparison.Ordinal);
        return source[open..(end + "</Style>".Length)];
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
