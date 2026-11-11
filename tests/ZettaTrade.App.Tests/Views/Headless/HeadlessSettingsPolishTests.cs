namespace ZettaTrade.App.Tests.Views.Headless;

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using FluentAssertions;
using Livery.Localization;
using Livery.Themes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.AI.Mcp;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.App.Views;
using ZettaTrade.Charting;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Shell;
using AvaloniaPath = Avalonia.Controls.Shapes.Path;

/// <summary>
/// The headless settings-polish pins (T-169): the REAL settings window
/// materializes against a composed ViewModel and the acceptance facts are
/// MEASURED, not source-pinned — the indicator rows ride one shared column
/// set (the user-reported ragged alignment), the pairs tab's transfer
/// buttons keep their readable names and commands under the kit's icon
/// glyph, and every scroll surface's content clears the 16 px gutter the
/// overlay scrollbar paints over. Every window touch rides the world's
/// user-interface lane (an off-thread constructor dies on VerifyAccess);
/// the facts share the LocalizationManager singleton, so the class rides
/// the LocalizationManagerSingleton collection. Visibility reads ride the
/// effective-visibility walk: Avalonia keeps an invisible control in the
/// visual tree, so a descendant lookup alone proves nothing about what
/// the user sees.
/// </summary>
[Collection("LocalizationManagerSingleton")]
public sealed class HeadlessSettingsPolishTests
{
    static HeadlessSettingsPolishTests() => AvaloniaDispatcherPump.EnsureInstalled();

    /// <summary>
    /// The T-169 alignment fact: the catalog-reported rows re-lay on ONE
    /// shared column set — the Period captions and the period boxes sit on
    /// the same x per row and the boxes hold one width — every row grid
    /// carries the fixed 80/64/64 columns the re-lay exists for, and the
    /// visibility gates keep their honesty: the stub catalog reports
    /// exactly SMA and MACD, so exactly those two rows render (the
    /// headless main world's catalog is the honest empty one, so the
    /// loaded-row geometry needs its own composition).
    /// </summary>
    [Fact]
    public async Task TheLoadedIndicatorRows_ShareOneColumnSet_AcrossTheRows()
    {
        await HeadlessWorld.EnsureAppAsync();
        await HeadlessWorld.EnsureHealthyThemeSingletonAsync();
        var viewModel = ComposeSettingsViewModel(["SMA", "MACD"]);
        var window = await ShowSettingsWindowAsync(viewModel);
        try
        {
            await SelectTabAsync(window, "Settings.Tab.Indicators");
            await HeadlessWorld.OnUi(() =>
            {
                var rows = HeadlessWorld.Descendants<CheckBox>(window)
                    .Where(box => box.Content as string is "SMA" or "MACD")
                    .Where(IsEffectivelyVisible)
                    .ToList();
                rows.Should().HaveCount(2, "the stub catalog reports exactly SMA and MACD");

                // The visibility gates keep their honesty: a row the catalog
                // does not report stays hidden behind its binding.
                HeadlessWorld.Descendants<CheckBox>(window)
                    .Where(box => box.Content as string is "EMA" or "WMA" or "RSI" or "ATR" or "BOLL")
                    .Where(IsEffectivelyVisible)
                    .Should().BeEmpty("the unreported rows stay hidden");

                var rowGrids = rows.Select(box => box.Parent).Cast<Grid>().ToList();
                foreach (var grid in rowGrids)
                {
                    grid.ColumnDefinitions.Should().HaveCount(
                        4,
                        "the shared row shape is name, info, caption, box");
                    grid.ColumnDefinitions[0].Width.IsAbsolute.Should().BeTrue("the name column is fixed");
                    grid.ColumnDefinitions[0].Width.Value.Should().Be(80, "the name column holds the widest canonical name");
                    grid.ColumnDefinitions[2].Width.IsAbsolute.Should().BeTrue("the caption column is fixed");
                    grid.ColumnDefinitions[2].Width.Value.Should().Be(64, "the caption column holds one shared width");
                    grid.ColumnDefinitions[3].Width.IsAbsolute.Should().BeTrue("the box column is fixed");
                    grid.ColumnDefinitions[3].Width.Value.Should().Be(64, "the box column holds one shared width");
                }

                var captions = rowGrids.Select(
                    grid => grid.Children.OfType<TextBlock>().Single(child => Grid.GetColumn(child) == 2));
                var boxes = rowGrids.Select(grid => grid.Children.OfType<TextBox>().Single()).ToList();

                captions.Select(caption => WindowRect(window, caption).Left)
                    .Distinct().Should().HaveCount(1, "the Period captions sit on one shared x");
                boxes.Select(box => WindowRect(window, box).Left)
                    .Distinct().Should().HaveCount(1, "the period boxes sit on one shared x");
                boxes.Select(box => box.Bounds.Width)
                    .Distinct().Should().HaveCount(1, "the period boxes hold one shared width");

                // The section caption rides its own header line above the
                // rows — not the tab header's own text block.
                var section = HeadlessWorld.Descendants<TextBlock>(window)
                    .Where(text => text.Text == LocalizationManager.Instance.GetString("Chart.Indicators"))
                    .Where(text => text.GetVisualAncestors().All(ancestor => ancestor is not TabItem))
                    .Single();
                IsEffectivelyVisible(section).Should().BeTrue("the caption reads above the rows whatever the catalog holds");
            });
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    /// <summary>
    /// The honest zero (T-169): the re-lay never hides the empty catalog —
    /// the headless main world composes the empty indicator catalog, so the
    /// tab must render no indicator row and speak through its empty state
    /// instead (the same conditional shape the loaded-row geometry guards).
    /// </summary>
    [Fact]
    public async Task TheEmptyCatalog_RendersTheHonestEmptyState_WithoutAnyRow()
    {
        var world = await MainWorld.CreateAsync();
        var window = await ShowSettingsWindowAsync(world.Main.Settings);
        try
        {
            await SelectTabAsync(window, "Settings.Tab.Indicators");
            await HeadlessWorld.OnUi(() =>
            {
                HeadlessWorld.Descendants<CheckBox>(window)
                    .Where(box => box.Content as string is "SMA" or "EMA" or "WMA" or "RSI" or "MACD" or "ATR" or "BOLL")
                    .Where(IsEffectivelyVisible)
                    .Should().BeEmpty("the empty catalog's tab renders zero indicator rows");

                var empty = HeadlessWorld.Descendants<TextBlock>(window)
                    .Single(text => text.Text == LocalizationManager.Instance.GetString("Settings.Indicators.Empty"));
                IsEffectivelyVisible(empty).Should().BeTrue("the honest empty state speaks for the zero");
            });
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    /// <summary>
    /// The pairs tab's polish fact: the transfer pair rides the chrome kit's
    /// icon glyph without losing a single readable affordance — the names
    /// and the commands survive the glyph swap, the chevron Path carries its
    /// own geometry (never a plain-text content) — and both pair lists plus
    /// the tab body's scroll content clear the 16 px gutter the overlay
    /// scrollbars paint over (the lists scroll internally, so the padding
    /// is the lists' own gutter).
    /// </summary>
    [Fact]
    public async Task ThePairsTab_KeepsTheTransferCommands_AndClearsTheGutter()
    {
        var world = await MainWorld.CreateAsync();
        var window = await ShowSettingsWindowAsync(world.Main.Settings);
        try
        {
            await SelectTabAsync(window, "Settings.Tab.Workspace");
            await HeadlessWorld.OnUi(() =>
            {
                var viewModel = world.Main.Settings;

                var transfer = HeadlessWorld.Descendants<Button>(window)
                    .Where(button => button.Classes.Contains("icon"))
                    .Where(IsEffectivelyVisible)
                    .ToList();
                transfer.Should().HaveCount(2, "the pairs tab carries exactly the two transfer buttons");

                var ordered = transfer.OrderBy(button => WindowRect(window, button).Top).ToList();
                var hide = ordered[0];
                var reveal = ordered[1];
                AutomationProperties.GetName(hide)
                    .Should().Be(LocalizationManager.Instance.GetString("Settings.Pairs.Hide"), "the glyph alone never carries meaning");
                hide.Command.Should().BeSameAs(viewModel.HidePairCommand, "the hide command rides through the glyph");
                AutomationProperties.GetName(reveal)
                    .Should().Be(LocalizationManager.Instance.GetString("Settings.Pairs.Reveal"), "the glyph alone never carries meaning");
                reveal.Command.Should().BeSameAs(viewModel.RevealPairCommand, "the reveal command rides through the glyph");
                foreach (var button in ordered)
                {
                    var chevron = button.Content as AvaloniaPath;
                    chevron.Should().NotBeNull("the affordance is the explicit-stroke chevron, never a plain-text content");
                    chevron!.Data.Should().NotBeNull("the chevron carries its own geometry");
                }

                // The pair lists scroll internally: their padding is their own gutter.
                var lists = HeadlessWorld.Descendants<ListBox>(window).ToList();
                lists.Should().HaveCount(2, "the pairs tab's two lists are the window's only list boxes");
                lists.Single(list => list.ItemsSource == viewModel.AllPairs)
                    .Padding.Right.Should().Be(16, "the all-pairs rows must not sit under the list's own overlay bar");
                lists.Single(list => list.ItemsSource == viewModel.HiddenPairs)
                    .Padding.Right.Should().Be(16, "the hidden-pairs rows must not sit under the list's own overlay bar");

                // The tab body scrolls as one for small machines: its content
                // ends a strip short of the viewport's right edge.
                var gutted = ScrollContents(window);
                gutted.Should().NotBeEmpty("the tab body carries a scrolling viewport");
                foreach (var content in gutted)
                {
                    content.Margin.Right.Should().Be(16, "the scroll content clears the overlay bar's strip");
                }
            });
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    /// <summary>
    /// The merged integrations tab's gutter fact: at minimum the tab body
    /// and the MCP client cards' viewport carry the same 16 px gutter —
    /// every scroll surface the tab materializes is pinned (the import
    /// preview and the theme-error list join the shape wherever they show).
    /// </summary>
    [Fact]
    public async Task TheIntegrationsTab_ScrollContents_ClearTheGutter()
    {
        var world = await MainWorld.CreateAsync();
        var window = await ShowSettingsWindowAsync(world.Main.Settings);
        try
        {
            await SelectTabAsync(window, "Settings.Tab.Integrations");
            await HeadlessWorld.OnUi(() =>
            {
                var gutted = ScrollContents(window);
                gutted.Count.Should().BeGreaterThanOrEqualTo(2, "the tab body plus the MCP client cards' viewport at minimum");
                foreach (var content in gutted)
                {
                    content.Margin.Right.Should().Be(16, "the scroll content clears the overlay bar's strip");
                }
            });
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    /// <summary>
    /// Compose a settings ViewModel over the REAL localization singleton
    /// and a stub catalog reporting exactly the given loaded set — the
    /// ViewModel-kits' recipe the indicator-pack facts ride, with the
    /// localization the window materialization shares.
    /// </summary>
    private static SettingsViewModel ComposeSettingsViewModel(IReadOnlyList<string> loadedIndicators)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<bool>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(false);
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);

        var themeManager = Substitute.For<IThemeManager>();
        themeManager.CurrentTheme.Returns("dark");
        themeManager.AvailableThemes.Returns([new("dark", "Dark", true, [])]);

        var adapter = Substitute.For<IMarketDataAdapter>();
        adapter.Id.Returns("demo");
        adapter.Name.Returns("Demo");
        var activeExchanges = new ActiveExchangeService(
            [adapter],
            Substitute.For<ISettingsService>(),
            NullLogger<ActiveExchangeService>.Instance);

        return new SettingsViewModel(
            settings,
            themeManager,
            LocalizationManager.Instance,
            Substitute.For<IShellUtils>(),
            new McpHostService(
                settings,
                Substitute.For<IMcpServerHost>(),
                NullLogger<McpHostService>.Instance),
            activeExchanges,
            new SymbolListProvider(activeExchanges, NullLogger<SymbolListProvider>.Instance),
            CatalogOf(loadedIndicators),
            Substitute.For<IMcpClientManager>(),
            NullLogger<SettingsViewModel>.Instance);
    }

    /// <summary>The catalog stub reporting exactly the given loaded set.</summary>
    private static IIndicatorCatalog CatalogOf(IReadOnlyList<string> loadedIndicators)
    {
        var catalog = Substitute.For<IIndicatorCatalog>();
        catalog.LoadedNames.Returns(loadedIndicators);
        return catalog;
    }

    /// <summary>
    /// Select one folded tab by its localization key and land the switch on
    /// the serialized dispatcher — the selected tab's content materializes
    /// only in its lane, so the drain is what makes the facts measurable.
    /// </summary>
    private static async Task SelectTabAsync(SettingsWindow window, string headerKey)
    {
        await HeadlessWorld.OnUi(() =>
        {
            var expected = LocalizationManager.Instance.GetString(headerKey);
            HeadlessWorld.Descendants<TabItem>(window)
                .Single(tab => tab.Header as string == expected)
                .IsSelected = true;
        });
    }

    /// <summary>Materialize the settings window at its shipped default size and land its open.</summary>
    private static async Task<SettingsWindow> ShowSettingsWindowAsync(SettingsViewModel settings)
    {
        var window = await HeadlessWorld.OnUi(() => new SettingsWindow { DataContext = settings });
        await HeadlessWorld.ShowAndSettleAsync(window);
        return window;
    }

    /// <summary>
    /// Every scroll surface whose CONTENT is a top-level panel or list: the
    /// template internals (the text boxes' and list boxes' own viewport
    /// parts) carry other content shapes and are excluded by construction.
    /// </summary>
    private static IReadOnlyList<Control> ScrollContents(Window window) =>
        HeadlessWorld.Descendants<ScrollViewer>(window)
            .Select(viewer => viewer.Content as Control)
            .Where(content => content is StackPanel or ItemsControl)
            .Select(content => content!)
            .ToList();

    /// <summary>
    /// The control's effective visibility: Avalonia keeps an invisible
    /// control in the visual tree, so the walk must carry the whole
    /// ancestor chain's own IsVisible flags.
    /// </summary>
    private static bool IsEffectivelyVisible(Visual visual)
    {
        for (var node = (Visual?)visual; node is not null; node = node.GetVisualParent())
        {
            if (!node.IsVisible)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The control's bounds in window coordinates.</summary>
    private static Rect WindowRect(Window window, Visual element)
    {
        var topLeft = element.TranslatePoint(new Point(0, 0), window);
        topLeft.Should().NotBeNull("the element lays out inside the window");
        return new Rect(topLeft!.Value, ((Control)element).Bounds.Size);
    }
}
