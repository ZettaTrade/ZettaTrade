namespace ZettaTrade.App.Tests.Views;

using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;
using ZettaTrade.UI.Resources;

/// <summary>
/// The window layout pins (T-145): the two regressions the user hit — the
/// chart toolbar painting over the book, tape and watchlist panels and the
/// settings dialog baking six tab headers into a fixed 560-wide frame —
/// ride the source of truth (the chrome/accessibility pins' precedent: the
/// repo has no headless render harness yet; the queued T-144 headless lane
/// succeeds this file when it lands).
/// </summary>
public sealed class WindowLayoutPinsTests
{
    private static readonly string RepoRoot = FindRepositoryRoot();

    private static string MainWindowXaml =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "MainWindow.axaml"));

    private static string SettingsXaml =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "SettingsWindow.axaml"));

    private static string ChartPanelXaml =>
        File.ReadAllText(Path.Combine(
            RepoRoot, "src", "ZettaTrade.App", "Views", "Panels", "ChartPanelView.axaml"));

    [Fact]
    public void TheChartToolbar_RidesAFiniteWrapBand()
    {
        var source = ChartPanelXaml;

        source.Should().Contain(
            "<WrapPanel Grid.Row=\"1\">",
            "the toolbar controls ride a finite-width wrap band that folds onto further lines");
        source.Should().NotContain(
            "ColumnDefinitions=\"*,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto\"",
            "the stale star + 12-auto header overflowed the chart column onto the neighbor panels");
        source.Should().NotContain(
            "ColumnDefinitions=\"*,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto\"",
            "the panel-hosted variant of the stale header (star + 15 auto columns) overflows the same way");
    }

    [Fact]
    public void TheChartToolbar_KeepsEveryActionWired()
    {
        var source = ChartPanelXaml;

        foreach (var command in new[]
                 {
                     "{Binding ToggleVwapCommand}",
                     "{Binding ToggleVolumeProfileCommand}",
                     "{Binding ExportCandlesCommand}",
                     "{Binding ExportPngCommand}",
                     "{Binding ExportVisibleCandlesCommand}",
                     "{Binding CompareCommand}",
                     "{Binding ToolCommand}",
                 })
        {
            source.Should().Contain(
                command,
                "the toolbar restructure must not drop a single wired chart action");
        }
    }

    [Fact]
    public void TheSettingsWindow_IsRoomyAndResizable()
    {
        var source = SettingsXaml;

        source.Should().Contain(
            "Width=\"760\" Height=\"720\"",
            "the dialog defaults bigger than the old fixed 560-wide frame");
        source.Should().Contain(
            "MinWidth=\"640\" MinHeight=\"560\"",
            "the resizable dialog keeps sane minimums");
        source.Should().Contain(
            "CanResize=\"True\"",
            "the dialog resizes with the pinned Save/Close row staying pinned");
        source.Should().NotContain(
            "CanResize=\"False\"",
            "the fixed-frame era is over");
    }

    [Fact]
    public void TheSettingsWindow_FoldsToFourTabs()
    {
        var source = SettingsXaml;

        Regex.Matches(source, "<TabItem").Should().HaveCount(
            4,
            "the six tabs fold: Appearance and Indicators stand, MCP+port ride Integrations, pairs+hotkeys ride Workspace");

        source.Should().Contain(
            "Header=\"{loc:Localized Settings.Tab.Integrations}\"",
            "the integrations tab carries the folded MCP + port surface");
        source.Should().Contain(
            "Header=\"{loc:Localized Settings.Tab.Workspace}\"",
            "the workspace tab carries the folded pairs + hotkeys surface");

        // The retired tab headers: gone from the tab strip (the new
        // Integrations/Workspace keys replaced them; the old keys may
        // survive only as section captions, never as Header=).
        source.Should().NotContain("Header=\"{loc:Localized Settings.Tab.Mcp}\"");
        source.Should().NotContain("Header=\"{loc:Localized Settings.Tab.Pairs}\"");
        source.Should().NotContain("Header=\"{loc:Localized Settings.Tab.Hotkeys}\"");
        source.Should().NotContain("Header=\"{loc:Localized Settings.Tab.Port}\"");
    }

    [Fact]
    public void TheMergedTabs_KeepEveryMigratedSurface()
    {
        var source = SettingsXaml;

        source.Should().Contain(
            "Text=\"{loc:Localized Settings.Tab.Mcp}\"",
            "the MCP section caption survives as the merged tab's section header");
        source.Should().Contain(
            "{loc:Localized Settings.McpClients.Add}",
            "the MCP client editor rode the integrations tab intact");
        source.Should().Contain(
            "{loc:Localized Port.Export}",
            "the settings port rode the integrations tab intact");
        source.Should().Contain(
            "IsVisible=\"{Binding IsPortAvailable}\"",
            "the port section keeps its own availability flag — a hidden port never hides MCP");
        source.Should().Contain(
            "{loc:Localized Settings.Pairs.All}",
            "the pairs lists rode the workspace tab intact");
        source.Should().Contain(
            "{loc:Localized Hotkeys.Editor.Hint}",
            "the hotkey editor rode the workspace tab intact");
    }

    [Theory]
    [InlineData("Settings.Tab.Integrations")]
    [InlineData("Settings.Tab.Workspace")]
    public void TheFoldedTabKeys_ResolveInBothCultures(string key)
    {
        Strings.ResourceManager.GetString(key, CultureInfo.InvariantCulture)
            .Should().NotBeNullOrEmpty("'{0}' is a user-facing tab strip string", key);
        Strings.ResourceManager.GetString(key, CultureInfo.GetCultureInfo("ru-RU"))
            .Should().NotBeNullOrEmpty("'{0}' must localize into the ru catalog", key);
    }

    /// <summary>
    /// Walk up from the test's base directory to the folder carrying the
    /// solution marker (the chrome/accessibility pins' walk: tests, gate
    /// runners and IDE runs all execute inside the repository).
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
