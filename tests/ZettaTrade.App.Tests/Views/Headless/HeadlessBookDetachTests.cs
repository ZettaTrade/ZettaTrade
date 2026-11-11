namespace ZettaTrade.App.Tests.Views.Headless;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using FluentAssertions;
using NSubstitute;
using Xunit;
using ZettaTrade.App.Controls;
using ZettaTrade.App.Services;
using ZettaTrade.App.Views;

/// <summary>
/// The headless book-detach facts (T-173): the REAL main window's book
/// column frees with its panel — the detach collapses the column, both its
/// splitter tracks and its minimum clamp to zero so the chart star absorbs
/// the width, the attach puts the width the user saw right back, and the
/// collapse never writes the settings (the width keeps its persisted truth
/// for the next session). With the panel away the book-side grab handles
/// hide too, so a drag where they used to sit cannot resurrect a phantom
/// column. The class rides the LocalizationManagerSingleton collection:
/// every fact materializes the REAL main window whose chart widgets
/// subscribe the localization and theme singletons on attach, so the
/// manager-creating classes run strictly sequentially with it.
/// </summary>
[Collection("LocalizationManagerSingleton")]
public sealed class HeadlessBookDetachTests
{
    static HeadlessBookDetachTests() => AvaloniaDispatcherPump.EnsureInstalled();

    [Fact]
    public async Task DetachingTheBook_FreesItsColumnForTheChart_AndAttachRestoresIt()
    {
        var world = await MainWorld.CreateAsync();
        var window = await world.ShowWindowAsync();
        try
        {
            var probe = await WorkspaceProbeAsync(window);

            // A settled drag first: the detach must bank the width the user
            // actually saw (350 after the sweep), not a stale default.
            await DragAsync(window, probe.BookHandle, delta: -50);
            await HeadlessWorld.OnUi(() =>
                probe.Workspace.ColumnDefinitions[2].Width.Value.Should().Be(350, "the drag shapes the width the detach banks"));

            var before = await HeadlessWorld.OnUi(() => (
                ChartWidth: ChartHostWidth(probe.Workspace),
                HostWidth: probe.Host.Bounds.Width));

            await world.Detacher.DetachPanelWindowAsync(PanelDetachId.Book, CancellationToken.None);
            await AvaloniaDispatcherPump.DrainAsync();

            var detached = await HeadlessWorld.OnUi(() => (
                BookWidth: probe.Workspace.ColumnDefinitions[2].Width.Value,
                BookActual: probe.Workspace.ColumnDefinitions[2].ActualWidth,
                BookHandleVisible: probe.BookHandle.IsVisible,
                TapeHandleVisible: probe.TapeHandle.IsVisible,
                TrackWidth: probe.Workspace.ColumnDefinitions[1].Width.Value,
                ChartWidth: ChartHostWidth(probe.Workspace),
                HostWidth: probe.Host.Bounds.Width));

            detached.BookWidth.Should().Be(0, "the detached book frees its column");
            detached.BookActual.Should().Be(0, "the collapsed column lays out at zero");
            detached.BookHandleVisible.Should().BeFalse("the chart/book handle hides with the panel");
            detached.TapeHandleVisible.Should().BeFalse("the book/tape handle hides too — its left neighbor is gone");
            detached.TrackWidth.Should().Be(0, "the book-side tracks collapse with the panel");
            detached.HostWidth.Should().Be(0, "the inline host stands down with the column");
            detached.ChartWidth.Should().BeGreaterThan(
                before.ChartWidth + 300,
                "the chart star absorbs the freed width (the 350px book plus its tracks)");

            await world.Detacher.AttachPanelWindowAsync(PanelDetachId.Book, CancellationToken.None);
            await AvaloniaDispatcherPump.DrainAsync();

            var attached = await HeadlessWorld.OnUi(() => (
                BookWidth: probe.Workspace.ColumnDefinitions[2].Width.Value,
                BookHandleVisible: probe.BookHandle.IsVisible,
                TapeHandleVisible: probe.TapeHandle.IsVisible,
                TrackWidth: probe.Workspace.ColumnDefinitions[1].Width.Value,
                ChartWidth: ChartHostWidth(probe.Workspace),
                HostWidth: probe.Host.Bounds.Width));

            attached.BookWidth.Should().Be(350, "the attach restores the width the user saw");
            attached.BookHandleVisible.Should().BeTrue("the handles come back with the panel");
            attached.TapeHandleVisible.Should().BeTrue();
            attached.TrackWidth.Should().Be(8, "the tracks return at their fixed thickness");
            attached.HostWidth.Should().BeGreaterThan(0, "the inline host takes the panel back");
            attached.ChartWidth.Should().BeApproximately(before.ChartWidth, 1, "the chart star gives the width back");
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    [Fact]
    public async Task DetachingTheBook_NeverPersistsTheCollapse()
    {
        var world = await MainWorld.CreateAsync();
        var window = await world.ShowWindowAsync();
        try
        {
            await world.Detacher.DetachPanelWindowAsync(PanelDetachId.Book, CancellationToken.None);
            await AvaloniaDispatcherPump.DrainAsync();

            // The drag debounce (250 ms) runs on real time; settle past it
            // and drain before the silence claim.
            await Task.Delay(400);
            await AvaloniaDispatcherPump.DrainAsync();

            await world.Settings.DidNotReceive().SetAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<double>());
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    [Fact]
    public async Task ADragWhileDetached_CannotResurrectTheColumn()
    {
        var world = await MainWorld.CreateAsync();
        var window = await world.ShowWindowAsync();
        try
        {
            var probe = await WorkspaceProbeAsync(window);
            var seam = await HeadlessWorld.OnUi(() => CenterOf(window, probe.TapeHandle));

            await world.Detacher.DetachPanelWindowAsync(PanelDetachId.Book, CancellationToken.None);
            await AvaloniaDispatcherPump.DrainAsync();

            // The handle hides with the panel; the pointer sweep at the old
            // seam lands on whatever took the space (the tape's border), and
            // no Thumb rides it — the phantom column must not come back.
            await HeadlessWorld.OnUi(() =>
            {
                window.MouseDown(seam, MouseButton.Left);
                window.MouseMove(seam + new Vector(-40, 0));
                window.MouseUp(seam + new Vector(-40, 0), MouseButton.Left);
            });
            await AvaloniaDispatcherPump.DrainAsync();

            await HeadlessWorld.OnUi(() =>
            {
                probe.Workspace.ColumnDefinitions[2].Width.Value.Should().Be(0, "the sweep cannot resurrect the detached book's column");
                probe.Workspace.ColumnDefinitions[4].Width.Value.Should().Be(240, "the tape keeps its own width untouched");
            });
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    /// <summary>
    /// The workspace probe: the book panel host names the workspace grid,
    /// whose children carry the book-side handles (columns 1 and 3) and the
    /// swap host the panel lives in.
    /// </summary>
    private static async Task<(Grid Workspace, Border Host, ColumnResizeHandle BookHandle, ColumnResizeHandle TapeHandle)> WorkspaceProbeAsync(
        Window window)
    {
        return await HeadlessWorld.OnUi(() =>
        {
            var host = HeadlessWorld.FindDescendant<Border>(window, "BookPanelHost");
            host.Should().NotBeNull("the book panel host names the workspace");
            var workspace = (Grid)host!.Parent!;
            var bookHandle = workspace.Children
                .OfType<ColumnResizeHandle>()
                .First(handle => Grid.GetColumn(handle) == 1);
            var tapeHandle = workspace.Children
                .OfType<ColumnResizeHandle>()
                .First(handle => Grid.GetColumn(handle) == 3);
            return (workspace, host, bookHandle, tapeHandle);
        });
    }

    /// <summary>The chart host's laid-out width: the star side of the chart/book boundary.</summary>
    private static double ChartHostWidth(Grid workspace) =>
        HeadlessWorld.Descendants<Border>(workspace)
            .First(border => border.Name == "ChartPanelHost")
            .Bounds.Width;

    /// <summary>Press the handle's center, sweep by the delta, release.</summary>
    private static async Task DragAsync(Window window, ColumnResizeHandle handle, double delta)
    {
        await HeadlessWorld.OnUi(() =>
        {
            var origin = CenterOf(window, handle);
            window.MouseDown(origin, MouseButton.Left);
            window.MouseMove(origin + new Vector(delta / 2, 0));
            window.MouseMove(origin + new Vector(delta, 0));
            window.MouseUp(origin + new Vector(delta, 0), MouseButton.Left);
        });
        await AvaloniaDispatcherPump.DrainAsync();
    }

    private static Point CenterOf(Window window, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
            ?? throw new Xunit.Sdk.XunitException("the handle lays out inside the window");
}
