namespace ZettaTrade.App.Tests.Views;

using System;
using System.IO;
using FluentAssertions;
using Xunit;

/// <summary>
/// The watchlist color-semantics pins (T-159): the change text tints by the
/// 24h sign and the sparkline colors by the ring's direction — both riding
/// the candle state colors (CandleUp/CandleDown, the inks the chart and the
/// book speak), with the plain text ink and the Highlight line left as the
/// no-signal states; the rows' hover/selection states read the palette's
/// Surface, off the stock system brushes Fluent paints the ListBoxItem with.
/// The color-leg facts over the shipped theme files live in UI.Tests' palette
/// battery; the heatmap tiles stay untouched here (the scope's conditional —
/// the shared color-scale helper did not land in this task, so the widget
/// keeps its own direction tiles, recorded in the task's canon).
/// </summary>
public sealed class WatchlistColorPinsTests
{
    private static readonly string RepoRoot = FindRepositoryRoot();

    /// <summary>The main window source, normalized to LF.</summary>
    private static string MainWindowXaml =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "MainWindow.axaml"))
            .Replace("\r\n", "\n");

    /// <summary>The row view-model source (the computed flag facts).</summary>
    private static string RowViewModelSource =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "ViewModels", "WatchlistRowViewModel.cs"));

    [Fact]
    public void TheChangeText_TintsByTheSign_OverTheCandleInks()
    {
        var source = MainWindowXaml;

        // The tint classes ride the candle state colors — the same inks the
        // chart speaks, not ad-hoc hexes, and never the unscored Accent.
        source.Should().Contain("<Style Selector=\"TextBlock.watch-change.up\">");
        StyleBlock(source, "TextBlock.watch-change.up")
            .Should().Contain("Property=\"Foreground\" Value=\"{DynamicResource CandleUp}\"");
        source.Should().Contain("<Style Selector=\"TextBlock.watch-change.down\">");
        StyleBlock(source, "TextBlock.watch-change.down")
            .Should().Contain("Property=\"Foreground\" Value=\"{DynamicResource CandleDown}\"");

        // The template binds both flags; an unpriced or flat row binds
        // neither and keeps the plain ink.
        source.Should().Contain("Classes=\"watch-change\"");
        source.Should().Contain("Classes.up=\"{Binding IsChangeUp}\"");
        source.Should().Contain("Classes.down=\"{Binding IsChangeDown}\"");
    }

    [Fact]
    public void TheSparkline_ColorsByTheRingDirection()
    {
        var source = MainWindowXaml;

        // The stroke's three states: the Highlight pointer line stays the
        // no-signal base, the direction classes override with the candle
        // inks. The stroke must NOT be a local attribute on the Polyline —
        // a local value beats every style setter and would pin the line
        // to Highlight forever.
        source.Should().Contain("<Style Selector=\"Polyline.watch-spark\">");
        StyleBlock(source, "Polyline.watch-spark")
            .Should().Contain("Property=\"Stroke\" Value=\"{DynamicResource Highlight}\"");
        source.Should().Contain("<Style Selector=\"Polyline.watch-spark.up\">");
        StyleBlock(source, "Polyline.watch-spark.up")
            .Should().Contain("Property=\"Stroke\" Value=\"{DynamicResource CandleUp}\"");
        source.Should().Contain("<Style Selector=\"Polyline.watch-spark.down\">");
        StyleBlock(source, "Polyline.watch-spark.down")
            .Should().Contain("Property=\"Stroke\" Value=\"{DynamicResource CandleDown}\"");

        source.Should().Contain("Classes=\"watch-spark\"");
        source.Should().Contain("Classes.up=\"{Binding IsSparklineUp}\"");
        source.Should().Contain("Classes.down=\"{Binding IsSparklineDown}\"");

        // The sparkline Polyline carries no local Stroke: the styles own it.
        source.Should().NotContain(
            "Stroke=\"{DynamicResource Highlight}\"",
            "the sparkline's no-signal ink rides the base style — a local attribute would beat every direction override");
    }

    [Fact]
    public void TheRows_VisualStates_ReadThePaletteOffTheStockBrushes()
    {
        var source = MainWindowXaml;

        // The hover affordance reads Surface; Fluent paints the ListBoxItem
        // hover straight onto the template part with a system brush, so the
        // override targets that same part.
        source.Should().Contain(
            "<Style Selector=\"ListBox.watch-rows ListBoxItem:pointerover /template/ ContentPresenter#PART_ContentPresenter\">");
        StyleBlock(source, "ListBox.watch-rows ListBoxItem:pointerover /template/ ContentPresenter#PART_ContentPresenter")
            .Should().Contain("Property=\"Background\" Value=\"{DynamicResource Surface}\"");

        // The rows' click does nothing today (the selection is inert), so
        // the selected state stays quiet and the hover carries the one
        // affordance — never the system accent fill.
        source.Should().Contain(
            "<Style Selector=\"ListBox.watch-rows ListBoxItem:selected /template/ ContentPresenter#PART_ContentPresenter\">");
        StyleBlock(source, "ListBox.watch-rows ListBoxItem:selected /template/ ContentPresenter#PART_ContentPresenter")
            .Should().Contain("Property=\"Background\" Value=\"Transparent\"");
        source.Should().Contain(
            "<Style Selector=\"ListBox.watch-rows ListBoxItem:selected:pointerover /template/ ContentPresenter#PART_ContentPresenter\">");

        // The stock-brush family stays swept out of the window (the T-150
        // discipline the rows' new states keep).
        source.Should().NotContain(
            "SystemControl",
            "the rows' visual states ride the palette keys, never the stock system brushes");
        source.Should().NotContain(
            "SystemAccent",
            "the rows' visual states ride the palette keys, never the stock system brushes");

        // The styles live in the window's own styles host (the panels never
        // carry Border.Styles blocks — the canon's pin constraint).
        source.Should().Contain("<Window.Styles>");
    }

    [Fact]
    public void TheFlags_AreComputedOnTheRow()
    {
        var source = RowViewModelSource;

        // The row computes the tint facts (the markup binds them); the sign
        // flags read the 24h change, the direction flags read the ring.
        foreach (var fact in new[] { "IsChangeUp", "IsChangeDown", "IsSparklineUp", "IsSparklineDown" })
        {
            source.Should().Contain(
                $"public bool {fact}",
                "the {0} tint fact is computed on the row view-model", fact);
        }
    }

    /// <summary>Extract one style block (its selector to its close).</summary>
    private static string StyleBlock(string source, string selector)
    {
        var opening = $"<Style Selector=\"{selector}\">";
        var start = source.IndexOf(opening, StringComparison.Ordinal);
        start.Should().BePositive($"the {selector} style must exist in the window");
        var end = source.IndexOf("</Style>", start, StringComparison.Ordinal);
        return source[start..end];
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
