namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using NSubstitute;
using ReactiveUI.Primitives;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The performance HUD ViewModel's flows (T-108): the shipped-off default
/// holds zero traces (the sampler never starts), the persisted restore
/// lands through <c>InitializeAsync</c> and starts/stops the sampler per
/// the applied visibility, the menu toggle persists its flips (the
/// cache-leads write discipline), an external writer applies live without
/// being rewritten, and one snapshot re-formats the overlay's lines with
/// the localized labels and the pure gauge math's readings.
/// </summary>
[Collection("AvaloniaDispatcherSerial")]
public sealed class PerformanceHudViewModelTests
{
    /// <summary>
    /// The shipped default: off — the sampler holds no book subscription,
    /// and the restore of an absent choice keeps it that way.
    /// </summary>
    [Fact]
    public async Task TheShippedDefault_IsOff_WithZeroTraces()
    {
        var kit = HudTests.CreateKit();

        kit.Hud.IsVisible.Should().BeFalse("the HUD ships off");
        kit.Service.IsRunning.Should().BeFalse("the off state arms no sampler");
        kit.BookUpdates.HasObservers.Should().BeFalse("the off state holds no subscription");

        await kit.Hud.InitializeAsync();
        await AvaloniaDispatcherPump.DrainAsync();

        kit.Hud.IsVisible.Should().BeFalse("no persisted choice restores to off");
        kit.Service.IsRunning.Should().BeFalse();
        kit.BookUpdates.HasObservers.Should().BeFalse("the restored-off state leaves zero traces");
    }

    /// <summary>The persisted-on choice restores through the startup walk and starts the sampler.</summary>
    [Fact]
    public async Task ThePersistedOnChoice_Restores_AndStartsTheSampler()
    {
        var kit = HudTests.CreateKit(persistedVisibility: true);

        await kit.Hud.InitializeAsync();
        await AvaloniaDispatcherPump.DrainAsync();

        kit.Hud.IsVisible.Should().BeTrue("the persisted choice restored");
        kit.Service.IsRunning.Should().BeTrue("the visibility pipeline started the sampler");
        kit.BookUpdates.HasObservers.Should().BeTrue("the running sampler taps the book stream");
    }

    /// <summary>The menu's flip persists immediately (the settings write carries the issued value).</summary>
    [Fact]
    public async Task TheToggle_FlipsVisibility_AndPersistsTheChoice()
    {
        var kit = HudTests.CreateKit();
        await kit.Hud.InitializeAsync();
        await AvaloniaDispatcherPump.DrainAsync();

        await kit.Hud.ToggleCommand.Execute(RxVoid.Default);
        await AvaloniaDispatcherPump.DrainAsync();

        kit.Hud.IsVisible.Should().BeTrue("the toggle flipped the overlay on");
        kit.Service.IsRunning.Should().BeTrue();
        await kit.Settings.Received(1).SetAsync(
            UiSettingsKeys.DiagnosticsSection,
            UiSettingsKeys.HudEnabled,
            true);

        await kit.Hud.ToggleCommand.Execute(RxVoid.Default);
        await AvaloniaDispatcherPump.DrainAsync();

        kit.Hud.IsVisible.Should().BeFalse("the second flip turned the overlay off");
        kit.Service.IsRunning.Should().BeFalse("the visibility pipeline stopped the sampler");
        kit.BookUpdates.HasObservers.Should().BeFalse("the stopped sampler left zero traces");
        await kit.Settings.Received(1).SetAsync(
            UiSettingsKeys.DiagnosticsSection,
            UiSettingsKeys.HudEnabled,
            false);
    }

    /// <summary>An external writer applies live — and the cache-leads filter never rewrites the value it applied.</summary>
    [Fact]
    public async Task TheChangesStream_AppliesAnExternalFlip_WithoutRewritingIt()
    {
        var kit = HudTests.CreateKit();
        await kit.Hud.InitializeAsync();
        await AvaloniaDispatcherPump.DrainAsync();

        kit.Changes.OnNext(new SettingsChange(
            UiSettingsKeys.DiagnosticsSection,
            UiSettingsKeys.HudEnabled,
            false,
            true));
        await AvaloniaDispatcherPump.DrainAsync();

        kit.Hud.IsVisible.Should().BeTrue("the external choice applied live");
        kit.Service.IsRunning.Should().BeTrue();
        await kit.Settings.DidNotReceive().SetAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<bool>());
    }

    /// <summary>A non-bool value on the HUD's key drops — the applied choice stays in charge.</summary>
    [Fact]
    public async Task TheChangesStream_DropsNonBoolValues()
    {
        var kit = HudTests.CreateKit();
        await kit.Hud.InitializeAsync();
        await AvaloniaDispatcherPump.DrainAsync();

        kit.Changes.OnNext(new SettingsChange(
            UiSettingsKeys.DiagnosticsSection,
            UiSettingsKeys.HudEnabled,
            false,
            "on"));
        await AvaloniaDispatcherPump.DrainAsync();

        kit.Hud.IsVisible.Should().BeFalse("a string is not a visibility choice");
        kit.Service.IsRunning.Should().BeFalse();
    }

    /// <summary>
    /// One snapshot re-formats the lines: the localized labels around the
    /// drained readings — the chart fps from the attached frame tap, the
    /// book rate and its latency histogram (a future-stamped delivery
    /// clamps into the fastest bucket, two stale ones overflow), the
    /// memory figure, the backpressure gauge (0 of 8192 reads as an empty
    /// bird bar) — and the drained counters reset for the next tick.
    /// </summary>
    [Fact]
    public async Task ASnapshot_ReformatsTheLines_WithTheLocalizedReadouts()
    {
        var kit = HudTests.CreateKit(backpressureBacklog: 0);
        var frames = 0;
        kit.Hud.AttachChartFrames(() => frames);
        await kit.Hud.InitializeAsync();
        await AvaloniaDispatcherPump.DrainAsync();

        // The overlay shows: the visibility pipeline arms the snapshots leg
        // (the direct assignment — the toggle's command path has its own
        // fact).
        kit.Hud.IsVisible = true;
        await AvaloniaDispatcherPump.DrainAsync();

        frames = 60; // the drain reads 60 over the 1 s window — a steady 60 fps
        kit.BookUpdates.OnNext(HudTests.FutureBook());
        kit.BookUpdates.OnNext(HudTests.StaleBook());
        kit.BookUpdates.OnNext(HudTests.StaleBook());
        kit.Service.EmitSample();
        await AvaloniaDispatcherPump.DrainAsync();

        kit.Hud.Lines.Should().HaveCount(5, "chart, book, memory, queue, latency — one line each");
        kit.Hud.Lines[0].Should().Be("[Diagnostics.Hud.Chart:60.0]");
        kit.Hud.Lines[1].Should().Be("[Diagnostics.Hud.Book:3]");
        kit.Hud.Lines[2].Should().Match("[Diagnostics.Hud.Memory:*]");
        kit.Hud.Lines[3].Should().Be("[Diagnostics.Hud.Queue:0|8192|░░░░░░░░]");
        kit.Hud.Lines[4].Should().Be(
            "[Diagnostics.Hud.Latency:≤1ms 1 · ≤5ms 0 · ≤20ms 0 · ≤100ms 0 · ≤500ms 0 · >500ms 2]");
    }

    /// <summary>Off, no snapshot ever re-formats the lines — the empty readout stays empty.</summary>
    [Fact]
    public async Task OffState_EmitsNoLines()
    {
        var kit = HudTests.CreateKit();
        await kit.Hud.InitializeAsync();
        await AvaloniaDispatcherPump.DrainAsync();

        kit.BookUpdates.OnNext(HudTests.FreshBook());
        kit.Service.EmitSample();
        await AvaloniaDispatcherPump.DrainAsync();

        kit.Hud.Lines.Should().BeEmpty("the off sampler never emits — the overlay would show nothing anyway");
    }
}
