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

    private static string SectionCardsXaml =>
        File.ReadAllText(Path.Combine(
            RepoRoot, "src", "ZettaTrade.App", "Themes", "SectionCards.axaml"));

    private static string ViewModelSource =>
        File.ReadAllText(Path.Combine(
            RepoRoot, "src", "ZettaTrade.App", "ViewModels", "SettingsViewModel.cs"));

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
            "Width=\"880\" Height=\"640\"",
            "the approved variant-A frame: the 190 px rail column and the pinned footer strip ride the compact default (T-222)");
        source.Should().Contain(
            "MinWidth=\"720\" MinHeight=\"560\"",
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

        Regex.Matches(source, "<TabItem Classes=\"rail-item\"").Should().HaveCount(
            4,
            "the six tabs fold: Appearance and Indicators stand, MCP+port ride Integrations, pairs+hotkeys ride Workspace (the header compositions open their own TabItem.Header tags)");

        source.Should().Contain(
            "Text=\"{loc:Localized Settings.Tab.Integrations}\"",
            "the integrations tab carries the folded MCP + port surface (the header composition's caption)");
        source.Should().Contain(
            "Text=\"{loc:Localized Settings.Tab.Workspace}\"",
            "the workspace tab carries the folded pairs + hotkeys surface (the header composition's caption)");

        // The retired tab headers: gone from the strip (the folded
        // Integrations/Workspace captions ride the section-card headers).
        // T-222 moved every rail caption into a header composition's
        // TextBlock — no attribute header carries a loc key anymore.
        source.Should().NotContain(
            "Header=\"{loc:",
            "the rail captions ride the header compositions' TextBlocks, never attribute headers");
    }

    [Fact]
    public void TheMergedTabs_KeepEveryMigratedSurface()
    {
        var source = SettingsXaml;

        source.Should().Contain(
            "Text=\"{loc:Localized Settings.Section.Mcp}\"",
            "the MCP section header carries the merged section (the T-184 card header)");
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

    [Fact]
    public void TheSettingsTabs_RideTheCategoryRail()
    {
        var source = SettingsXaml;

        source.Should().Contain(
            "<TabControl Classes=\"rail\" TabStripPlacement=\"Left\" SelectedIndex=\"{Binding SelectedTabIndex}\">",
            "the rail is the tab strip on the left placement with the view-model index binding, riding the flush panel over the backing strip (T-222)");
        Regex.Matches(source, "<TabItem Classes=\"rail-item\"").Should().HaveCount(
            4,
            "every category item rides the rail-item class");
        source.Should().Contain(
            "<Style Selector=\"TabItem.rail-item\">",
            "the rail item shape is pinned by the window-scoped class");
        source.Should().Contain(
            "<Style Selector=\"TabItem.rail-item[TabStripPlacement=Left] /template/ Border#PART_SelectedPipe\">",
            "the selected tick is the theme's own left-pipe part, re-shaped (no template override)");
    }

    [Fact]
    public void TheSettingsPages_RideTheSectionCards()
    {
        var source = SettingsXaml;
        var cards = SectionCardsXaml;

        Regex.Matches(source, "<Border Classes=\"section-card\"").Should().HaveCount(
            9,
            "the nine sections: Appearance 3 (appearance + theme folder + first-run wizard), Indicators 1, Integrations 3 (MCP + AI chat + import/export), Workspace 2 (pairs + hotkeys)");
        Regex.Matches(source, "<Border Classes=\"section-tick\" />").Should().HaveCount(
            9,
            "every section card carries the Accent tick header");
        source.Should().Contain(
            "<StyleInclude Source=\"avares://ZettaTrade.App/Themes/SectionCards.axaml\" />",
            "the card shape ships once in the App themes and is included, never re-declared");
        source.Should().NotContain(
            "Selector=\"Border.section-card\"",
            "the window consumes the card classes — the shape must not be re-declared window-side");
        cards.Should().Contain(
            "<Style Selector=\"Border.section-card\">",
            "the section-card class carries the shared panel shape");
        cards.Should().Contain(
            "<Style Selector=\"Border.section-tick\">",
            "the section-tick class carries the 3 px Accent mark");
        cards.Should().Contain(
            "<Style Selector=\"TextBlock.section-header\">",
            "the section-header class carries the tracked uppercase caption");
        cards.Should().Contain(
            "<Setter Property=\"CornerRadius\" Value=\"{StaticResource CornerM}\" />",
            "the cards ride the approved compact CornerM rounding (the variant-A card step, T-222)");

        // The nine header keys ride the pages (one per card, exactly).
        foreach (var key in new[]
                 {
                     "Settings.Section.Appearance",
                     "Settings.Section.ThemesFolder",
                     "Settings.Section.Wizard",
                     "Settings.Section.Indicators",
                     "Settings.Section.Mcp",
                     "Settings.Section.Ai",
                     "Settings.Section.Port",
                     "Settings.Section.Pairs",
                     "Settings.Section.Hotkeys",
                 })
        {
            Regex.Matches(source, "loc:Localized " + key + "}").Should().HaveCount(
                1,
                "'{0}' is the one card header of its section", key);
        }

        // The update-check toggle (T-213) rides the appearance card as its
        // fourth row (the T-184 no-scroll pin bounds the page — a standalone
        // Application card would not fit the default size): the row rides
        // the pinned automation id.
        source.Should().Contain(
            "UpdateCheckEnabledCheckBox",
            "the update check's checkbox is the one pinned control of its row");
    }

    [Fact]
    public void TheSettingsWindow_RidesTheVariantAChrome()
    {
        var source = SettingsXaml;

        // The rail strip (T-222): the 190 px title-bar backing with its
        // right hairline, and the rail note speaking the categories'
        // shape at its bottom.
        source.Should().Contain(
            "x:Name=\"RailBacking\"",
            "the rail strip's backing is the named chrome element the render pins measure");
        source.Should().Contain(
            "Background=\"{DynamicResource TitleBar}\"",
            "the backing strip rides the title-bar fill (the approved variant-A rail)");
        source.Should().Contain(
            "BorderThickness=\"0,0,1,0\"",
            "the backing strip carries the right hairline");
        source.Should().Contain(
            "Settings.Rail.Note",
            "the rail note speaks the categories' shape at the strip's bottom");

        // The footer strip (T-222): the full-width pinned bar over the
        // title-bar fill with its top hairline, the quiet Close riding
        // before the ONE primary Save.
        source.Should().Contain(
            "x:Name=\"FooterBar\"",
            "the pinned footer is the named chrome element the render pins measure");
        source.Should().Contain(
            "BorderThickness=\"0,1,0,0\"",
            "the footer strip carries the top hairline");
        source.Should().Contain(
            "Padding=\"16,12\"",
            "the footer strip rides the approved inset");
        source.IndexOf("x:Name=\"CloseButton\"", StringComparison.Ordinal)
            .Should().BeLessThan(
                source.IndexOf("x:Name=\"SaveButton\"", StringComparison.Ordinal),
                "the quiet Close rides before the ONE primary Save (the approved order)");

        // The rail headers (T-222): the icons ride the header
        // compositions — the applied template renders no Icon presenter
        // (the probe measured it), and the strokes bind the item's own
        // foreground so the selection's re-ink reaches them.
        Regex.Matches(source, Regex.Escape("<TabItem.Header>")).Should().HaveCount(
            4,
            "every rail caption rides a header composition");
        Regex.Matches(source, Regex.Escape("{Binding $parent[TabItem].Foreground}")).Should().HaveCount(
            4,
            "every rail icon's main stroke rides the item's own foreground");
    }

    [Theory]
    [InlineData("Settings.Section.Appearance")]
    [InlineData("Settings.Section.ThemesFolder")]
    [InlineData("Settings.Section.Wizard")]
    [InlineData("Settings.Section.Indicators")]
    [InlineData("Settings.Section.Mcp")]
    [InlineData("Settings.Section.Ai")]
    [InlineData("Settings.Section.Port")]
    [InlineData("Settings.Section.Pairs")]
    [InlineData("Settings.Section.Hotkeys")]
    public void TheSectionHeaderKeys_ResolveInBothCultures(string key)
    {
        Strings.ResourceManager.GetString(key, CultureInfo.InvariantCulture)
            .Should().NotBeNullOrEmpty("'{0}' is a user-facing section header string", key);
        Strings.ResourceManager.GetString(key, CultureInfo.GetCultureInfo("ru-RU"))
            .Should().NotBeNullOrEmpty("'{0}' must localize into the ru catalog", key);
    }

    [Fact]
    public void TheRailSelection_IsViewStateOnly()
    {
        var source = ViewModelSource;

        source.Should().Contain(
            "public int SelectedTabIndex",
            "the rail's selected index is the view model's view state (T-184)");
        source.Should().Contain(
            "set => this.RaiseAndSetIfChanged(ref _selectedTabIndex, value);",
            "the rail selection raises through the ReactiveUI contract like every view-model property");
    }

    [Fact]
    public void TheInnerViewports_RideTheShippedCaps()
    {
        var source = SettingsXaml;

        // The inner scrollports: each long list scrolls inside its own
        // capped viewport so the page fits the compact default window
        // (the headless bounds pin measures the fit; the caps stepped
        // down with the approved variant-A frame, T-222). The
        // theme-error list's cap predates T-184 and stays.
        source.Should().Contain(
            "<ScrollViewer MaxHeight=\"160\">",
            "the theme-error list keeps its pre-T-184 viewport cap");
        source.Should().Contain(
            "<ScrollViewer MaxHeight=\"220\"",
            "the MCP client cards scroll inside their capped viewport");
        source.Should().Contain(
            "<ScrollViewer MaxHeight=\"180\"",
            "the import preview scrolls inside its capped viewport");
        source.Should().Contain(
            "<ScrollViewer MaxHeight=\"150\"",
            "the hotkeys rows scroll inside their capped viewport (T-184)");
        source.Should().Contain(
            "Height=\"236\"",
            "the pairs lists ride one shared row height sized with the rest (T-184)");
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
