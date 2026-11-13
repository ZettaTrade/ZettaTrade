namespace ZettaTrade.App.Tests.Views.Headless;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using FluentAssertions;
using NSubstitute;
using Xunit;
using ZettaTrade.App.Controls;
using ZettaTrade.App.ViewModels;
using ZettaTrade.App.Views;
using ZettaTrade.App.Views.Panels;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The multi-chart grid's headless facts (T-201): the REAL main window
/// tiles its chart column exactly as the workspace ViewModel's mode says
/// — the startup restore lands a stored two-by-two before the window
/// opens (the composition's walk, mirrored), the View menu's pick tiles
/// the primary and slot one behind the draggable seam while the slot's
/// rail strip stays the primary's surface alone, the slots persist their
/// picks under their own per-slot keys while the primary keeps the
/// shared ones, a switch back re-shows the single panel without tearing
/// the session's slots down, the seams ride pointer drags that persist
/// once per settled burst, the shared slot column keeps both grid rows'
/// seams on one aligned line, and the four-face grid keeps rastering
/// within the frame budget. The class rides the
/// LocalizationManagerSingleton collection: every fact materializes the
/// REAL main window whose chart widgets subscribe the localization and
/// theme singletons on attach, so the manager-creating classes run
/// strictly sequentially with it.
/// </summary>
[Collection("LocalizationManagerSingleton")]
public sealed class HeadlessChartGridTests
{
    static HeadlessChartGridTests() => AvaloniaDispatcherPump.EnsureInstalled();

    /// <summary>How many frames the four-face perf fact captures.</summary>
    private const int CaptureCount = 12;

    /// <summary>
    /// The generous per-capture median bound (logical milliseconds): a
    /// four-face grid must stay far below a dropped-frame blowup even on
    /// the gate's shared runner, while a runaway render (the layout
    /// rebuilding per frame, say) blows past it at once.
    /// </summary>
    private const double PerfBudgetMilliseconds = 500;

    [Fact]
    public async Task ARestoredTwoByTwo_TilesTheChartColumnIntoFourFaces()
    {
        var world = await MainWorld.CreateAsync();
        // The composition's startup walk, mirrored: the stored mode
        // restores BEFORE any window opens, the substitute's double reads
        // echo the stored defaults back (a fresh profile's shape).
        world.Settings.GetAsync<string>(
                UiSettingsKeys.Section,
                UiSettingsKeys.ChartLayout,
                Arg.Any<string>())
            .Returns(Task.FromResult("TwoByTwo"));
        world.Settings.GetAsync<double>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>())
            .Returns(ci => Task.FromResult(ci.ArgAt<double>(2)));
        await world.Main.Charts.InitializeAsync();
        await AwaitSlotsSettledAsync(world.Main.Charts.Slot1, world.Main.Charts.Slot2, world.Main.Charts.Slot3);

        var window = await world.ShowWindowAsync();
        try
        {
            await HeadlessWorld.OnUi(() =>
            {
                var charts = world.Main.Charts;
                var grid = HeadlessWorld.FindDescendant<ChartSlotGrid>(window, "ChartGrid")!;
                grid.SlotPanels.Count.Should().Be(3, "the two-by-two restore places exactly the mode's three slots");

                var slot1 = HeadlessWorld.FindDescendant<ChartPanelView>(window, "ChartSlot1Panel");
                var slot2 = HeadlessWorld.FindDescendant<ChartPanelView>(window, "ChartSlot2Panel");
                var slot3 = HeadlessWorld.FindDescendant<ChartPanelView>(window, "ChartSlot3Panel");
                slot1.Should().NotBeNull("the top-right face carries slot one");
                slot2.Should().NotBeNull("the bottom-left face carries slot two");
                slot3.Should().NotBeNull("the bottom-right face carries slot three");
                slot1!.DataContext.Should().BeSameAs(charts.Slot1, "each face rides its own slot ViewModel");
                slot2!.DataContext.Should().BeSameAs(charts.Slot2, "each face rides its own slot ViewModel");
                slot3!.DataContext.Should().BeSameAs(charts.Slot3, "each face rides its own slot ViewModel");

                var topRow = (Grid)slot1.Parent!;
                var bottomRow = (Grid)slot3.Parent!;
                Grid.GetRow(slot1).Should().Be(0, "slot one rides the top row");
                Grid.GetColumn(slot1).Should().Be(2, "slot one rides the shared slot column");
                Grid.GetColumn(slot2).Should().Be(0, "slot two rides the bottom-left face");
                Grid.GetColumn(slot3).Should().Be(2, "slot three rides the shared slot column");

                var host = HeadlessWorld.FindDescendant<Border>(window, "ChartPanelHost");
                host.Should().NotBeNull("the primary's swap host survives the rebuild (the detach coordinator's reference)");
                Grid.GetRow(host!).Should().Be(0, "the primary rides the top row");
                Grid.GetColumn(host!).Should().Be(0, "the primary rides the top-left face");
                host!.Parent.Should().BeSameAs(topRow, "the host is reparented into the rebuilt primary cell, never rebuilt");

                HeadlessWorld.Descendants<ColumnResizeHandle>(window)
                    .Count(handle => AutomationProperties.GetAutomationId(handle) == "ChartTopSlotColumnHandle")
                    .Should().Be(1, "the top row carries its slot seam");
                HeadlessWorld.Descendants<ColumnResizeHandle>(window)
                    .Count(handle => AutomationProperties.GetAutomationId(handle) == "ChartBottomSlotColumnHandle")
                    .Should().Be(1, "the bottom row carries its slot seam");
                HeadlessWorld.Descendants<RowResizeHandle>(window)
                    .Count(handle => AutomationProperties.GetAutomationId(handle) == "ChartGridRowHandle")
                    .Should().Be(1, "the grid carries its horizontal seam");

                topRow.ColumnDefinitions[2].Width.Value.Should().Be(charts.SlotColumnWidth.Value, "the top row rides the shared slot column");
                bottomRow.ColumnDefinitions[2].Width.Value.Should().Be(charts.SlotColumnWidth.Value, "both rows keep one aligned slot column");
            });

            await RadioMirrorsAsync(window, ChartLayoutMode.TwoByTwo);

            // The restore reads: the stored mode it just applied is never
            // rewritten behind the window's back.
            await world.Settings.DidNotReceive().SetAsync(
                UiSettingsKeys.Section,
                UiSettingsKeys.ChartLayout,
                Arg.Any<string>());
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    [Fact]
    public async Task TheOneByTwoPick_TilesThePrimaryAndSlotOne_AndHidesTheSlotRail()
    {
        var world = await MainWorld.CreateAsync();
        var window = await world.ShowWindowAsync();
        try
        {
            await HeadlessWorld.OnUi(() =>
            {
                var grid = HeadlessWorld.FindDescendant<ChartSlotGrid>(window, "ChartGrid")!;
                grid.SlotPanels.Should().BeEmpty("the window opens on the single-panel layout");
                var host = HeadlessWorld.FindDescendant<Border>(window, "ChartPanelHost");
                host.Should().NotBeNull("the primary lives in the single cell");
                ((Grid)host!.Parent!).ColumnDefinitions.Should().HaveCount(1, "the single layout is one cell");
            });

            // The pick rides the menu item's own bound command — the exact
            // route a click feeds — so the wiring pin and the pick share
            // one fact.
            var item = await HeadlessWorld.OnUi(() =>
                window.GetLogicalDescendants().OfType<MenuItem>()
                    .Single(candidate => AutomationProperties.GetAutomationId(candidate) == "ViewLayoutOneByTwoMenuItem"));
            await HeadlessWorld.OnUi(() =>
            {
                item.Command.Should().BeSameAs(
                    world.Main.Charts.SetLayoutCommand,
                    "the menu item routes the workspace's layout command");
                item.CommandParameter.Should().Be(ChartLayoutMode.OneByTwo, "the item's parameter is the mode it names");
                item.Command!.Execute(item.CommandParameter);
            });
            await AvaloniaDispatcherPump.DrainAsync();
            await AwaitSlotsSettledAsync(world.Main.Charts.Slot1);

            await HeadlessWorld.OnUi(() =>
            {
                var charts = world.Main.Charts;
                var grid = HeadlessWorld.FindDescendant<ChartSlotGrid>(window, "ChartGrid")!;
                grid.SlotPanels.Count.Should().Be(1, "the one-by-two pick places slot one beside the primary");

                var slot1 = HeadlessWorld.FindDescendant<ChartPanelView>(window, "ChartSlot1Panel");
                slot1.Should().NotBeNull("slot one's face exists");
                slot1!.DataContext.Should().BeSameAs(charts.Slot1, "the face rides the slot ViewModel");
                var rowGrid = (Grid)slot1.Parent!;
                rowGrid.ColumnDefinitions.Should().HaveCount(3, "the one-by-two row is star, seam, slot column");
                Grid.GetColumn(slot1).Should().Be(2, "slot one rides the slot column");
                Grid.GetColumn(rowGrid.Children.OfType<Border>().First(border => border.Name == "ChartPanelHost"))
                    .Should().Be(0, "the primary rides the star column");

                // The slot's rail strip is the primary's surface alone: the
                // collapse zeroes the strip row's clamps and hides the
                // whole strip, splitter track and all.
                var slotRail = HeadlessWorld.FindDescendant<Border>(slot1, "RailBorder");
                slotRail.Should().NotBeNull("the slot face carries the rail structure");
                slotRail!.IsVisible.Should().BeFalse("a slot never repeats the primary's rail strip");
                var slotGrid = HeadlessWorld.FindDescendant<Grid>(slot1, "PanelGrid")!;
                slotGrid.RowDefinitions[4].Height.Value.Should().Be(0, "the slot's splitter track row collapses");
                slotGrid.RowDefinitions[5].MinHeight.Should().Be(0, "the slot's strip row loses its persisted minimum");
                slotGrid.RowDefinitions[5].MaxHeight.Should().Be(0, "the slot's strip row loses its persisted maximum");

                var primaryPanel = HeadlessWorld.FindDescendant<ChartPanelView>(window, "ChartPanel");
                primaryPanel.Should().NotBeNull("the primary keeps its face");
                var primaryRail = HeadlessWorld.FindDescendant<Border>(primaryPanel!, "RailBorder");
                primaryRail!.IsVisible.Should().BeTrue("the primary keeps its rail strip");
                var primaryGrid = HeadlessWorld.FindDescendant<Grid>(primaryPanel!, "PanelGrid")!;
                primaryGrid.RowDefinitions[5].MinHeight.Should().BeGreaterThan(0, "the primary's strip keeps its persisted height");
                primaryGrid.RowDefinitions[4].Height.Value.Should().Be(8, "the primary keeps its splitter track");
            });

            await RadioMirrorsAsync(window, ChartLayoutMode.OneByTwo);

            await world.Settings.Received(1).SetAsync(
                UiSettingsKeys.Section,
                UiSettingsKeys.ChartLayout,
                "OneByTwo");
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    [Fact]
    public async Task SlotFacesPersistTheirPicksUnderTheirOwnKeys_ThePrimaryKeepsTheSharedOnes()
    {
        var world = await MainWorld.CreateAsync();
        var window = await world.ShowWindowAsync();
        try
        {
            await PickLayoutAsync(world, window, "ViewLayoutOneByTwoMenuItem");
            await AwaitSlotsSettledAsync(world.Main.Charts.Slot1);

            await HeadlessWorld.OnUi(() =>
            {
                // The slot's own picks land on its face.
                world.Main.Charts.Slot1!.SelectedTimeFrame = TimeFrame.H4;
                world.Main.Charts.Slot1.SelectedSymbol = Symbol.Parse("ETH/USDT");
                // The primary's pick rides the workspace's shared keys.
                world.Main.Chart.SelectedTimeFrame = TimeFrame.H4;
            });
            await AvaloniaDispatcherPump.DrainAsync();

            await world.Settings.Received(1).SetAsync(
                UiSettingsKeys.Section,
                $"{UiSettingsKeys.ChartSlotPrefix}1TimeFrame",
                "H4");
            await world.Settings.Received(1).SetAsync(
                UiSettingsKeys.Section,
                $"{UiSettingsKeys.ChartSlotPrefix}1SymbolId",
                "ETH/USDT");
            await world.Settings.Received(1).SetAsync(
                UiSettingsKeys.Section,
                UiSettingsKeys.ChartTimeFrame,
                "H4");

            // A slot never writes the workspace's shared symbol context —
            // the order book, the tape and the order dialog follow the
            // primary's pick alone.
            await world.Settings.DidNotReceive().SetAsync(
                UiSettingsKeys.WorkspaceSection,
                UiSettingsKeys.WorkspaceSymbolId,
                Arg.Any<string>());
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    [Fact]
    public async Task SwitchingBackToSingle_EmptiesTheGrid_KeepsTheSlotsAlive()
    {
        var world = await MainWorld.CreateAsync();
        var window = await world.ShowWindowAsync();
        try
        {
            await PickLayoutAsync(world, window, "ViewLayoutOneByTwoMenuItem");
            await AwaitSlotsSettledAsync(world.Main.Charts.Slot1);
            var slot1 = world.Main.Charts.Slot1;

            await PickLayoutAsync(world, window, "ViewLayoutSingleMenuItem");

            await HeadlessWorld.OnUi(() =>
            {
                var charts = world.Main.Charts;
                var grid = HeadlessWorld.FindDescendant<ChartSlotGrid>(window, "ChartGrid")!;
                grid.SlotPanels.Should().BeEmpty("the single layout places no slot faces");
                charts.Slot1.Should().BeSameAs(slot1, "slots are never torn down within the session — a switch back re-shows them");

                var host = HeadlessWorld.FindDescendant<Border>(window, "ChartPanelHost");
                host.Should().NotBeNull("the primary's swap host returns to the single cell");
                var singleGrid = (Grid)host!.Parent!;
                singleGrid.ColumnDefinitions.Should().HaveCount(1, "the single layout is one cell");
                Grid.GetColumn(host).Should().Be(0, "the host fills the cell");
            });

            await RadioMirrorsAsync(window, ChartLayoutMode.Single);

            await world.Settings.Received(1).SetAsync(
                UiSettingsKeys.Section,
                UiSettingsKeys.ChartLayout,
                "Single");
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    [Fact]
    public async Task TheSlotSeamDragsPersistThroughTheSettingsPipeline()
    {
        var world = await MainWorld.CreateAsync();
        var window = await world.ShowWindowAsync();
        try
        {
            await PickLayoutAsync(world, window, "ViewLayoutOneByTwoMenuItem");
            await AwaitSlotsSettledAsync(world.Main.Charts.Slot1);

            var handle = await ColumnHandleAsync(window, "ChartSlotColumnHandle");
            var before = await HeadlessWorld.OnUi(() => ((Grid)handle.Parent!).ColumnDefinitions[2].ActualWidth);

            // A leftward drag grows the slot column (the star pays it).
            await DragAsync(window, handle, new Vector(-50, 0));

            await HeadlessWorld.OnUi(() =>
            {
                world.Main.Charts.SlotColumnWidth.Value.Should().Be(before + 50, "the ride carries the drag into the ViewModel");
            });

            // The drag debounce runs on real time; settle past it and
            // drain the pipeline's async write.
            await Task.Delay(400);
            await AvaloniaDispatcherPump.DrainAsync();

            await world.Settings.Received(1).SetAsync(
                UiSettingsKeys.Section,
                UiSettingsKeys.ChartSlotColumnWidth,
                before + 50);
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    [Fact]
    public async Task TheTopRowSeamDragsPersistThroughTheSettingsPipeline()
    {
        var world = await MainWorld.CreateAsync();
        var window = await world.ShowWindowAsync();
        try
        {
            await PickLayoutAsync(world, window, "ViewLayoutTwoByTwoMenuItem");
            await AwaitSlotsSettledAsync(world.Main.Charts.Slot1, world.Main.Charts.Slot2, world.Main.Charts.Slot3);

            var handle = await RowHandleAsync(window, "ChartGridRowHandle");
            var before = await HeadlessWorld.OnUi(() => ((Grid)handle.Parent!).RowDefinitions[0].ActualHeight);

            // The standard direction: a downward drag grows the row above.
            await DragAsync(window, handle, new Vector(0, 50));

            await HeadlessWorld.OnUi(() =>
            {
                world.Main.Charts.TopRowHeight.Value.Should().Be(before + 50, "the ride carries the drag into the ViewModel");
            });

            await Task.Delay(400);
            await AvaloniaDispatcherPump.DrainAsync();

            await world.Settings.Received(1).SetAsync(
                UiSettingsKeys.Section,
                UiSettingsKeys.ChartGridTopRowHeight,
                before + 50);
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    [Fact]
    public async Task DraggingTheTopSlotSeam_AlignsBothRowsSlotColumns()
    {
        var world = await MainWorld.CreateAsync();
        var window = await world.ShowWindowAsync();
        try
        {
            await PickLayoutAsync(world, window, "ViewLayoutTwoByTwoMenuItem");
            await AwaitSlotsSettledAsync(world.Main.Charts.Slot1, world.Main.Charts.Slot2, world.Main.Charts.Slot3);

            var handle = await ColumnHandleAsync(window, "ChartTopSlotColumnHandle");
            var before = await HeadlessWorld.OnUi(() => ((Grid)handle.Parent!).ColumnDefinitions[2].ActualWidth);

            await DragAsync(window, handle, new Vector(-50, 0));

            await HeadlessWorld.OnUi(() =>
            {
                var topRow = (Grid)handle.Parent!;
                var bottomHandle = HeadlessWorld.Descendants<ColumnResizeHandle>(window)
                    .Single(candidate => AutomationProperties.GetAutomationId(candidate) == "ChartBottomSlotColumnHandle");
                var bottomRow = (Grid)bottomHandle.Parent!;

                topRow.ColumnDefinitions[2].Width.Value.Should().Be(before + 50, "the dragged row's seam follows the pointer");
                bottomRow.ColumnDefinitions[2].Width.Value.Should().Be(before + 50, "the other row's seam rides the same shared column");
                world.Main.Charts.SlotColumnWidth.Value.Should().Be(before + 50, "both rides converge on one ViewModel value");
            });
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    [Fact]
    public async Task TheFourFaceGrid_CapturesFramesWithinThePerfBudget()
    {
        var world = await MainWorld.CreateAsync();
        var window = await world.ShowWindowAsync();
        try
        {
            // Widen the window first: the four faces need the room for the
            // capture to raster the full grid, not a clipped column.
            await HeadlessWorld.OnUi(() =>
            {
                window.Width = 1920;
                window.Height = 1000;
            });
            await AvaloniaDispatcherPump.DrainAsync();

            await PickLayoutAsync(world, window, "ViewLayoutTwoByTwoMenuItem");
            await AwaitSlotsSettledAsync(world.Main.Charts.Slot1, world.Main.Charts.Slot2, world.Main.Charts.Slot3);

            await HeadlessWorld.OnUi(() =>
            {
                HeadlessWorld.Descendants<ChartPanelView>(window)
                    .Should().HaveCount(4, "the grid tiles the primary and its three slots");
            });

            var samples = new List<double>();
            for (var index = 0; index < CaptureCount; index++)
            {
                var stopwatch = Stopwatch.StartNew();
                var bitmap = await HeadlessWorld.OnUi(() => window.CaptureRenderedFrame());
                stopwatch.Stop();
                bitmap.Should().NotBeNull("the four-face grid must keep rastering");
                bitmap!.Dispose();
                samples.Add(stopwatch.Elapsed.TotalMilliseconds);
            }

            var median = samples.OrderBy(sample => sample).ElementAt(samples.Count / 2);
            median.Should().BeLessThanOrEqualTo(
                PerfBudgetMilliseconds,
                "the four-face grid must raster a settled frame within the budget (the median rides out the runner's outliers)");
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    /// <summary>
    /// The View menu's radio items mirror the workspace's current layout:
    /// the picked mode checks its item, the others uncheck.
    /// </summary>
    private static async Task RadioMirrorsAsync(Window window, ChartLayoutMode picked)
    {
        await HeadlessWorld.OnUi(() =>
        {
            foreach (var mode in new[] { ChartLayoutMode.Single, ChartLayoutMode.OneByTwo, ChartLayoutMode.TwoByTwo })
            {
                var item = window.GetLogicalDescendants().OfType<MenuItem>()
                    .Single(candidate => AutomationProperties.GetAutomationId(candidate) == $"ViewLayout{mode}MenuItem");
                item.IsChecked.Should().Be(mode == picked, $"the {mode} radio item mirrors the workspace's layout");
            }
        });
    }

    /// <summary>
    /// Pick a layout through the menu item's own bound command — the
    /// exact route a click feeds — and land the rebuild on the
    /// serialized dispatcher.
    /// </summary>
    private static async Task PickLayoutAsync(MainWorld world, Window window, string automationId)
    {
        var item = await HeadlessWorld.OnUi(() =>
            window.GetLogicalDescendants().OfType<MenuItem>()
                .Single(candidate => AutomationProperties.GetAutomationId(candidate) == automationId));
        await HeadlessWorld.OnUi(() =>
        {
            item.Command.Should().NotBeNull($"the layout item rides the window-routed command ({automationId})");
            item.Command!.Execute(item.CommandParameter);
        });
        await AvaloniaDispatcherPump.DrainAsync();
    }

    /// <summary>Reach a column seam by its automation id (one per grid).</summary>
    private static async Task<ColumnResizeHandle> ColumnHandleAsync(Window window, string automationId) =>
        await HeadlessWorld.OnUi(() =>
            HeadlessWorld.Descendants<ColumnResizeHandle>(window)
                .Single(handle => AutomationProperties.GetAutomationId(handle) == automationId));

    /// <summary>Reach a row seam by its automation id.</summary>
    private static async Task<RowResizeHandle> RowHandleAsync(Window window, string automationId) =>
        await HeadlessWorld.OnUi(() =>
            HeadlessWorld.Descendants<RowResizeHandle>(window)
                .Single(handle => AutomationProperties.GetAutomationId(handle) == automationId));

    /// <summary>
    /// One settled-quiet wait the facts share: the slot init walks run on
    /// the serialized dispatcher, so a mode's slots are ready exactly when
    /// their selections landed.
    /// </summary>
    private static async Task AwaitSlotsSettledAsync(params ChartViewModel?[] slots)
    {
        var budget = TimeSpan.FromSeconds(5);
        var started = DateTime.UtcNow;
        while (true)
        {
            if (slots.All(slot => slot?.SelectedSymbol is not null))
            {
                return;
            }

            if (DateTime.UtcNow - started > budget)
            {
                throw new Xunit.Sdk.XunitException("the slots' init walks never landed their selections");
            }

            await Task.Delay(25);
            await AvaloniaDispatcherPump.DrainAsync();
        }
    }

    /// <summary>Press the handle's center, sweep straight to the target, release: ONE drag event, so the Thumb's reported delta is the whole sweep and the seam's write lands once.</summary>
    private static async Task DragAsync(Window window, Control handle, Vector delta)
    {
        await HeadlessWorld.OnUi(() =>
        {
            var origin = CenterOf(window, handle);
            var target = origin + delta;

            window.MouseDown(origin, MouseButton.Left);
            window.MouseMove(target);
            window.MouseUp(target, MouseButton.Left);
        });
        await AvaloniaDispatcherPump.DrainAsync();
    }

    private static Point CenterOf(Window window, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
            ?? throw new Xunit.Sdk.XunitException("the handle lays out inside the window");
}
