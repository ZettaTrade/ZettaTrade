namespace ZettaTrade.App.Tests.Views.Headless;

using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.Views;

/// <summary>The half-day-agnostic status byte ReactiveUI.Primitives names RxVoid.</summary>
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// The headless overlay routing facts (T-144): the REAL main window drives
/// its keyboard routes against the full ViewModel world — Ctrl+K opens and
/// closes the palette through the window's global hotkey route, the open
/// palette stands down every other bound gesture, the palette's overlay
/// owns its navigation keys (Esc closes it) and the cheat sheet's Esc
/// rides first when it shows. The keys raise on the routed-event source
/// the real keyboard would feed: the window for the global route, the
/// overlay element for the palette's own route.
/// </summary>
[Collection("AvaloniaDispatcherSerial")]
public sealed class HeadlessOverlayRoutingTests
{
    static HeadlessOverlayRoutingTests() => AvaloniaDispatcherPump.EnsureInstalled();

    [Fact]
    public async Task CtrlK_TogglesThePaletteOverlay_ThroughTheWindowRoute()
    {
        var world = await MainWorld.CreateAsync();
        var window = await world.ShowWindowAsync();
        try
        {
            world.Main.Palette.IsOpen.Should().BeFalse("a fresh workspace stands the palette down");

            // The presses ride the user-interface thread: the route's
            // handlers run there.
            await HeadlessWorld.OnUi(() => Press(window, Key.K, KeyModifiers.Control));
            await WaitUntilIsOpenAsync(world, expected: true, "Ctrl+K routes the palette's toggle open");

            await HeadlessWorld.OnUi(() => Press(window, Key.K, KeyModifiers.Control));
            await WaitUntilIsOpenAsync(world, expected: false, "Ctrl+K again rides the toggle's close side");
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    [Fact]
    public async Task TheOpenPalette_StandDownsEveryOtherBoundGesture()
    {
        var world = await MainWorld.CreateAsync();
        var window = await world.ShowWindowAsync();
        try
        {
            // The settings-open tally: the route the menu interaction
            // serves — registered before any key presses, so a fired
            // route increments it and a stood-down one never does.
            var settingsOpens = 0;
            world.Main.Menu.OpenSettingsRequested.RegisterHandler(context =>
            {
                settingsOpens++;
                context.SetOutput(RxVoid.Default);
            });

            await HeadlessWorld.OnUi(() => Press(window, Key.K, KeyModifiers.Control));
            await WaitUntilIsOpenAsync(world, expected: true, "the palette opens first");

            // The settings gesture: bound, but the palette owns the
            // keyboard until it closes — the route stands down.
            await HeadlessWorld.OnUi(() => Press(window, Key.OemComma, KeyModifiers.Control));
            await Task.Delay(50);
            await AvaloniaDispatcherPump.DrainAsync();

            world.Main.Palette.IsOpen.Should().BeTrue("the open palette keeps the keyboard's ownership");
            settingsOpens.Should().Be(0, "the stood-down settings gesture never reaches its route");

            await HeadlessWorld.OnUi(() => Press(window, Key.K, KeyModifiers.Control));
            await WaitUntilIsOpenAsync(world, expected: false, "the palette's own gesture still routes");
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    [Fact]
    public async Task ThePaletteOverlay_OwnsItsNavigationKeys_EscapeClosesIt()
    {
        var world = await MainWorld.CreateAsync();
        var window = await world.ShowWindowAsync();
        try
        {
            await HeadlessWorld.OnUi(() => Press(window, Key.K, KeyModifiers.Control));
            await WaitUntilIsOpenAsync(world, expected: true, "the palette opens first");

            // The overlay's own route: Esc marks handled and closes the
            // palette — the global route never sees the key. The overlay
            // lookup rides the user-interface lane too (the overlay is the
            // window's named Grid, not a Border).
            var overlay = await HeadlessWorld.OnUi(() =>
                HeadlessWorld.FindDescendant<Grid>(window, "PaletteOverlay"));
            overlay.Should().NotBeNull("the palette overlay hosts the route's source");
            await HeadlessWorld.OnUi(() => Press(overlay!, Key.Escape, KeyModifiers.None));
            await WaitUntilIsOpenAsync(world, expected: false, "the overlay's Esc closes the palette");
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    [Fact]
    public async Task Esc_ClosesTheCheatSheet_WhenItShows()
    {
        var world = await MainWorld.CreateAsync();
        var window = await world.ShowWindowAsync();
        try
        {
            // The overlay-command dispatch discipline (the window's own
            // DispatchOverlayCommand shape): subscribe, then spin until
            // the marshaled write lands — never a bare await.
            world.Main.Hotkeys.ToggleSheetCommand.Execute(RxVoid.Default).Subscribe(_ => { });
            await WaitUntilAsync(
                () => world.Main.Hotkeys.IsSheetVisible,
                "the cheat sheet toggle shows the sheet");

            await HeadlessWorld.OnUi(() => Press(window, Key.Escape, KeyModifiers.None));
            await WaitUntilAsync(
                () => !world.Main.Hotkeys.IsSheetVisible,
                "Esc closes the sheet first when it shows");

            world.Main.Palette.IsOpen.Should().BeFalse("the sheet's Esc never touches the palette");
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    /// <summary>Feed the window one key-down the routed-event way.</summary>
    private static void Press(Interactive source, Key key, KeyModifiers modifiers) =>
        source.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers,
        });

    private static Task WaitUntilIsOpenAsync(MainWorld world, bool expected, string because) =>
        WaitUntilAsync(() => world.Main.Palette.IsOpen == expected, because);

    /// <summary>
    /// The bounded settle: the routed commands run on the worker threads
    /// and their marshaled writes land through the pump, so the fact spins
    /// a bounded wait — a parked route must never hang the world.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var budget = TimeSpan.FromSeconds(5);
        var started = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - started > budget)
            {
                throw new Xunit.Sdk.XunitException($"The routed state never settled: {because}");
            }

            await Task.Delay(20);
            await AvaloniaDispatcherPump.DrainAsync();
        }
    }
}
