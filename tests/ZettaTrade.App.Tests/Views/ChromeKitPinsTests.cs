namespace ZettaTrade.App.Tests.Views;

using System;
using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

/// <summary>
/// The chrome-kit consumer pins (T-154): the shared vocabulary lives in
/// the Livery package (its own repo pins the class shapes), so the views
/// must carry ZERO copy-pasted equivalents — the chart toolbar's five
/// `.on` style blocks, the four chip/badge style copies and the health
/// strip's dot styles are gone, and the settings "i" cues are the
/// InfoIcon control, focusable and automation-named (the a11y step up).
/// The repo has no headless render harness yet, so the facts ride the
/// source of truth (the chrome/accessibility pins' precedent).
/// </summary>
public sealed class ChromeKitPinsTests
{
    private static readonly string RepoRoot = FindRepositoryRoot();

    private static string ChartPanelXaml =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "Panels", "ChartPanelView.axaml"));

    private static string MainWindowXaml =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "MainWindow.axaml"));

    private static string SettingsXaml =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "SettingsWindow.axaml"));

    [Fact]
    public void TheChartToolbar_TogglesRideTheSharedOnClass()
    {
        var source = ChartPanelXaml;

        // The eight armed toggles keep their dynamic class assignments
        // (the two overlay toggles plus the six drawing tools).
        Regex.Matches(source, @"Classes\.on=""\{Binding [^""]+\}""")
            .Should().HaveCount(8, "the toolbar's eight toggles arm through the shared .on class");

        source.Should().NotContain(
            "<Style Selector=\"Button.on\">",
            "the .on style copy lives once in the Livery kit, never per-button");
    }

    [Fact]
    public void TheChipsAndDots_RideTheSharedClasses()
    {
        MainWindowXaml.Should().NotContain(
            "<Border.Styles>",
            "the lane chip's state styles live in the Livery chip class");
        MainWindowXaml.Should().NotContain(
            "<Ellipse.Styles>",
            "the health dots' state styles live in the Livery dot class");
        MainWindowXaml.Should().Contain(
            "Classes=\"dot\"",
            "the health dots namespace their states under the shared dot class");

        foreach (var window in new[] { "NewOrderDialogWindow", "PositionsPanelWindow", "OrderHistoryWindow" })
        {
            File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", $"{window}.axaml"))
                .Should().NotContain(
                    "<Border.Styles>",
                    "the {0} testnet badge's style copy lives in the Livery chip class", window);
        }
    }

    [Fact]
    public void TheSettingsCues_AreTheInfoIconControl()
    {
        var source = SettingsXaml;

        Regex.Matches(source, "<livery:InfoIcon")
            .Should().HaveCount(7, "all seven indicator cues are the shared control");
        source.Should().NotContain(
            "<TextBlock Text=\"i\"",
            "the hand-built Border+TextBlock cue copies are gone");

        // The a11y step up: every cue carries its automation name.
        Regex.Matches(source, "AutomationProperties.Name=\"{loc:Localized Chart.Indicators.A11y")
            .Should().HaveCount(7, "every InfoIcon cue is named for assistive tech");
    }

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
