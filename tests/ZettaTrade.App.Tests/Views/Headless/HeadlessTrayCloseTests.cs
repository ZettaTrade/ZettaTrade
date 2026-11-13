namespace ZettaTrade.App.Tests.Views.Headless;

using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.App.Views;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.SingleInstance;

/// <summary>
/// The tray close surface's headless pins (T-214): the MinimizeToTray mode
/// turns a plain user close into a hide (the session keeps running), every
/// armed exit closes exactly as shipped, the off state changes nothing,
/// the tray menu's toggles write their honest settings keys, and the
/// single-instance hand-off stays intact with the window hidden. The
/// headless world has no desktop lifetime, so closing windows here never
/// shuts anything down — the close-policy pins are safe by construction.
/// </summary>
[Collection("LocalizationManagerSingleton")]
public sealed class HeadlessTrayCloseTests
{
    static HeadlessTrayCloseTests() => AvaloniaDispatcherPump.EnsureInstalled();

    [Fact]
    public async Task MinimizeToTray_TheCloseHidesAndTheArmedExitCloses()
    {
        var world = await MainWorld.CreateAsync();
        var policy = await CreatePolicyAsync(world, TrayCloseMode.MinimizeToTray);
        var (window, closed) = await CreateWindowAsync(world, policy);

        await HeadlessWorld.OnUi(() => window.Close());

        var afterClose = await HeadlessWorld.OnUi(() => (Closed: closed[0], Visible: window.IsVisible));
        afterClose.Closed.Should().BeFalse("the interception cancels the plain user close in the tray mode");
        afterClose.Visible.Should().BeFalse("the close became a hide - the session keeps running");

        policy.ArmExit();
        await HeadlessWorld.OnUi(() => window.Close());

        var afterExit = await HeadlessWorld.OnUi(() => closed[0]);
        afterExit.Should().BeTrue("an armed exit - the tray's or the menu's - always closes");
    }

    [Fact]
    public async Task TheExitMode_TheCloseClosesExactlyAsBefore()
    {
        var world = await MainWorld.CreateAsync();
        var policy = await CreatePolicyAsync(world, null);
        var (window, closed) = await CreateWindowAsync(world, policy);

        await HeadlessWorld.OnUi(() => window.Close());

        var afterClose = await HeadlessWorld.OnUi(() => closed[0]);
        afterClose.Should().BeTrue("the off state changes nothing - the shipped close semantics stand");
    }

    [Fact]
    public async Task TheTrayDndToggle_WritesTheFlippedKey()
    {
        var world = await MainWorld.CreateAsync();
        world.Settings
            .GetAsync<bool>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(false);
        var tray = CreateTray(world, await CreatePolicyAsync(world, null));

        await tray.ToggleDndAsync();

        await world.Settings.Received(1).SetAsync(
            NotificationSettingsKeys.Section,
            NotificationSettingsKeys.DoNotDisturb,
            true);

        await tray.ToggleDndAsync();

        // The toggle is one honest write per flip - the standing truth
        // flips every time.
        await world.Settings.Received(1).SetAsync(
            NotificationSettingsKeys.Section,
            NotificationSettingsKeys.DoNotDisturb,
            false);
    }

    [Fact]
    public async Task TheTrayCloseToTrayToggle_WritesTheModeAndThePolicyReapplies()
    {
        var world = await MainWorld.CreateAsync();
        var policy = await CreatePolicyAsync(world, null);
        var tray = CreateTray(world, policy);

        await tray.ToggleCloseToTrayAsync();

        policy.Mode.Should().Be(TrayCloseMode.MinimizeToTray,
            "the write rides the policy's own change stream - the mode re-applies live");
        await world.Settings.Received(1).SetAsync(
            AppSettingsKeys.Section,
            AppSettingsKeys.TrayCloseBehavior,
            TrayCloseMode.MinimizeToTray.ToString());

        await tray.ToggleCloseToTrayAsync();

        policy.Mode.Should().Be(TrayCloseMode.Exit);
        await world.Settings.Received(1).SetAsync(
            AppSettingsKeys.Section,
            AppSettingsKeys.TrayCloseBehavior,
            TrayCloseMode.Exit.ToString());
    }

    [Fact]
    public async Task TheHiddenMainWindow_TheHandoffStillLandsTheSelection()
    {
        var world = await MainWorld.CreateAsync();
        var policy = await CreatePolicyAsync(world, TrayCloseMode.MinimizeToTray);
        var (window, closed) = await CreateWindowAsync(world, policy);

        await HeadlessWorld.OnUi(() => window.Close());
        var hidden = await HeadlessWorld.OnUi(() => window.IsVisible);
        hidden.Should().BeFalse();

        var handoff = new HandoffApplyService(
            world.Exchanges,
            world.Main.Chart,
            world.Settings,
            NullLogger<HandoffApplyService>.Instance);
        var apply = handoff.ApplyAsync(["--symbol", "BTC/USDT"]);

        // The apply's symbol write marshals through the Avalonia dispatcher,
        // whose jobs only run on the pump's drains: pump until the receipt
        // settles (one drain lands the write; the cap only bounds a hang).
        var pumps = 0;
        while (!apply.IsCompleted && pumps < 10)
        {
            await AvaloniaDispatcherPump.DrainAsync();
            pumps++;
        }

        // The completion continuation rides the thread pool asynchronously,
        // so an immediate IsCompleted probe races it - settle the receipt
        // with a bounded wait instead.
        var receipt = await apply.WaitAsync(TimeSpan.FromSeconds(30));

        var stillHidden = await HeadlessWorld.OnUi(() => window.IsVisible);
        stillHidden.Should().BeFalse(
            "the close-to-tray session runs with the window hidden - the hand-off lands through it");
        receipt.Applied.Should().BeTrue(
            "the close-to-tray session keeps the dispatch pipe alive - the forwarded selection lands");
        world.Main.Chart.SelectedSymbol?.Normalized.Should().Be("BTC/USDT");

        policy.ArmExit();
        await HeadlessWorld.OnUi(() => window.Close());
        var afterExit = await HeadlessWorld.OnUi(() => closed[0]);
        afterExit.Should().BeTrue();
    }

    private static async Task<TrayClosePolicy> CreatePolicyAsync(
        MainWorld world,
        TrayCloseMode? stored)
    {
        if (stored is { } mode)
        {
            world.Settings
                .GetAsync<string>(
                    AppSettingsKeys.Section,
                    AppSettingsKeys.TrayCloseBehavior,
                    Arg.Any<string>())
                .Returns(mode.ToString());
        }

        var policy = new TrayClosePolicy(world.Settings, NullLogger<TrayClosePolicy>.Instance);
        await policy.InitializeAsync();

        return policy;
    }

    private static async Task<(MainWindow Window, bool[] Closed)> CreateWindowAsync(
        MainWorld world,
        TrayClosePolicy policy)
    {
        var window = await HeadlessWorld.OnUi(() => new MainWindow(world.Main, policy));
        // The holder rides by reference: a tuple would copy the flag before
        // the close ever fires and the fact would read a stale false.
        var closed = new[] { false };
        window.Closed += (_, _) => closed[0] = true;
        await HeadlessWorld.ShowAndSettleAsync(window);

        return (window, closed);
    }

    private static TrayIconService CreateTray(MainWorld world, TrayClosePolicy policy)
    {
        var center = new NotificationCenter(world.Settings, NullLogger<NotificationCenter>.Instance);
        var tray = new TrayIconService(
            world.Settings,
            center,
            policy,
            world.Localization,
            NullLogger<TrayIconService>.Instance);

        // The close-to-tray toggle's write must reach the policy through
        // the same stream the app's real settings publish: the substitute
        // mirrors every write (the string mode writes and the bool
        // Do-Not-Disturb writes alike) into the world's change subject.
        world.Settings
            .When(substitute => substitute.SetAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>()))
            .Do(invocation => world.Changes.OnNext(new SettingsChange(
                invocation.ArgAt<string>(0),
                invocation.ArgAt<string>(1),
                null,
                invocation.ArgAt<string>(2))));

        world.Settings
            .When(substitute => substitute.SetAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<bool>()))
            .Do(invocation => world.Changes.OnNext(new SettingsChange(
                invocation.ArgAt<string>(0),
                invocation.ArgAt<string>(1),
                null,
                invocation.ArgAt<bool>(2))));

        return tray;
    }
}
