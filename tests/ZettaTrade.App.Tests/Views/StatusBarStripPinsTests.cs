namespace ZettaTrade.App.Tests.Views;

using System;
using System.IO;
using FluentAssertions;
using Xunit;

/// <summary>
/// The status strip's declutter pins (T-153): the strip carries at most the
/// five named groups (the message channel, the exchange picker, the active
/// exchange + lane chip, the connection state, the health dots) and the
/// removed labels (theme, language, version) are gone from it — each one
/// stays reachable in one action (settings, Help/About). The repo has no
/// headless render harness yet, so the facts ride the source of truth (the
/// chrome/accessibility pins' precedent; the queued T-144 headless lane
/// succeeds this file when it lands).
/// </summary>
public sealed class StatusBarStripPinsTests
{
    private static readonly string RepoRoot = FindRepositoryRoot();

    private static string MainWindowXaml =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "MainWindow.axaml"));

    private static string DisclaimerXaml =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "DisclaimerDialogWindow.axaml"));

    [Fact]
    public void TheStatusStrip_RidesExactlyTheFiveGroupColumns()
    {
        var source = MainWindowXaml;

        source.Should().Contain(
            "ColumnDefinitions=\"*,Auto,Auto,Auto,Auto\"",
            "the strip carries exactly five groups: the message channel, the picker, the exchange + lane chip, the connection state, the health dots");
        source.Should().NotContain(
            "ColumnDefinitions=\"*,Auto,Auto,Auto,Auto,Auto,Auto,Auto\"",
            "the eight-column strip (with the theme, language and version cells) is the pre-declutter shape");
    }

    [Fact]
    public void TheRemovedLabels_AreGoneFromTheStrip()
    {
        var source = MainWindowXaml;

        source.Should().NotContain(
            "{Binding CurrentThemeName}",
            "the theme name moved to the settings (it already switches there)");
        source.Should().NotContain(
            "{Binding CurrentLanguage}",
            "the language name moved to the settings (it already switches there)");
        source.Should().NotContain(
            "{Binding AppVersion}",
            "the version moved to Help/About");
    }

    [Fact]
    public void TheMessageChannel_RidesTheStripAndTheAboutDialogCarriesTheVersion()
    {
        MainWindowXaml.Should().Contain(
            "{Binding StatusMessage}",
            "the strip's first cell is the message channel (a transient event message over the Ready idle text)");
        DisclaimerXaml.Should().Contain(
            "{Binding VersionText}",
            "the removed version label stays reachable in one action: Help/About shows it");
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
