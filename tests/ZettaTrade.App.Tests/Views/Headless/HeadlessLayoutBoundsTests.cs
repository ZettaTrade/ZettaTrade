namespace ZettaTrade.App.Tests.Views.Headless;

using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
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
/// facts pin the folded tab strip and the Save/Close row staying inside
/// the minimum window. Every window touch rides the world's
/// user-interface lane (an off-thread constructor dies on VerifyAccess).
/// </summary>
[Collection("AvaloniaDispatcherSerial")]
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
                // The T-145 fold: four tab headers ride the strip — the
                // six-tab fixed frame is retired. Each header re-inks
                // from its own key, so the count and the catalog agree.
                var tabs = HeadlessWorld.Descendants<TabItem>(window).ToList();
                tabs.Should().HaveCount(4, "the folded strip carries exactly four tabs");
                foreach (var (tab, key) in tabs.Zip(
                             new[]
                             {
                                 "Settings.Tab.Appearance", "Settings.Tab.Indicators",
                                 "Settings.Tab.Integrations", "Settings.Tab.Workspace",
                             }))
                {
                    var expected = LocalizationManager.Instance.GetString(key);
                    tab.Header.Should().Be(expected, "'{0}' is the tab's own key", key);
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
            var created = new MainWindow { DataContext = main };
            created.Width = width ?? created.MinWidth;
            created.Height = created.MinHeight;
            return created;
        });
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
