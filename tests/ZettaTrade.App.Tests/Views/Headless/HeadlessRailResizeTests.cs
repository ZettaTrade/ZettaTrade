namespace ZettaTrade.App.Tests.Views.Headless;

using System;
using System.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using FluentAssertions;
using Livery.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.Controls;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Strategies;

/// <summary>
/// The headless rail facts (T-177): the REAL main window's rail boundary
/// rides pointer press/move/release scripts — the same input path a
/// user's mouse feeds — and the boundary must move: the drag follows the
/// pointer (a downward drag grows the row above, so the rail grows when
/// the boundary moves up toward the chart), the row's min/max clamps hold,
/// the chart row keeps its star, the TwoWay row binding carries the drag
/// into the persisted height, a settled drag persists through the
/// settings pipeline, the focused handle steps with the arrow keys and
/// the grip shows. The rows surface scrolls its overflow: readings
/// outgrowing the rail stay reachable through the scroller. The class
/// rides the LocalizationManagerSingleton collection: every fact
/// materializes the REAL main window whose chart widgets subscribe the
/// localization and theme singletons on attach, so the manager-creating
/// classes run strictly sequentially with it.
/// </summary>
[Collection("LocalizationManagerSingleton")]
public sealed class HeadlessRailResizeTests
{
    static HeadlessRailResizeTests() => AvaloniaDispatcherPump.EnsureInstalled();

    /// <summary>Base instant the crafted signals step through (UTC, offset zero).</summary>
    private static readonly DateTimeOffset Base = new(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DraggingTheRailHandleUp_GrowsTheRailRow()
    {
        var world = await MainWorld.CreateAsync();
        var window = await world.ShowWindowAsync();
        try
        {
            var handle = await RailHandleAsync(window);

            // The standard direction, row-wise: an upward drag moves the
            // boundary up — the rail below it grows.
            await DragAsync(window, handle, delta: -40);

            await HeadlessWorld.OnUi(() =>
            {
                RailRow(handle).Height.Value.Should().Be(212, "the upward drag gives the rail 40px");
                world.Main.Chart.SignalRail.RailHeight.Value.Should().Be(212,
                    "the TwoWay binding carries the drag into the ViewModel");
                RailRow(handle).Height.IsAbsolute.Should().BeTrue("the rail row is an absolute row");
            });
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    [Fact]
    public async Task DraggingTheRailHandleDown_ShrinksTheRailRow_AndTheChartKeepsItsStar()
    {
        var world = await MainWorld.CreateAsync();
        var window = await world.ShowWindowAsync();
        try
        {
            var handle = await RailHandleAsync(window);

            await DragAsync(window, handle, delta: 40);

            await HeadlessWorld.OnUi(() =>
            {
                RailRow(handle).Height.Value.Should().Be(132, "the downward drag takes 40px off the rail");
                world.Main.Chart.SignalRail.RailHeight.Value.Should().Be(132,
                    "the TwoWay binding carries the drag into the ViewModel");
                ((Grid)handle.Parent!).RowDefinitions[3].Height.IsStar.Should().BeTrue(
                    "the chart row keeps absorbing — the rail grows within its band, never eats the chart");
            });
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    [Fact]
    public async Task DraggingPastTheMinimum_ClampsAtTheRailFloor()
    {
        var world = await MainWorld.CreateAsync();
        var window = await world.ShowWindowAsync();
        try
        {
            var handle = await RailHandleAsync(window);

            // A downward sweep of 200 would take the rail below its
            // 72px minimum: the row's own clamp stops the boundary there.
            await DragAsync(window, handle, delta: 200);

            await HeadlessWorld.OnUi(() =>
            {
                RailRow(handle).Height.Value.Should().Be(UiSettingsKeys.MinSignalRailHeight,
                    "the rail clamps at its minimum");
                world.Main.Chart.SignalRail.RailHeight.Value.Should().Be(UiSettingsKeys.MinSignalRailHeight,
                    "the TwoWay binding carries the clamped drag into the ViewModel");
            });
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    [Fact]
    public async Task DraggingPastTheMaximum_ClampsAtTheRailCeiling()
    {
        var world = await MainWorld.CreateAsync();
        var window = await world.ShowWindowAsync();
        try
        {
            var handle = await RailHandleAsync(window);

            // An upward sweep of 400 would grow the rail past its
            // 480px ceiling: the row's own clamp stops the boundary there.
            await DragAsync(window, handle, delta: -400);

            await HeadlessWorld.OnUi(() =>
            {
                RailRow(handle).Height.Value.Should().Be(UiSettingsKeys.MaxSignalRailHeight,
                    "the rail clamps at its ceiling");
                world.Main.Chart.SignalRail.RailHeight.Value.Should().Be(UiSettingsKeys.MaxSignalRailHeight,
                    "the TwoWay binding carries the clamped drag into the ViewModel");
            });
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    [Fact]
    public async Task ASettledRailDrag_PersistsThroughTheSettingsPipeline()
    {
        var world = await MainWorld.CreateAsync();
        var window = await world.ShowWindowAsync();
        try
        {
            var handle = await RailHandleAsync(window);

            await DragAsync(window, handle, delta: -40);

            // The drag debounce (250 ms) runs on real time; settle past it
            // and drain the pipeline's async write.
            await Task.Delay(400);
            await AvaloniaDispatcherPump.DrainAsync();

            await world.Settings.Received(1).SetAsync(
                UiSettingsKeys.Section,
                UiSettingsKeys.SignalRailHeight,
                212d);
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    [Fact]
    public async Task TheFocusedHandle_StepsTheRowWithArrowKeys()
    {
        var world = await MainWorld.CreateAsync();
        var window = await world.ShowWindowAsync();
        try
        {
            var handle = await RailHandleAsync(window);

            await HeadlessWorld.OnUi(() =>
            {
                handle.Focus();
                handle.RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.Up,
                    KeyModifiers = KeyModifiers.None,
                });
            });
            await AvaloniaDispatcherPump.DrainAsync();

            await HeadlessWorld.OnUi(() =>
            {
                RailRow(handle).Height.Value.Should().Be(182, "the Up key grows the rail by the keyboard step");
                world.Main.Chart.SignalRail.RailHeight.Value.Should().Be(182,
                    "the TwoWay binding carries the keyboard step too");
            });
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    [Fact]
    public async Task TheRailHandleShowsItsGrip_AndHighlightsOnHover()
    {
        var world = await MainWorld.CreateAsync();
        var window = await world.ShowWindowAsync();
        try
        {
            var handle = await RailHandleAsync(window);

            await HeadlessWorld.OnUi(() =>
            {
                var grip = HeadlessWorld.Descendants<Border>(handle)
                    .First(border => border.Name == "GripLine");
                grip.Bounds.Height.Should().Be(1, "the grip reads as a quiet hairline at rest");
            });

            // Sweep the pointer onto the handle: the grip widens and the
            // pointerover state resolves through the template.
            await HeadlessWorld.OnUi(() => window.MouseMove(CenterOf(window, handle)));
            await AvaloniaDispatcherPump.DrainAsync();

            await HeadlessWorld.OnUi(() =>
            {
                handle.IsPointerOver.Should().BeTrue("the pointer rides the handle");
                var grip = HeadlessWorld.Descendants<Border>(handle)
                    .First(border => border.Name == "GripLine");
                grip.Bounds.Height.Should().Be(3, "the hover state widens the grip");
            });
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    [Fact]
    public async Task TheRailRowsSurface_ScrollsItsOverflow()
    {
        var probe = await CreateProbeAsync();
        var window = await probe.World.ShowWindowAsync();
        try
        {
            var rail = probe.World.Main.Chart.SignalRail;

            // One coalesced burst: 14 readings land as ONE rows
            // replacement, the ring bounded at its 12-row capacity.
            for (var hour = 0; hour < 14; hour++)
            {
                probe.Signals.OnNext(Long("demo", hour));
            }

            await Task.Delay(SignalRailViewModel.RailCoalesceWindow + TimeSpan.FromMilliseconds(200));
            await AvaloniaDispatcherPump.DrainAsync();

            // A small rail guarantees the rows outgrow it.
            await HeadlessWorld.OnUi(() =>
            {
                rail.Rows.Should().HaveCount(SignalRailViewModel.MaxRows);
                rail.RailHeight = new GridLength(90);
            });
            await AvaloniaDispatcherPump.DrainAsync();

            var scroller = await HeadlessWorld.OnUi(() =>
                HeadlessWorld.FindDescendant<ScrollViewer>(window, "RailRowsScroller"));
            scroller.Should().NotBeNull("the rows surface rides a scroller");

            await HeadlessWorld.OnUi(() =>
            {
                scroller!.Extent.Height.Should().BeGreaterThan(
                    scroller.Viewport.Height + 1,
                    "twelve readings outgrow a 90px rail — the overflow exists");
                scroller.Offset = new Point(0, scroller.Extent.Height);
            });
            await AvaloniaDispatcherPump.DrainAsync();

            await HeadlessWorld.OnUi(() =>
            {
                scroller!.Offset.Y.Should().BeGreaterThan(0, "the overflow is reachable");
                var oldest = HeadlessWorld.Descendants<Button>(scroller!).Last();
                var position = oldest.TranslatePoint(new Point(0, 0), scroller!);
                position.Should().NotBeNull("the row lays out inside the scroller");
                position!.Value.Y.Should().BeLessThan(
                    scroller.Bounds.Height,
                    "the ring's tail scrolled into view — the oldest reading stays reachable");
            });
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    /// <summary>Reach the rail's handle: the window's only row handle.</summary>
    private static async Task<RowResizeHandle> RailHandleAsync(Window window) =>
        await HeadlessWorld.OnUi(() =>
            HeadlessWorld.Descendants<RowResizeHandle>(window).Single());

    /// <summary>The rail's persisted-height row: the one below the handle's track.</summary>
    private static RowDefinition RailRow(RowResizeHandle handle) =>
        ((Grid)handle.Parent!).RowDefinitions[5];

    /// <summary>Press the handle's center, sweep by the vertical delta, release.</summary>
    private static async Task DragAsync(Window window, RowResizeHandle handle, double delta)
    {
        await HeadlessWorld.OnUi(() =>
        {
            var origin = CenterOf(window, handle);
            var target = origin + new Vector(0, delta);

            window.MouseDown(origin, MouseButton.Left);
            window.MouseMove(origin + new Vector(0, delta / 2));
            window.MouseMove(target);
            window.MouseUp(target, MouseButton.Left);
        });
        await AvaloniaDispatcherPump.DrainAsync();
    }

    private static Point CenterOf(Window window, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
            ?? throw new Xunit.Sdk.XunitException("the handle lays out inside the window");

    /// <summary>
    /// One long signal at <paramref name="hour"/> (per-id distinct candles, close 10 + hour).
    /// </summary>
    private static StrategySignal Long(string id, int hour) =>
        new(id, Symbol.Parse("BTC/USDT"), TimeFrame.H1,
            new Bar(Base.AddHours(hour), 9m, 12m, 8m, 10m + hour + 1m, 50m),
            StrategyStance.Long);

    /// <summary>
    /// The overflow probe's world: the real main window rides a
    /// subject-fed rail the fact drives directly (the world's own recipe
    /// otherwise stands — the streamed rail's rows come from the subject,
    /// never a strategy load).
    /// </summary>
    private static async Task<RailProbe> CreateProbeAsync()
    {
        var signals = new Subject<StrategySignal>();
        var rail = new SignalRailViewModel(
            signals,
            Substitute.For<ILocalizationManager>(),
            Substitute.For<ISettingsService>(),
            NullLogger<SignalRailViewModel>.Instance);
        var world = await MainWorld.CreateAsync(rail);
        return new RailProbe(world, signals);
    }

    /// <summary>The overflow probe: the world plus its rail's signal source.</summary>
    private sealed record RailProbe(MainWorld World, Subject<StrategySignal> Signals);
}
