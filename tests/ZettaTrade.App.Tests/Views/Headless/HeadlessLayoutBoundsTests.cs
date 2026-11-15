namespace ZettaTrade.App.Tests.Views.Headless;

using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using FluentAssertions;
using Livery.Localization;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.ViewModels;
using ZettaTrade.App.Views;
using ZettaTrade.App.Views.Panels;

/// <summary>
/// The headless layout bounds facts (T-144): the REAL windows materialize
/// against the full ViewModel world and their geometry is MEASURED, not
/// source-pinned — the acceptance fact is exactly the one the pre-T-145
/// toolbar overflow would have failed (the one-line header's auto-sum
/// spilling past the chart column onto the book panel), and the settings
/// facts pin the folded tab strip, the Save/Close row staying inside
/// the minimum window, the T-184 pages fitting their body viewport at
/// the default size (no whole-window scroll) and the T-184 rail riding
/// the keyboard (the arrows walk it, the selection follows the
/// directional focus). Every window touch rides the world's
/// user-interface lane (an off-thread constructor dies on VerifyAccess).
/// The facts share the LocalizationManager singleton with the chart-layout
/// family, so the class rides the LocalizationManagerSingleton collection:
/// a manager-creating class running concurrently would swap and dispose
/// the singleton behind a window's attach (ObjectDisposedException on the
/// language stream's BehaviorSubject — the observed poison shape).
[Collection("LocalizationManagerSingleton")]
public sealed class HeadlessLayoutBoundsTests
{
    static HeadlessLayoutBoundsTests() => AvaloniaDispatcherPump.EnsureInstalled();

    /// <summary>
    /// The shipped 1400×900 default and one narrower step — the fresh-profile
    /// shapes a real session starts at. Below these the wrap band's own floor
    /// governs: its widest control (~100 px) cannot fold below itself, and at
    /// the default persisted columns that floor meets the chart column around
    /// a 1250 px window — the honest narrow case is the compact fact's, where
    /// the minimum window rides the minimum columns and the band demonstrably
    /// folds. The pre-T-145 one-line header's auto sum overflowed every one
    /// of these widths by hundreds of pixels; the wrap band folds instead.
    /// </summary>
    [Theory]
    [InlineData(1400)]
    [InlineData(1280)]
    public async Task TheChartToolbar_StaysInsideItsColumn_AtNarrowWidths(int width)
    {
        var world = await MainWorld.CreateAsync();
        var window = await ShowMainWindowAsync(world.Main, width);
        try
        {
            await HeadlessWorld.OnUi(() => AssertToolbarStaysInsideTheChartColumn(window, width));
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    [Fact]
    public async Task TheChartToolbar_StaysInsideItsColumn_AtTheMinimumWindowSize_WithCompactColumns()
    {
        var world = await MainWorld.CreateAsync();

        // The honest minimum layout: the window at its 1024×600 floor and
        // every persisted column clamped at its own minimum — the shape
        // the column clamps exist to make workable.
        await HeadlessWorld.OnUi(() =>
        {
            world.Main.Layout.BookColumnWidth = new GridLength(UiSettingsKeys.MinBookPanelWidth);
            world.Main.Layout.TapeColumnWidth = new GridLength(UiSettingsKeys.MinTapePanelWidth);
            world.Main.Layout.WatchlistColumnWidth = new GridLength(UiSettingsKeys.MinWatchlistPanelWidth);
            world.Main.Layout.AiColumnWidth = new GridLength(UiSettingsKeys.MinAiPanelWidth);
        });

        var window = await ShowMainWindowAsync(world.Main, null);
        try
        {
            await HeadlessWorld.OnUi(() => AssertToolbarStaysInsideTheChartColumn(window, (int)window.MinWidth));
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    [Fact]
    public async Task TheSettingsWindow_KeepsThePinnedSaveCloseRow_InsideTheBounds_AtTheMinimumSize()
    {
        var world = await MainWorld.CreateAsync();
        var window = await HeadlessWorld.OnUi(() =>
        {
            var created = new SettingsWindow { DataContext = world.Main.Settings };
            created.Width = created.MinWidth;
            created.Height = created.MinHeight;
            return created;
        });
        await HeadlessWorld.ShowAndSettleAsync(window);
        try
        {
            await HeadlessWorld.OnUi(() =>
            {
                // The T-145 fold: five tab headers ride the strip — the
                // six-tab fixed frame is retired and the Notifications tab
                // stands (T-260). Each header re-inks from its own key, so
                // the count and the catalog agree.
                var tabs = HeadlessWorld.Descendants<TabItem>(window).ToList();
                tabs.Should().HaveCount(5, "the folded strip carries exactly five tabs");
                foreach (var (tab, key) in tabs.Zip(
                             new[]
                             {
                                 "Settings.Tab.Appearance", "Settings.Tab.Indicators",
                                 "Settings.Tab.Integrations", "Settings.Tab.Workspace",
                                 "Settings.Tab.Notifications",
                             }))
                {
                    var expected = LocalizationManager.Instance.GetString(key);
                    var header = tab.Header.Should()
                        .BeOfType<StackPanel>(
                            "the rail caption rides the header composition beside its icon (T-222)")
                        .Which;
                    header.Children.OfType<TextBlock>()
                        .Should().ContainSingle(
                            caption => caption.Text == expected,
                            "'{0}' is the tab's own key", key);
                }

                // The pinned Save/Close row: inside the window at the
                // minimum size — the fixed frame that pushed it out of
                // reach is gone.
                foreach (var name in new[] { "SaveButton", "CloseButton" })
                {
                    var button = HeadlessWorld.FindDescendant<Button>(window, name);
                    button.Should().NotBeNull("'{0}' is the pinned footer's control", name);

                    var rect = WindowRect(window, button!);
                    rect.Left.Should().BeGreaterThanOrEqualTo(0, "'{0}' starts inside the window", name);
                    rect.Top.Should().BeGreaterThanOrEqualTo(0, "'{0}' starts inside the window", name);
                    rect.Right.Should().BeLessThanOrEqualTo(
                        window.ClientSize.Width,
                        "'{0}' ends inside the window at the minimum size", name);
                    rect.Bottom.Should().BeLessThanOrEqualTo(
                        window.ClientSize.Height,
                        "'{0}' ends inside the window at the minimum size", name);
                }
            });
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    /// <summary>
    /// The T-184 no-scroll pin: at the shipped default size every settings
    /// page fits its body viewport — the whole-window scroll stays the
    /// small machines' safety net only (each long list keeps its own
    /// scrollport instead). The body viewer is the selected tab content's
    /// own ScrollViewer; the inner viewport caps ride their own facts.
    /// </summary>
    [Fact]
    public async Task TheSettingsPages_FitTheirBodyViewport_AtTheDefaultSize()
    {
        var world = await MainWorld.CreateAsync();
        var window = await ShowSettingsWindowAsync(world.Main.Settings);
        try
        {
            foreach (var key in new[]
                     {
                         "Settings.Tab.Appearance", "Settings.Tab.Indicators",
                         "Settings.Tab.Integrations", "Settings.Tab.Workspace",
                         "Settings.Tab.Notifications",
                     })
            {
                await SelectTabAsync(window, key);
                await HeadlessWorld.OnUi(() =>
                {
                    var body = HeadlessWorld.Descendants<TabItem>(window)
                        .Single(tab => tab.IsSelected)
                        .Content.Should().BeAssignableTo<ScrollViewer>().Which;
                    body.Extent.Height.Should().BeLessThanOrEqualTo(
                        body.Viewport.Height + 0.5,
                        "at the default size the '{0}' page fits - the body scroll is the safety net, not the shape", key);
                    body.Extent.Width.Should().BeLessThanOrEqualTo(
                        body.Viewport.Width + 0.5,
                        "at the default size the '{0}' page never overflows horizontally", key);
                });
            }
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    /// <summary>
    /// The T-184 rail keyboard fact: the rail is reachable and operable
    /// without a mouse — the Down/Up arrows walk the categories through the
    /// window's own keyboard navigation, and the selection follows the
    /// directional focus (the TabItem/TabControl focus-selection contract,
    /// no custom keyboard code).
    /// </summary>
    [Fact]
    public async Task TheSettingsRail_ArrowKeysWalkTheCategories()
    {
        var world = await MainWorld.CreateAsync();
        var window = await ShowSettingsWindowAsync(world.Main.Settings);
        try
        {
            await HeadlessWorld.OnUi(() =>
            {
                var tabs = HeadlessWorld.Descendants<TabItem>(window).ToList();
                tabs.Should().HaveCount(5, "the rail carries the five categories");
                tabs[0].IsSelected.Should().BeTrue("the window opens on the first category");

                tabs[0].Focus();
                tabs[0].RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.Down,
                    KeyModifiers = KeyModifiers.None,
                });
            });
            await AvaloniaDispatcherPump.DrainAsync();
            await HeadlessWorld.OnUi(() =>
            {
                HeadlessWorld.Descendants<TabItem>(window).ToList()[1].IsSelected.Should().BeTrue(
                    "the Down arrow walks the rail and the selection follows the directional focus");
            });

            await HeadlessWorld.OnUi(() =>
            {
                var tabs = HeadlessWorld.Descendants<TabItem>(window).ToList();
                tabs[1].Focus();
                tabs[1].RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.Up,
                    KeyModifiers = KeyModifiers.None,
                });
            });
            await AvaloniaDispatcherPump.DrainAsync();
            await HeadlessWorld.OnUi(() =>
            {
                HeadlessWorld.Descendants<TabItem>(window).ToList()[0].IsSelected.Should().BeTrue(
                    "the Up arrow walks the rail back");
            });
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    /// <summary>
    /// The T-223 fold fact: every page's card headers are real toggles —
    /// the five approved defaults open (the appearance card, the
    /// indicators page's only card, the Telegram card, MCP, pairs), seven
    /// collapsed — and flipping one unfolds its body: the one-way
    /// IsChecked binding drives the body's visibility while the checked
    /// style rotates the chevron down. The walk rides the pages: the
    /// TabControl materializes one page's content at a time, so the twelve
    /// toggles exist page by page (3 + 1 + 1 + 5 + 2 across the five
    /// pages, the Telegram card riding since T-260).
    /// </summary>
    [Fact]
    public async Task TheSectionToggles_FoldTheCards()
    {
        var world = await MainWorld.CreateAsync();
        var window = await ShowSettingsWindowAsync(world.Main.Settings);
        try
        {
            var defaults = new (string Key, string[] Open, string[] Collapsed)[]
            {
                ("Settings.Tab.Appearance",
                    new[] { "AppearanceSectionToggle" },
                    new[] { "ThemesFolderSectionToggle", "WizardSectionToggle" }),
                ("Settings.Tab.Indicators",
                    new[] { "IndicatorsSectionToggle" },
                    Array.Empty<string>()),
                ("Settings.Tab.Notifications",
                    new[] { "TelegramSectionToggle" },
                    Array.Empty<string>()),
                ("Settings.Tab.Integrations",
                    new[] { "McpSectionToggle" },
                    new[] { "AiSectionToggle", "CredentialsSectionToggle", "PortSectionToggle", "BackupSectionToggle" }),
                ("Settings.Tab.Workspace",
                    new[] { "PairsSectionToggle" },
                    new[] { "HotkeysSectionToggle" }),
            };

            var total = 0;
            foreach (var (key, open, collapsed) in defaults)
            {
                await SelectTabAsync(window, key);
                await HeadlessWorld.OnUi(() =>
                {
                    var toggles = HeadlessWorld.Descendants<ToggleButton>(window)
                        .Where(toggle => toggle.Classes.Contains("section-toggle"))
                        .ToList();
                    toggles.Should().HaveCount(
                        open.Length + collapsed.Length,
                        "the '{0}' page carries exactly its own section toggles (the enable checkbox is a ToggleButton too, but carries no section-toggle class)", key);
                    foreach (var name in open)
                    {
                        toggles.Single(toggle => toggle.Name == name).IsChecked.Should().BeTrue(
                            "'{0}' rides the open-by-default set", name);
                    }
                    foreach (var name in collapsed)
                    {
                        toggles.Single(toggle => toggle.Name == name).IsChecked.Should().BeFalse(
                            "'{0}' rides the collapsed-by-default set", name);
                    }
                    total += toggles.Count;
                });
            }

            total.Should().Be(12, "every card header folds: the pages materialize twelve toggles together");

            // The fold itself: the appearance page's theme-folder card opens
            // collapsed, so the flip makes the whole mechanics measurable —
            // the body's visibility and the chevron's rotation.
            await SelectTabAsync(window, "Settings.Tab.Appearance");
            await HeadlessWorld.OnUi(() =>
            {
                var fold = HeadlessWorld.Descendants<ToggleButton>(window)
                    .Single(toggle => toggle.Name == "ThemesFolderSectionToggle");
                HeadlessWorld.FindDescendant<StackPanel>(window, "ThemesFolderSectionBody")!
                    .IsVisible.Should().BeFalse("the collapsed default hides the body");

                var chevron = HeadlessWorld.Descendants<Avalonia.Controls.Shapes.Path>(fold)
                    .Single(path => path.Classes.Contains("section-chevron"));
                chevron.RenderTransform.Should().BeNull("the collapsed mark points right");

                fold.IsChecked = true;
            });
            await AvaloniaDispatcherPump.DrainAsync();
            await HeadlessWorld.OnUi(() =>
            {
                HeadlessWorld.FindDescendant<StackPanel>(window, "ThemesFolderSectionBody")!
                    .IsVisible.Should().BeTrue("the flip unfolds the body — the one-way IsChecked binding drives it");

                var fold = HeadlessWorld.Descendants<ToggleButton>(window)
                    .Single(toggle => toggle.Name == "ThemesFolderSectionToggle");
                var chevron = HeadlessWorld.Descendants<Avalonia.Controls.Shapes.Path>(fold)
                    .Single(path => path.Classes.Contains("section-chevron"));
                chevron.RenderTransform.Should().BeOfType<RotateTransform>()
                    .Which.Angle.Should().Be(90, "the checked state rotates the chevron down");

                fold.IsChecked = false;
            });
            await AvaloniaDispatcherPump.DrainAsync();
            await HeadlessWorld.OnUi(() =>
            {
                HeadlessWorld.FindDescendant<StackPanel>(window, "ThemesFolderSectionBody")!
                    .IsVisible.Should().BeFalse("the flip back folds the body again");
            });
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    /// <summary>
    /// The band's geometry contract: the header's controls fold inside the
    /// chart column — no control may paint past the column's right edge,
    /// and none may cross into the book panel the old auto-sum covered.
    /// </summary>
    private static void AssertToolbarStaysInsideTheChartColumn(MainWindow window, int width)
    {
        var chartHost = HeadlessWorld.FindDescendant<Border>(window, "ChartPanelHost");
        var bookHost = HeadlessWorld.FindDescendant<Border>(window, "BookPanelHost");
        var chartPanel = HeadlessWorld.FindDescendant<ChartPanelView>(window, "ChartPanel");
        chartHost.Should().NotBeNull("the chart column is the workspace's first child");
        bookHost.Should().NotBeNull("the book column rides the workspace grid beside it");
        chartPanel.Should().NotBeNull("the chart panel hosts the toolbar band");

        // The band's controls: every button and every combo the header
        // carries — the same controls the pre-T-145 one-line header laid
        // out in its overflowing auto columns.
        var bandControls = HeadlessWorld.Descendants<Control>(chartPanel!)
            .Where(control => control is Button or ComboBox)
            .ToList();
        bandControls.Should().NotBeEmpty("the toolbar band carries the chart's controls");

        var chartHostRect = WindowRect(window, chartHost!);
        var bookHostRect = WindowRect(window, bookHost!);
        var bandRight = bandControls.Max(control => WindowRect(window, control).Right);

        bandRight.Should().BeLessThanOrEqualTo(
            chartHostRect.Right + 0.5,
            "at width {0} the T-145 wrap band folds onto further lines — no control may leave the chart column", width);
        bandRight.Should().BeLessThanOrEqualTo(
            bookHostRect.Left + 0.5,
            "at width {0} the pre-T-145 regression painted the header over the book panel: the toolbar's right edge must stop before the book column begins", width);
    }

    /// <summary>
    /// Materialize the main window at the given width (null: the window's
    /// own minimum) on the user-interface lane, then land its open.
    /// </summary>
    private static async Task<MainWindow> ShowMainWindowAsync(MainViewModel main, int? width)
    {
        var window = await HeadlessWorld.OnUi(() =>
        {
            var created = new MainWindow(main);
            created.Width = width ?? created.MinWidth;
            created.Height = created.MinHeight;
            return created;
        });
        await HeadlessWorld.ShowAndSettleAsync(window);
        return window;
    }

    /// <summary>Materialize the settings window at its shipped default size and land its open.</summary>
    private static async Task<SettingsWindow> ShowSettingsWindowAsync(SettingsViewModel settings)
    {
        var window = await HeadlessWorld.OnUi(() => new SettingsWindow { DataContext = settings });
        await HeadlessWorld.ShowAndSettleAsync(window);
        return window;
    }

    /// <summary>
    /// Select one category by its localization key and land the switch on
    /// the serialized dispatcher — the selected tab's content materializes
    /// only in its lane, so the drain is what makes the facts measurable.
    /// </summary>
    private static async Task SelectTabAsync(SettingsWindow window, string headerKey)
    {
        await HeadlessWorld.OnUi(() =>
        {
            var expected = LocalizationManager.Instance.GetString(headerKey);
            HeadlessWorld.Descendants<TabItem>(window)
                .Single(tab => HeaderCarriesCaption(tab, expected))
                .IsSelected = true;
        });
    }

    /// <summary>
    /// The rail header carries the caption inside its composition's
    /// TextBlock (the icon rides beside it, T-222) — the tabs no longer
    /// ride bare string headers.
    /// </summary>
    private static bool HeaderCarriesCaption(TabItem tab, string expected) =>
        tab.Header is StackPanel header
        && HeadlessWorld.Descendants<TextBlock>(header)
            .Any(caption => caption.Text == expected);

    /// <summary>The control's bounds in window coordinates.</summary>
    private static Rect WindowRect(Window window, Visual element)
    {
        var topLeft = element.TranslatePoint(new Point(0, 0), window);
        topLeft.Should().NotBeNull("the element lays out inside the window");
        return new Rect(topLeft!.Value, ((Control)element).Bounds.Size);
    }
}
