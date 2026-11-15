namespace ZettaTrade.App.Tests.Views.Headless;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
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

/// <summary>
/// The T-222 rail render pins: the approved variant-A voice MEASURED on
/// the real settings window and the applied templates instead of pinned
/// to the source. The T-184 rail styles target the Fluent TabItem
/// template's named parts and a placement attribute selector — a silent
/// miss renders nothing and nothing fails — so these facts measure what
/// actually applies: the named parts exist in both placements, the
/// selected item wears the derived ButtonHoverTint exactly and shows the
/// 3 px Accent tick on the item's own left edge while a resting item
/// stays transparent and tickless, every rail header carries its 16 px
/// icon beside the caption (the applied template renders no Icon
/// presenter, so the icons ride the header compositions), and the
/// window's chrome rides the approved strips — the 190 px title-bar
/// backing with its right hairline and the rail note, and the full-width
/// pinned footer over the title-bar fill. The derived keys ride the
/// theme publish, so every real-window fact applies the shipped dark
/// palette first (the publish tests' recipe) — the bare headless world
/// never runs the theme apply the real app does.
/// </summary>
[Collection("LocalizationManagerSingleton")]
public sealed class HeadlessSettingsRailTests
{
    static HeadlessSettingsRailTests() => AvaloniaDispatcherPump.EnsureInstalled();

    /// <summary>The shipped dark palette (themes/dark.json) the mockup's colors ride.</summary>
    private static readonly IReadOnlyDictionary<string, string> DarkPalette =
        new Dictionary<string, string>
        {
            ["Background"] = "#0a0a0f",
            ["Surface"] = "#1a1a2e",
            ["Border"] = "#16213e",
            ["Accent"] = "#2563eb",
            ["TitleBar"] = "#11111c",
            ["TitleBarText"] = "#e0e0e8",
            ["TextPrimary"] = "#ffffff",
            ["TextSecondary"] = "#a0a0a0",
            ["Buy"] = "#22c55e",
            ["Sell"] = "#ef4444",
            ["CandleUp"] = "#22c55e",
            ["CandleDown"] = "#ef4444",
            ["DrawingLine"] = "#38bdf8",
            ["DrawingSelected"] = "#facc15",
            ["Highlight"] = "#ffd700",
            ["Error"] = "#ef4444",
        };

    /// <summary>The rail's five categories in strip order.</summary>
    private static readonly string[] RailKeys =
    {
        "Settings.Tab.Appearance",
        "Settings.Tab.Indicators",
        "Settings.Tab.Integrations",
        "Settings.Tab.Workspace",
        "Settings.Tab.Notifications",
    };

    /// <summary>
    /// The applied Fluent TabItem template (both placements) carries the
    /// named parts the rail styles target — the styles' whole vocabulary
    /// hangs on <c>/template/ Border#PART_…</c> selectors matching.
    /// </summary>
    [Fact]
    public async Task TheAppliedFluentTabItemTemplate_CarriesTheNamedParts()
    {
        await HeadlessWorld.EnsureAppAsync();
        var window = await HeadlessWorld.OnUi(() => new Window
        {
            Width = 700,
            Height = 460,
            Content = new StackPanel
            {
                Children =
                {
                    new TabControl
                    {
                        TabStripPlacement = Dock.Left,
                        Items = { new TabItem { Header = "One" }, new TabItem { Header = "Two" } },
                    },
                    new TabControl
                    {
                        TabStripPlacement = Dock.Top,
                        Items = { new TabItem { Header = "One" }, new TabItem { Header = "Two" } },
                    },
                },
            },
        });
        try
        {
            await HeadlessWorld.ShowAndSettleAsync(window);
            await HeadlessWorld.OnUi(() =>
            {
                var tabs = HeadlessWorld.Descendants<TabItem>(window).ToList();
                tabs.Should().HaveCount(4, "the two probes' strips materialize four items");

                foreach (var group in tabs.GroupBy(tab => tab.TabStripPlacement))
                {
                    foreach (var tab in group)
                    {
                        var found = HeadlessWorld.Descendants<Control>(tab)
                            .Select(part => part.GetType().Name
                                + (part.Name is { Length: > 0 } name ? "#" + name : string.Empty))
                            .ToList();
                        found.Should().Contain(
                            name => name.StartsWith("Border#PART_LayoutRoot", StringComparison.Ordinal),
                            $"the {group.Key} TabItem template carries the layout-root part the rail styles target; found: {string.Join(", ", found)}");
                        found.Should().Contain(
                            name => name.StartsWith("Border#PART_SelectedPipe", StringComparison.Ordinal),
                            $"the {group.Key} TabItem template carries the selected-pipe part the tick style targets; found: {string.Join(", ", found)}");
                    }
                }
            });
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    /// <summary>
    /// The REAL settings window renders the approved rail voice: the
    /// placement sits on the items (the attribute selector's premise), the
    /// selected item holds the soft tint on the layout root and shows the
    /// 3 px Accent tick ON THE ITEM'S LEFT EDGE (the approved mark opens
    /// the rail item), and a resting item stays transparent and tickless.
    /// </summary>
    [Fact]
    public async Task TheSelectedRailItem_RendersTheTintAndTheEdgeTick()
    {
        var manager = await RegisterDarkTheme();
        var window = await ShowSettingsWindowAsync(ComposeSettingsViewModel(["SMA"]));
        try
        {
            // The apply is the publish: run it on the resource world's own
            // thread before the window materializes, so the styles' first
            // DynamicResource pass resolves the derived tints like the real
            // app's first launch does.
            await Run(() => manager.ApplyThemeAsync("dark").GetAwaiter().GetResult());
            await HeadlessWorld.OnUi(() =>
            {
                var tabs = HeadlessWorld.Descendants<TabItem>(window).ToList();
                tabs.Should().HaveCount(5, "the rail's five categories materialize");
                tabs.Select(tab => tab.TabStripPlacement)
                    .Should().OnlyContain(
                        placement => placement == Dock.Left,
                        "the placement attribute selector matches only when the items carry the placement");

                var tint = Application.Current?.Resources["ButtonHoverTint"] as ISolidColorBrush;
                tint.Should().NotBeNull("the applied theme publishes the derived hover tint the styles ride");

                var selected = tabs.Single(tab => tab.IsSelected);
                var resting = tabs.First(tab => !tab.IsSelected);

                var selectedRoot = HeadlessWorld.FindDescendant<Border>(selected, "PART_LayoutRoot");
                selectedRoot.Should().NotBeNull("the selected item's tint rides the template's layout-root part");
                selectedRoot!.Background.Should().NotBeNull("the selected item holds the soft tint");
                selectedRoot.Background.Should().NotBe(
                    Brushes.Transparent,
                    "the selection tint is not the resting transparency");
                (selectedRoot.Background as ISolidColorBrush)!.Color.Should().Be(
                    tint!.Color,
                    "the selected item wears the derived hover tint exactly");

                var restingRoot = HeadlessWorld.FindDescendant<Border>(resting, "PART_LayoutRoot");
                restingRoot.Should().NotBeNull("the resting item's transparency rides the same part");
                (restingRoot!.Background is null || Equals(restingRoot.Background, Brushes.Transparent))
                    .Should().BeTrue("the resting rail item stays transparent over the strip's backing");

                var selectedPipe = HeadlessWorld.FindDescendant<Border>(selected, "PART_SelectedPipe");
                selectedPipe.Should().NotBeNull("the selected item's tick rides the template's pipe part");
                selectedPipe!.IsVisible.Should().BeTrue("the selected item shows the tick");
                selectedPipe.Bounds.Width.Should().Be(3, "the tick is the approved 3 px pipe");
                (WindowRect(window, selectedPipe).Left - WindowRect(window, selected).Left)
                    .Should().BeLessThanOrEqualTo(
                        2,
                        "the tick rides the item's left edge — the approved mark opens the rail item");

                var restingPipe = HeadlessWorld.FindDescendant<Border>(resting, "PART_SelectedPipe");
                (restingPipe is null || !restingPipe.IsVisible)
                    .Should().BeTrue("the tick shows on the selected item only");
            });
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    /// <summary>
    /// Every rail header carries the 16 px icon beside its caption — the
    /// applied template renders no Icon presenter (measured), so the icons
    /// ride the header compositions while the caption TextBlocks keep the
    /// tab keys for the localization facts.
    /// </summary>
    [Fact]
    public async Task TheRailHeaders_CarryTheIconsBesideTheCaptions()
    {
        var manager = await RegisterDarkTheme();
        var window = await ShowSettingsWindowAsync(ComposeSettingsViewModel(["SMA"]));
        try
        {
            await Run(() => manager.ApplyThemeAsync("dark").GetAwaiter().GetResult());
            await HeadlessWorld.OnUi(() =>
            {
                var tabs = HeadlessWorld.Descendants<TabItem>(window).ToList();
                tabs.Should().HaveCount(5, "the rail's five categories materialize");

                foreach (var (tab, key) in tabs.Zip(RailKeys))
                {
                    var expected = LocalizationManager.Instance.GetString(key);
                    var header = tab.Header.Should()
                        .BeOfType<StackPanel>(
                            "the rail caption rides the header composition (T-222)")
                        .Which;
                    header.Children.OfType<TextBlock>()
                        .Should().ContainSingle(
                            caption => caption.Text == expected,
                            "'{0}' is the tab's own key", key);
                    HeadlessWorld.Descendants<Grid>(header)
                        .Any(grid => grid.Width == 16 && grid.Height == 16)
                        .Should().BeTrue("'{0}' carries the 16 px icon mark beside its caption", key);
                    HeadlessWorld.Descendants<Avalonia.Controls.Shapes.Path>(header)
                        .Should().NotBeEmpty("'{0}' carries the stroke icon mark", key);
                }
            });
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    /// <summary>
    /// The rail captions ride the BodyFontSize step (T-223), measured: the
    /// Fluent TabItem template sets its own large header type and the
    /// header compositions never carried a FontSize, so the item's setter
    /// is the only thing standing between the mockup's 13 px captions and
    /// the template's default. The item carries the setter (the caption
    /// inherits it) and the caption fits inside its item box at that step.
    /// </summary>
    [Fact]
    public async Task TheRailCaptions_RideTheBodyTypeStep_AndFitTheirItems()
    {
        var manager = await RegisterDarkTheme();
        var window = await ShowSettingsWindowAsync(ComposeSettingsViewModel(["SMA"]));
        try
        {
            await Run(() => manager.ApplyThemeAsync("dark").GetAwaiter().GetResult());
            await HeadlessWorld.OnUi(() =>
            {
                window.TryFindResource("BodyFontSize", out var stepValue).Should().BeTrue(
                    "the ramp's BodyFontSize token ships in the app resources");
                var step = (double)stepValue!;
                step.Should().Be(13, "the ramp's BodyFontSize step is the mockup's rail type");

                var tabs = HeadlessWorld.Descendants<TabItem>(window).ToList();
                foreach (var (tab, key) in tabs.Zip(RailKeys))
                {
                    Avalonia.Controls.Documents.TextElement.GetFontSize(tab).Should().Be(
                        step,
                        "'{0}' rides the item's own type setter (the template's size would win otherwise)", key);

                    var expected = LocalizationManager.Instance.GetString(key);
                    var caption = HeadlessWorld.Descendants<TextBlock>(tab)
                        .Single(text => text.Text == expected);
                    caption.FontSize.Should().Be(step, "the caption inherits the item's BodyFontSize step");
                    WindowRect(window, caption).Right.Should().BeLessThanOrEqualTo(
                        WindowRect(window, tab).Right - 0.5,
                        "'{0}'s caption fits inside its item box at the BodyFontSize step", key);
                }
            });
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    /// <summary>
    /// The rail strip rides the approved chrome: the 190 px backing
    /// touches the window's left and top edges, wears the dark palette's
    /// title-bar fill with its right hairline, and carries the rail note
    /// (decorative — the rail's clicks belong to the items).
    /// </summary>
    [Fact]
    public async Task TheRailStrip_RidesTheTitleBarBackingWithTheNote()
    {
        var manager = await RegisterDarkTheme();
        var window = await ShowSettingsWindowAsync(ComposeSettingsViewModel(["SMA"]));
        try
        {
            await Run(() => manager.ApplyThemeAsync("dark").GetAwaiter().GetResult());
            await HeadlessWorld.OnUi(() =>
            {
                var backing = HeadlessWorld.FindDescendant<Border>(window, "RailBacking");
                backing.Should().NotBeNull("the rail strip rides the approved backing chrome (T-222)");
                backing!.Bounds.Width.Should().Be(190, "the backing is the approved 190 px strip");
                WindowRect(window, backing).Left.Should().BeApproximately(
                    0, 0.5, "the strip touches the window's left edge");
                WindowRect(window, backing).Top.Should().BeApproximately(
                    WindowRect(window, HeadlessWorld.Descendants<TabControl>(window).Single()).Top,
                    0.5,
                    "the strip touches the content's top edge — the window's system frame rides above the content");
                backing.BorderThickness.Right.Should().Be(1, "the backing carries the right hairline");
                backing.BorderThickness.Left.Should().Be(0, "the hairline is the right one only");

                backing.Background.Should().NotBeNull("the backing rides the applied title-bar fill");
                ((ISolidColorBrush)backing.Background!).Color.Should().Be(
                    Color.Parse("#11111c"),
                    "the backing wears the dark palette's title-bar fill (the approved strip)");

                var note = HeadlessWorld.Descendants<TextBlock>(backing)
                    .Single(text => text.Text == LocalizationManager.Instance.GetString("Settings.Rail.Note"));
                note.IsHitTestVisible.Should().BeFalse(
                    "the note is decorative — the rail's clicks belong to the items");
            });
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    /// <summary>
    /// The footer rides the approved pinned strip: it spans the full
    /// window width and pins to the bottom edge, wears the dark palette's
    /// title-bar fill with its top hairline, and orders the quiet Close
    /// before the ONE primary Save.
    /// </summary>
    [Fact]
    public async Task TheFooterBar_PinsTheFullWidthStrip()
    {
        var manager = await RegisterDarkTheme();
        var window = await ShowSettingsWindowAsync(ComposeSettingsViewModel(["SMA"]));
        try
        {
            await Run(() => manager.ApplyThemeAsync("dark").GetAwaiter().GetResult());
            await HeadlessWorld.OnUi(() =>
            {
                var footer = HeadlessWorld.FindDescendant<Border>(window, "FooterBar");
                footer.Should().NotBeNull("the footer rides the approved pinned strip (T-222)");
                footer!.Bounds.Width.Should().BeApproximately(
                    window.ClientSize.Width, 0.5, "the strip spans the full window width");
                WindowRect(window, footer).Bottom.Should().BeApproximately(
                    window.ClientSize.Height, 0.5, "the strip pins to the window's bottom edge");
                footer.BorderThickness.Top.Should().Be(1, "the strip carries the top hairline");
                footer.Background.Should().NotBeNull("the strip rides the applied title-bar fill");
                ((ISolidColorBrush)footer.Background!).Color.Should().Be(
                    Color.Parse("#11111c"),
                    "the strip wears the dark palette's title-bar fill (the approved footer)");

                var buttons = HeadlessWorld.Descendants<Button>(footer).ToList();
                var close = buttons.Single(button => button.Name == "CloseButton");
                var save = buttons.Single(button => button.Name == "SaveButton");
                WindowRect(window, close).Left.Should().BeLessThan(
                    WindowRect(window, save).Left,
                    "the quiet Close rides before the ONE primary Save (the approved order)");
                save.Classes.Should().Contain(
                    "primary", "the Save is the strip's ONE primary button");
            });
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    /// <summary>Post one drain job on the dispatcher's owner thread.</summary>
    private static async Task Run(Action job)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(job);
        await AvaloniaDispatcherPump.DrainAsync();
    }

    /// <summary>
    /// Register the shipped dark palette under a fresh manager (the publish
    /// tests' recipe) and return it — the apply publishes the derived
    /// button keys the window's tint styles ride.
    /// </summary>
    private static async Task<ThemeManager> RegisterDarkTheme()
    {
        var dir = Directory.CreateTempSubdirectory("zt-rail-pins-");
        try
        {
            var file = System.IO.Path.Combine(dir.FullName, "dark.json");
            await File.WriteAllTextAsync(
                file,
                JsonSerializer.Serialize(new ThemeDefinition
                {
                    Name = "dark",
                    Colors = new Dictionary<string, string>(DarkPalette),
                }));

            var manager = new ThemeManager(
                NullLogger<ThemeManager>.Instance,
                dir.FullName);
            await manager.LoadCustomThemeAsync(file);

            return manager;
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>Compose a settings ViewModel over the REAL localization singleton (the polish suite's recipe).</summary>
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

    /// <summary>Materialize the settings window at its shipped default size and land its open.</summary>
    private static async Task<SettingsWindow> ShowSettingsWindowAsync(SettingsViewModel settings)
    {
        var window = await HeadlessWorld.OnUi(() => new SettingsWindow { DataContext = settings });
        await HeadlessWorld.ShowAndSettleAsync(window);
        return window;
    }

    /// <summary>The control's bounds in window coordinates.</summary>
    private static Rect WindowRect(Window window, Visual element)
    {
        var topLeft = element.TranslatePoint(new Point(0, 0), window);
        topLeft.Should().NotBeNull("the element lays out inside the window");
        return new Rect(topLeft!.Value, ((Control)element).Bounds.Size);
    }
}
