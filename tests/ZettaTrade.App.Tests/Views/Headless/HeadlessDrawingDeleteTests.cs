namespace ZettaTrade.App.Tests.Views.Headless;

using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using FluentAssertions;
using Xunit;
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.UI.Charting;

/// <summary>
/// The headless delete-affordance facts (T-178): the REAL main window's
/// chart header carries the visible delete button and its gate is honest
/// — disabled while nothing stands selected, armed the moment a drawing
/// is, a real pointer press removes the selected drawing through the
/// same route the chart's Delete key names (both ride
/// <c>ChartViewModel.DeleteDrawingAsync</c> — the layer saves and the
/// selection clears), and the affordance stands down again with the
/// cleared selection. Every ViewModel route rides the serialized
/// dispatcher (the production routes do — the control's event mirrors
/// hand the routes to the window's subscriptions on the UI thread), so
/// the window's live bindings never see an off-thread property change.
/// The class rides the LocalizationManagerSingleton collection: every
/// fact materializes the REAL main window whose chart widgets subscribe
/// the localization and theme singletons on attach, so the
/// manager-creating classes run strictly sequentially with it.
/// </summary>
[Collection("LocalizationManagerSingleton")]
public sealed class HeadlessDrawingDeleteTests
{
    static HeadlessDrawingDeleteTests() => AvaloniaDispatcherPump.EnsureInstalled();

    /// <summary>Base instant the committed placements anchor through (UTC, offset zero).</summary>
    private static readonly DateTimeOffset Base = new(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TheAffordance_GatesOnTheSelection()
    {
        var world = await MainWorld.CreateAsync();
        var window = await world.ShowWindowAsync();
        try
        {
            var button = await HeadlessWorld.OnUi(() =>
                HeadlessWorld.FindDescendant<Button>(window, "DeleteDrawingButton"));
            button.Should().NotBeNull("the affordance rides the chart header's tool strip");

            // The gate without a selection: the command's own stream is the
            // honest read — the button's enabled state rides it through the
            // command binding.
            (await world.Main.Chart.DeleteSelectedDrawingCommand.CanExecute.FirstAsync())
                .Should().BeFalse("nothing stands selected — the affordance stays down");
            await HeadlessWorld.OnUi(() =>
                button!.IsEffectivelyEnabled.Should().BeFalse("the command binding mirrors the closed gate"));

            var drawing = await ASelectedDrawingAsync(world);
            drawing.Should().NotBeNull("the committed drawing is the selection the gate reads");

            (await world.Main.Chart.DeleteSelectedDrawingCommand.CanExecute.FirstAsync())
                .Should().BeTrue("a drawing now stands selected — the affordance arms");
            await HeadlessWorld.OnUi(() =>
                button!.IsEffectivelyEnabled.Should().BeTrue("the command binding mirrors the open gate"));
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    [Fact]
    public async Task PressingTheAffordance_RemovesTheSelectedDrawing_AndStandsDown()
    {
        var world = await MainWorld.CreateAsync();
        var window = await world.ShowWindowAsync();
        try
        {
            var button = await HeadlessWorld.OnUi(() =>
                HeadlessWorld.FindDescendant<Button>(window, "DeleteDrawingButton"));
            button.Should().NotBeNull("the affordance rides the chart header's tool strip");

            var drawing = await ASelectedDrawingAsync(world);
            drawing.Should().NotBeNull("the committed drawing is the selection the press removes");

            // The real press: pointer down and up over the button's center
            // — the same input path a user's mouse feeds.
            await HeadlessWorld.OnUi(() =>
            {
                var origin = CenterOf(window, button!);
                window.MouseDown(origin, MouseButton.Left);
                window.MouseUp(origin, MouseButton.Left);
            });
            await AvaloniaDispatcherPump.DrainAsync();

            world.Main.Chart.Drawings.Should().BeEmpty(
                "the press names the removal — the selected drawing falls off the layer");
            world.Main.Chart.SelectedDrawingId.Should().BeNull(
                "the removal cleared its own selection");
            (await world.Main.Chart.DeleteSelectedDrawingCommand.CanExecute.FirstAsync())
                .Should().BeFalse("the affordance stands down with the cleared selection");
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    [Fact]
    public async Task TheDeleteKey_RidesTheSameRemovalRoute()
    {
        var world = await MainWorld.CreateAsync();
        var window = await world.ShowWindowAsync();
        try
        {
            var control = await HeadlessWorld.OnUi(() =>
                HeadlessWorld.Descendants<CandlestickChart>(window).Single());
            var drawing = await ASelectedDrawingAsync(world);
            drawing.Should().NotBeNull("the committed drawing is the selection the key removes");

            // The T-091 mirror first: the VM's selection rode the one-way
            // binding into the control, so the key's route names the same
            // drawing. Then the Delete key — the affordance's tooltip
            // carries it, and this fact pins it rides the SAME route the
            // press rides: the control names the removal, the MainWindow's
            // mirror hands it to the ViewModel's DeleteDrawingAsync.
            await HeadlessWorld.OnUi(() =>
            {
                control.SelectedDrawingId.Should().Be(
                    drawing!.Id, "the selection mirrors into the control");
                control.Focus();
                control.RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.Delete,
                    KeyModifiers = KeyModifiers.None,
                });
            });
            await AvaloniaDispatcherPump.DrainAsync();

            world.Main.Chart.Drawings.Should().BeEmpty(
                "the key's removal and the affordance's press land in the same VM route");
            world.Main.Chart.SelectedDrawingId.Should().BeNull(
                "the key's route cleared its own selection likewise");
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    /// <summary>
    /// Load a page (the standing symbol's empty series is enough — the
    /// drawings layer's key is the triple, the bars never enter the
    /// removal's route), commit a placement through the VM's route and
    /// select it: the affordance's honest armed state. The committed
    /// drawing comes back with the store's echo answer as the standing
    /// layer, its identity settled. Every route rides the UI thread.
    /// </summary>
    private static async Task<ChartDrawing?> ASelectedDrawingAsync(MainWorld world)
    {
        var chart = world.Main.Chart;
        await HeadlessWorld.OnUi(() => chart.SelectedSymbol = Symbol.Parse("BTC/USDT"));
        await AvaloniaDispatcherPump.DrainAsync();

        // Placement-free geometry the commit's validate accepts: two
        // distinct anchors (the placement lane's own minimum).
        var anchors = new[]
        {
            new ChartDrawingAnchor(Base, 100m),
            new ChartDrawingAnchor(Base.AddHours(2), 120m),
        };
        await OnTheDispatcherAsync(async () =>
        {
            await chart.CommitDrawingAsync(new ChartDrawingEventArgs(
                null, ChartDrawingKind.TrendLine, anchors));
            return true;
        });
        await AvaloniaDispatcherPump.DrainAsync();

        if (chart.Drawings.Count is not 1)
        {
            return null;
        }

        var drawing = chart.Drawings.Single();
        await HeadlessWorld.OnUi(() => chart.SelectDrawing(drawing.Id));
        await AvaloniaDispatcherPump.DrainAsync();

        return drawing;
    }

    /// <summary>
    /// Run one async ViewModel route on the UI thread and land its whole
    /// body — including the await continuations — on the serialized
    /// dispatcher: the route's property changes raise on the thread the
    /// window's live bindings were created on, exactly the production
    /// shape. The drain settles the chain before the result surfaces.
    /// </summary>
    private static async Task<T> OnTheDispatcherAsync<T>(Func<Task<T>> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                done.SetResult(await work());
            }
            catch (Exception fault)
            {
                done.SetException(fault);
            }
        });

        await AvaloniaDispatcherPump.DrainAsync();

        return await done.Task;
    }

    private static Point CenterOf(Window window, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
            ?? throw new Xunit.Sdk.XunitException("the affordance lays out inside the window");
}
