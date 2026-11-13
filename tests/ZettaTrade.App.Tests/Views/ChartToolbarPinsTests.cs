namespace ZettaTrade.App.Tests.Views;

using System;
using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

/// <summary>
/// The chart toolbar icon pass + panel header adoption pins (T-160): the
/// band's tool toggles speak the kit's icon shape (borderless glyph
/// buttons whose captions became the automation names — the icon-only
/// controls' names never ride their tooltips), the three export actions
/// gather under one named flyout, and the panels' heads ride the Livery
/// scaffold (the one padding grammar and the title treatment the kit
/// publishes) instead of hand-typed per-panel copies. The repo has no
/// headless render harness yet (the T-144 lane is queued), so the wrap
/// fact ("the band folds one more narrow step") rides the source of truth
/// here and the live bounds pin waits for that lane — recorded, not
/// silently absorbed.
/// </summary>
public sealed class ChartToolbarPinsTests
{
    private static readonly string RepoRoot = FindRepositoryRoot();

    /// <summary>The chart panel source, normalized to LF.</summary>
    private static string ChartPanelXaml =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "Panels", "ChartPanelView.axaml"))
            .Replace("\r\n", "\n");

    /// <summary>The main window source, normalized to LF.</summary>
    private static string MainWindowXaml =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "MainWindow.axaml"))
            .Replace("\r\n", "\n");

    /// <summary>The order book panel source, normalized to LF.</summary>
    private static string BookPanelXaml =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "Panels", "OrderBookPanelView.axaml"))
            .Replace("\r\n", "\n");

    [Fact]
    public void TheToolToggles_SpeakIcons_RidingTheKitClass()
    {
        var source = ChartPanelXaml;

        // The band carries exactly nine icon buttons: the six drawing
        // tools (wave 1's three plus T-200's zone, text and measure), the
        // export flyout, the T-178 delete affordance and T-202's templates
        // flyout (the overlay toggles and the compare action stay text —
        // they are outside the icon pass's named scope).
        Regex.Matches(source, "Classes=\"icon\"")
            .Should().HaveCount(9, "the six tools, the export flyout, the delete affordance and the templates flyout are the band's icon family");

        // Each tool rides the kit's armed class through the VM's own fact,
        // and its caption became the automation name — an icon-only
        // control's name never rides its tooltip.
        foreach (var (tool, active) in new[]
                 {
                     ("Chart.Tool.TrendLine", "IsTrendLineToolActive"),
                     ("Chart.Tool.Level", "IsLevelToolActive"),
                     ("Chart.Tool.Fib", "IsFibToolActive"),
                     ("Chart.Tool.Zone", "IsZoneToolActive"),
                     ("Chart.Tool.Text", "IsTextToolActive"),
                     ("Chart.Tool.Measure", "IsMeasureToolActive"),
                 })
        {
            var block = IconBlock(source, tool);
            block.Should().Contain(
                $"Classes.on=\"{{Binding {active}}}\"",
                "the {0} toggle's armed state rides the kit's .on class", tool);
            block.Should().Contain(
                $"AutomationProperties.Name=\"{{loc:Localized {tool}}}\"",
                "the {0} icon carries its caption as the automation name", tool);
            block.Should().Contain(
                "Stroke=\"{DynamicResource TextPrimary}\"",
                "the {0} glyph draws like the strip's: an explicit stroke, no inheritance chain", tool);
            block.Should().Contain(
                "StrokeThickness=\"1.5\"",
                "the {0} glyph keeps the strip's stroke weight", tool);
        }

        // The text copies are gone: the tools' captions print nowhere —
        // the icons + tooltips + names carry them now.
        foreach (var tool in new[]
                 {
                     "Chart.Tool.TrendLine", "Chart.Tool.Level", "Chart.Tool.Fib",
                     "Chart.Tool.Zone", "Chart.Tool.Text", "Chart.Tool.Measure",
                 })
        {
            source.Should().NotContain(
                $"Content=\"{{loc:Localized {tool}}}\"",
                "the {0} caption is the automation name now, never a printed label", tool);
        }
    }

    [Fact]
    public void TheExports_GatherUnderOneFlyout()
    {
        var source = ChartPanelXaml;

        // Two flyouts gather the band's multi-action affordances: the
        // three export actions (T-160) and the chart templates (T-202).
        // Each item keeps its caption as the header (a menu item's header
        // text is its automation name) and its own command. Avalonia 12
        // hosts the flyout's actions as MenuItem elements — the docs'
        // MenuFlyout example rides MenuItems.
        Regex.Matches(source, "<MenuFlyout>")
            .Should().HaveCount(2, "the exports and the templates each gather under ONE flyout");
        Regex.Matches(source, "<MenuItem ")
            .Should().HaveCount(7, "the three export items plus the templates' save item and the row scaffolding ride as MenuItems");

        foreach (var (key, command) in new[]
                 {
                     ("Chart.ExportCandlesCsv", "ExportCandlesCommand"),
                     ("Chart.ExportPng", "ExportPngCommand"),
                     ("Chart.ExportVisibleCsv", "ExportVisibleCandlesCommand"),
                 })
        {
            source.Should().Contain(
                $"Header=\"{{loc:Localized {key}}}\"",
                "the {0} action keeps its caption as the item's header", key);
            source.Should().Contain(
                $"Command=\"{{Binding {command}}}\"",
                "the {0} action keeps its command — a flyout item that does nothing is a lie", key);
        }

        // The flyout button: the named icon control (the screen reader's
        // entry point into the gathered actions).
        var exportBlock = IconBlock(source, "Chart.Export");
        exportBlock.Should().Contain(
            "AutomationProperties.Name=\"{loc:Localized Chart.Export}\"",
            "the flyout's icon button is named — the acceptance's automation-name pin");
        exportBlock.Should().Contain(
            "<Button.Flyout>",
            "the flyout rides the button itself");

        // The three text copies are gone: the band's export row is one icon.
        foreach (var key in new[] { "Chart.ExportCandlesCsv", "Chart.ExportPng", "Chart.ExportVisibleCsv" })
        {
            source.Should().NotContain(
                $"Content=\"{{loc:Localized {key}}}\"",
                "the {0} text button folded into the flyout", key);
        }
    }

    [Fact]
    public void ThePanelHeads_RideTheSharedScaffold()
    {
        // The tape head: the scaffold owns the grammar — the head is the
        // scaffold DockPanel with the right-docked Clear action and the
        // bare title TextBlock (the treatment lives in the kit).
        MainWindowXaml.Should().Contain(
            "<DockPanel Grid.Row=\"0\" Classes=\"panel-header\">",
            "the tape head rides the kit scaffold");
        MainWindowXaml.Should().Contain(
            "<TextBlock Text=\"{loc:Localized Tape.Title}\" />",
            "the tape title carries no hand-typed treatment — the scaffold's descendant rule owns it");
        MainWindowXaml.Should().NotContain(
            "Margin=\"8,0,4,4\"",
            "the tape head's hand margin is gone — the scaffold's padding grammar owns the inset");
        Regex.Matches(MainWindowXaml, "Classes=\"panel-header\"")
            .Should().HaveCount(1, "the tape head is the window's only scaffold host (the tabs ride the title class)");

        // The tab captions ride the title treatment alone: the tab chrome
        // keeps its own geometry.
        MainWindowXaml.Should().Contain(
            "<TextBlock Classes=\"panel-title\"\n                                               Text=\"{loc:Localized Watch.Title}\" />",
            "the watch tab caption rides the shared title treatment");
        MainWindowXaml.Should().Contain(
            "<TextBlock Classes=\"panel-title\"\n                                               Text=\"{loc:Localized Heatmap.Title}\" />",
            "the heat tab caption rides the shared title treatment");
        MainWindowXaml.Should().NotContain(
            "Header=\"{loc:Localized Watch.Title}\"",
            "the watch tab's caption is the scaffold's title class now");
        MainWindowXaml.Should().NotContain(
            "Header=\"{loc:Localized Heatmap.Title}\"",
            "the heat tab's caption is the scaffold's title class now");

        // The book panel: both captions ride the scaffold.
        Regex.Matches(BookPanelXaml, "Classes=\"panel-header\"")
            .Should().HaveCount(2, "the book's two captions (title and depth) adopt the scaffold");
        BookPanelXaml.Should().Contain(
            "<TextBlock Text=\"{loc:Localized OrderBook.Title}\" />",
            "the book title carries no hand-typed treatment");
        BookPanelXaml.Should().NotContain(
            "Margin=\"8,0,0,4\"",
            "the book captions' hand margins are gone");

        // The signal rail head: the scaffold over the host Border's own
        // planar padding (which stays the rows' inset).
        Regex.Matches(ChartPanelXaml, "Classes=\"panel-header\"")
            .Should().HaveCount(1, "the rail head adopts the scaffold");
        ChartPanelXaml.Should().NotContain(
            "Margin=\"4,2,0,6\"",
            "the rail head's hand margin is gone");
        ChartPanelXaml.Should().Contain(
            "<TextBlock Text=\"{loc:Localized Signal.Rail.Title}\" />",
            "the rail title carries no hand-typed treatment");
    }

    [Fact]
    public void EveryIconControl_CarriesItsName_AndTheItemsCarryTheirHeaders()
    {
        var source = ChartPanelXaml;

        // The five icon controls: three tools, the export flyout, the
        // T-202 templates flyout and the T-178 delete affordance, each
        // with the localized automation name.
        foreach (var key in new[]
                 {
                     "Chart.Tool.TrendLine", "Chart.Tool.Level", "Chart.Tool.Fib", "Chart.Export",
                     "Chart.Templates", "Chart.Drawing.Delete",
                 })
        {
            Regex.Matches(source, $"AutomationProperties.Name=\"{{loc:Localized {key}}}\"")
                .Should().HaveCount(
                    1,
                    "the {0} icon control is named exactly once — the a11y lane's per-control pin",
                    key);
        }

        // The flyout's items carry their header captions (a menu item's
        // header text is what assistive tech reads): the three export
        // items and the templates' static save item.
        Regex.Matches(source, "Header=\"{loc:Localized Chart\\.Export")
            .Should().HaveCount(3, "the three items name themselves through their headers");
        Regex.Matches(source, "Header=\"{loc:Localized Chart\\.Templates\\.Save\\}\"")
            .Should().HaveCount(1, "the templates' static save item names itself through its header");
    }

    [Fact]
    public void TheTemplatesFlyout_IsPrimaryOnly_AndBindsTheRowsLazily()
    {
        var source = ChartPanelXaml;

        // The templates affordance (T-202): an icon-only flyout button
        // that shows on the primary's surface alone — a grid slot keeps
        // its face picks in-session and never sees the shared template
        // state (the T-201 canon). The bookmark glyph draws like the
        // band's other glyphs: an explicit stroke, no inheritance chain.
        var block = IconBlock(source, "Chart.Templates");
        block.Should().Contain(
            "IsVisible=\"{Binding HasTemplates}\"",
            "the affordance hides on the slots — the templates stay primary-only");
        block.Should().Contain(
            "<Button.Flyout>",
            "the flyout rides the button itself");
        block.Should().Contain(
            "Data=\"M 3 1 H 9 V 11 L 6 8.5 L 3 11 Z\"",
            "the bookmark glyph is the templates' mark");
        block.Should().Contain(
            "Stroke=\"{DynamicResource TextPrimary}\"",
            "the glyph draws like the strip's: an explicit stroke, no inheritance chain");
        block.Should().Contain(
            "StrokeThickness=\"1.5\"",
            "the glyph keeps the strip's stroke weight");

        // The static save item and the dynamic rows: the save item rides
        // the manager's command, the rows bind the manager's collection —
        // the nested DataTemplates carry the typed roots (the compiled
        // bindings' discipline).
        block.Should().Contain(
            "Command=\"{Binding Templates.SaveCommand}\"",
            "the static save item rides the manager's save command");
        block.Should().Contain(
            "ItemsSource=\"{Binding Templates.Rows}\"",
            "the rows bind the manager's collection (materialized on the flyout's open, the startup-noise pin stays safe)");
        block.Should().Contain(
            "x:DataType=\"vm:ChartTemplateRow\"",
            "the row template carries its typed root");
        block.Should().Contain(
            "x:DataType=\"vm:ChartTemplateAction\"",
            "the action template carries its typed root");
        block.Should().Contain(
            "Header=\"{Binding Header}\"",
            "the per-row action binds its header");
        block.Should().Contain(
            "Command=\"{Binding Command}\"",
            "the per-row action binds its command");
    }

    /// <summary>
    /// Extract one icon button's element block: from its
    /// <c>AutomationProperties.Name</c> line back to the opening
    /// "Button" tag and forward to the element close — the block the
    /// per-control facts read.
    /// </summary>
    private static string IconBlock(string source, string nameKey)
    {
        var nameMarker = $"AutomationProperties.Name=\"{{loc:Localized {nameKey}}}\"";
        var nameIndex = source.IndexOf(nameMarker, StringComparison.Ordinal);
        nameIndex.Should().BePositive($"the {nameKey} control must be named in the panel");

        var openIndex = source.LastIndexOf("<Button", nameIndex, StringComparison.Ordinal);
        openIndex.Should().BePositive($"the {nameKey} control must open before its name");
        var closeIndex = source.IndexOf("</Button>", nameIndex, StringComparison.Ordinal);
        closeIndex.Should().BeGreaterThan(nameIndex, $"the {nameKey} control must close after its name");

        return source[openIndex..closeIndex];
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
