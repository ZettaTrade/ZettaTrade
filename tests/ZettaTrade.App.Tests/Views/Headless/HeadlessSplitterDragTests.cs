namespace ZettaTrade.App.Tests.Views.Headless;

using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using FluentAssertions;
using NSubstitute;
using Xunit;
using ZettaTrade.App.Controls;
using ZettaTrade.App.Views;

/// <summary>
/// The headless resize-handle facts (T-172): the REAL main window's four
/// layout boundaries ride pointer press/move/release scripts — the same
/// input path a user's mouse feeds — and every boundary must move: the
/// boundary follows the pointer, trading space between the persisted
/// columns it serves under the standard direction (a rightward drag grows
/// the column left of the handle), with the minimum clamps honored and a
/// settled drag persisting through the settings pipeline. The grips show:
/// each boundary renders its grip line and highlights on hover. The class
/// rides the LocalizationManagerSingleton collection: every fact
/// materializes the REAL main window whose chart widgets subscribe the
/// localization and theme singletons on attach, so the manager-creating
/// classes run strictly sequentially with it.
/// </summary>
[Collection("LocalizationManagerSingleton")]
public sealed class HeadlessSplitterDragTests
{
    static HeadlessSplitterDragTests() => AvaloniaDispatcherPump.EnsureInstalled();

    [Fact]
    public async Task DraggingTheChartBookBoundaryLeft_WidensTheBookColumn()
    {
        var world = await MainWorld.CreateAsync();
        var window = await world.ShowWindowAsync();
        try
        {
            var handle = await WorkspaceHandleAsync(window, column: 1);

            await DragAsync(window, handle, delta: -50);

            await HeadlessWorld.OnUi(() =>
            {
                BookColumn(handle).Width.Value.Should().Be(350, "the leftward drag gives the book 50px");
                world.Main.Layout.BookColumnWidth.Value.Should().Be(350, "the TwoWay binding carries the drag into the ViewModel");
            });
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    [Fact]
    public async Task DraggingTheBookTapeBoundaryRight_MovesTheBoundaryBetweenTheBookAndTheTape()
    {
        var world = await MainWorld.CreateAsync();
        var window = await world.ShowWindowAsync();
        try
        {
            var handle = await WorkspaceHandleAsync(window, column: 3);

            await DragAsync(window, handle, delta: 50);

            await HeadlessWorld.OnUi(() =>
            {
                // The stock GridSplitter never moved this fixed-to-fixed
                // seam: both neighbors must trade the delta now.
                BookColumn(handle).Width.Value.Should().Be(350, "the rightward drag grows the book 50px");
                TapeColumn(handle).Width.Value.Should().Be(190, "the tape pays the 50px");
                world.Main.Layout.TapeColumnWidth.Value.Should().Be(190, "the TwoWay binding carries the drag into the ViewModel");
            });
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    [Fact]
    public async Task DraggingTheTapeWatchlistBoundaryRight_MovesTheBoundaryBetweenTheTapeAndTheWatchlist()
    {
        var world = await MainWorld.CreateAsync();
        var window = await world.ShowWindowAsync();
        try
        {
            var handle = await WorkspaceHandleAsync(window, column: 5);

            await DragAsync(window, handle, delta: 50);

            await HeadlessWorld.OnUi(() =>
            {
                TapeColumn(handle).Width.Value.Should().Be(290, "the rightward drag grows the tape 50px");
                WatchlistColumn(handle).Width.Value.Should().Be(210, "the watchlist pays the 50px");
                world.Main.Layout.WatchlistColumnWidth.Value.Should().Be(210, "the TwoWay binding carries the drag into the ViewModel");
            });
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    [Fact]
    public async Task DraggingTheAssistantBoundaryLeft_WidensTheAssistantColumn()
    {
        var world = await MainWorld.CreateAsync();
        var window = await world.ShowWindowAsync();
        try
        {
            var handle = await AssistantHandleAsync(window);

            // The standard direction: a leftward drag grows the column
            // right of the handle — the assistant pays the star workspace.
            await DragAsync(window, handle, delta: -50);

            await HeadlessWorld.OnUi(() =>
            {
                AssistantColumn(handle).Width.Value.Should().Be(350, "the leftward drag gives the assistant 50px");
                world.Main.Layout.AiColumnWidth.Value.Should().Be(350, "the TwoWay binding carries the drag into the ViewModel");
            });
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    [Fact]
    public async Task DraggingPastTheMinimum_ClampsTheShrinkingSide()
    {
        var world = await MainWorld.CreateAsync();
        var window = await world.ShowWindowAsync();
        try
        {
            var handle = await WorkspaceHandleAsync(window, column: 5);

            // A leftward sweep of 100 would take the tape below its
            // 160px minimum: the joint clamp stops the boundary there.
            await DragAsync(window, handle, delta: -100);

            await HeadlessWorld.OnUi(() =>
            {
                TapeColumn(handle).Width.Value.Should().Be(160, "the tape clamps at its minimum");
                WatchlistColumn(handle).Width.Value.Should().Be(340, "the watchlist takes only what the tape released");
            });
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    [Fact]
    public async Task ASettledBookDrag_PersistsThroughTheSettingsPipeline()
    {
        var world = await MainWorld.CreateAsync();
        var window = await world.ShowWindowAsync();
        try
        {
            var handle = await WorkspaceHandleAsync(window, column: 1);

            await DragAsync(window, handle, delta: -50);

            // The drag debounce (250 ms) runs on real time; settle past it
            // and drain the pipeline's async write.
            await Task.Delay(400);
            await AvaloniaDispatcherPump.DrainAsync();

            await world.Settings.Received(1).SetAsync(
                ZettaTrade.App.UiSettingsKeys.Section,
                ZettaTrade.App.UiSettingsKeys.BookPanelWidth,
                350d);
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    [Fact]
    public async Task EveryBoundaryShowsItsGripLine_AndHighlightsOnHover()
    {
        var world = await MainWorld.CreateAsync();
        var window = await world.ShowWindowAsync();
        try
        {
            var handles = await HeadlessWorld.OnUi(() =>
                HeadlessWorld.Descendants<ColumnResizeHandle>(window).ToList());
            handles.Should().HaveCount(4, "the workspace carries four resizable boundaries");

            foreach (var handle in handles)
            {
                await HeadlessWorld.OnUi(() =>
                {
                    var grip = handle.GetTemplateChild<Border>("GripLine");
                    grip.Should().NotBeNull("every boundary shows a visible grip line");
                    grip!.Bounds.Width.Should().Be(1, "the grip reads as a quiet hairline at rest");
                });

                // Sweep the pointer onto the handle: the grip widens and
                // the pointerover state resolves through the template.
                await HoverAsync(window, handle);

                await HeadlessWorld.OnUi(() =>
                {
                    handle.IsPointerOver.Should().BeTrue("the pointer rides the handle");
                    var grip = handle.GetTemplateChild<Border>("GripLine");
                    grip!.Bounds.Width.Should().Be(3, "the hover state widens the grip");
                });
            }
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    [Fact]
    public async Task TheFocusedHandle_StepsTheBoundaryWithArrowKeys()
    {
        var world = await MainWorld.CreateAsync();
        var window = await world.ShowWindowAsync();
        try
        {
            var handle = await WorkspaceHandleAsync(window, column: 1);

            await HeadlessWorld.OnUi(() =>
            {
                handle.Focus();
                handle.RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.Left,
                    KeyModifiers = KeyModifiers.None,
                });
            });
            await AvaloniaDispatcherPump.DrainAsync();

            await HeadlessWorld.OnUi(() =>
            {
                BookColumn(handle).Width.Value.Should().Be(310, "the Left key grows the book by the keyboard step");
                world.Main.Layout.BookColumnWidth.Value.Should().Be(310, "the TwoWay binding carries the keyboard step too");
            });
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    /// <summary>
    /// Reach one workspace handle by its grid column: the book panel host
    /// names the workspace grid, whose children carry the three workspace
    /// boundaries (columns 1, 3, 5).
    /// </summary>
    private static async Task<ColumnResizeHandle> WorkspaceHandleAsync(Window window, int column)
    {
        return await HeadlessWorld.OnUi(() =>
        {
            var host = HeadlessWorld.FindDescendant<Border>(window, "BookPanelHost");
            host.Should().NotBeNull("the book panel host names the workspace");
            var workspace = (Grid)host!.Parent!;
            return workspace.Children
                .OfType<ColumnResizeHandle>()
                .First(handle => Grid.GetColumn(handle) == column);
        });
    }

    /// <summary>
    /// Reach the assistant boundary: the only handle whose parent grid
    /// holds three columns (the outer workspace/AI split).
    /// </summary>
    private static async Task<ColumnResizeHandle> AssistantHandleAsync(Window window)
    {
        return await HeadlessWorld.OnUi(() =>
            HeadlessWorld.Descendants<ColumnResizeHandle>(window)
                .Single(handle => handle.Parent is Grid grid && grid.ColumnDefinitions.Count == 3));
    }

    private static ColumnDefinition BookColumn(ColumnResizeHandle handle) =>
        ((Grid)handle.Parent!).ColumnDefinitions[2];

    private static ColumnDefinition TapeColumn(ColumnResizeHandle handle) =>
        ((Grid)handle.Parent!).ColumnDefinitions[4];

    private static ColumnDefinition WatchlistColumn(ColumnResizeHandle handle) =>
        ((Grid)handle.Parent!).ColumnDefinitions[6];

    private static ColumnDefinition AssistantColumn(ColumnResizeHandle handle) =>
        ((Grid)handle.Parent!).ColumnDefinitions[2];

    /// <summary>Press the handle's center, sweep by the delta, release.</summary>
    private static async Task DragAsync(Window window, ColumnResizeHandle handle, double delta)
    {
        await HeadlessWorld.OnUi(() =>
        {
            var origin = CenterOf(window, handle);
            var target = origin + new Vector(delta, 0);

            window.MouseDown(origin, MouseButton.Left);
            window.MouseMove(origin + new Vector(delta / 2, 0));
            window.MouseMove(target);
            window.MouseUp(target, MouseButton.Left);
        });
        await AvaloniaDispatcherPump.DrainAsync();
    }

    /// <summary>Move the pointer onto the handle and let the hover state settle.</summary>
    private static async Task HoverAsync(Window window, ColumnResizeHandle handle)
    {
        await HeadlessWorld.OnUi(() => window.MouseMove(CenterOf(window, handle)));
        await AvaloniaDispatcherPump.DrainAsync();
    }

    private static Point CenterOf(Window window, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
            ?? throw new Xunit.Sdk.XunitException("the handle lays out inside the window");
}

/// <summary>The template-part lookup the facts share: one named visual child.</summary>
file static class TemplateChildExtensions
{
    public static T? GetTemplateChild<T>(this Control control, string name)
        where T : Control =>
        HeadlessWorld.Descendants<T>(control).FirstOrDefault(child => child.Name == name);
}
