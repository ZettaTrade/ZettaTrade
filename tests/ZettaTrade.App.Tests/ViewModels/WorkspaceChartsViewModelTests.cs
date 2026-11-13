namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.App.Tests.Views.Headless;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The multi-chart layout ViewModel's persistence and slot facts (T-201):
/// the restore applies the stored mode and seam sizes before any window
/// opens and never rewrites what it read, a user pick persists the mode
/// and reuses the session's slot ViewModels across mode switches, a
/// settled seam burst persists once through the throttle, the external
/// writers apply live with their caches leading the property changes
/// (garbage keeps the standing mode), and the slots persist their picks
/// under their own per-slot keys while the shared workspace keys stay the
/// primary's. No views attach on this lane — the facts assert the pure
/// ViewModel state, and the slots' init walks ride the serialized
/// dispatcher pump.
/// </summary>
[Collection("AvaloniaDispatcherSerial")]
public sealed class WorkspaceChartsViewModelTests
{
    static WorkspaceChartsViewModelTests() => AvaloniaDispatcherPump.EnsureInstalled();

    [Fact]
    public async Task AStartupWithoutStoredLayout_RestoresTheSinglePanel_AndWritesNothing()
    {
        var (charts, settings, _, slotIds) = CreateKit();

        await charts.InitializeAsync();
        await AvaloniaDispatcherPump.DrainAsync();

        charts.Layout.Should().Be(ChartLayoutMode.Single, "a fresh profile starts on the one-panel layout");
        charts.Slot1.Should().BeNull("the single mode never creates slots");
        charts.SlotColumnWidth.Value.Should().Be(UiSettingsKeys.DefaultChartSlotColumnWidth, "the fresh profile serves the shipped default");
        charts.TopRowHeight.Value.Should().Be(UiSettingsKeys.DefaultChartGridTopRowHeight, "the fresh profile serves the shipped default");

        // The restore reads: it never rewrites anything it just applied.
        await settings.DidNotReceive().SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartLayout, Arg.Any<string>());
        await settings.DidNotReceive().SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartSlotColumnWidth, Arg.Any<double>());
        await settings.DidNotReceive().SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartGridTopRowHeight, Arg.Any<double>());
        slotIds.Should().BeEmpty("the single mode never reaches the slot factory");
    }

    [Fact]
    public async Task AGarbageStoredLayout_RestoresTheSinglePanel_WithoutRewritingIt()
    {
        var (charts, settings, _, slotIds) = CreateKit();
        settings.GetAsync<string>(UiSettingsKeys.Section, UiSettingsKeys.ChartLayout, Arg.Any<string>())
            .Returns(Task.FromResult("Hexagonal"));

        await charts.InitializeAsync();
        await AvaloniaDispatcherPump.DrainAsync();

        charts.Layout.Should().Be(ChartLayoutMode.Single, "an unrecognized stored layout falls back to the shipped default");

        await settings.DidNotReceive().SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartLayout, Arg.Any<string>());
        slotIds.Should().BeEmpty("the fallback restores no slots");
    }

    [Fact]
    public async Task ARestoredTwoByTwo_CreatesItsThreeSlots_AndWritesNothing()
    {
        var (charts, settings, _, slotIds) = CreateKit();
        settings.GetAsync<string>(UiSettingsKeys.Section, UiSettingsKeys.ChartLayout, Arg.Any<string>())
            .Returns(Task.FromResult("TwoByTwo"));

        await charts.InitializeAsync();
        await AwaitSlotsSettledAsync(charts.Slot1, charts.Slot2, charts.Slot3);

        charts.Layout.Should().Be(ChartLayoutMode.TwoByTwo, "the stored mode restores");
        charts.Slot1.Should().NotBeNull("the top-left slot exists before the raise the grid mirrors");
        charts.Slot2.Should().NotBeNull("the bottom-left slot exists");
        charts.Slot3.Should().NotBeNull("the bottom-right slot exists");
        charts.SlotColumnWidth.Value.Should().Be(UiSettingsKeys.DefaultChartSlotColumnWidth, "the echo-backed substitute serves the shipped default");
        charts.TopRowHeight.Value.Should().Be(UiSettingsKeys.DefaultChartGridTopRowHeight, "the echo-backed substitute serves the shipped default");

        slotIds.Should().BeEquivalentTo(["1", "2", "3"], "the restore creates exactly the mode's slots, numbered from one");
        // The restore reads: the mode and the seams it just applied are
        // never rewritten.
        await settings.DidNotReceive().SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartLayout, Arg.Any<string>());
        await settings.DidNotReceive().SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartSlotColumnWidth, Arg.Any<double>());
        await settings.DidNotReceive().SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartGridTopRowHeight, Arg.Any<double>());
    }

    [Fact]
    public async Task SlotFacesPersistTheirPicksUnderTheirOwnKeys_NeverTheSharedOnes()
    {
        var (charts, settings, _, _) = CreateKit();
        settings.GetAsync<string>(UiSettingsKeys.Section, UiSettingsKeys.ChartLayout, Arg.Any<string>())
            .Returns(Task.FromResult("TwoByTwo"));

        await charts.InitializeAsync();
        await AwaitSlotsSettledAsync(charts.Slot1, charts.Slot2, charts.Slot3);

        // The bootstrap applied its fall-through under the suppression
        // flag: a restore never rewrites the symbol it deferred.
        await settings.DidNotReceive().SetAsync(UiSettingsKeys.Section, $"{UiSettingsKeys.ChartSlotPrefix}1SymbolId", Arg.Any<string>());

        charts.Slot1!.SelectedTimeFrame = TimeFrame.H4;
        charts.Slot1.SelectedSymbol = Symbol.Parse("ETH/USDT");
        await AvaloniaDispatcherPump.DrainAsync();

        await settings.Received(1).SetAsync(UiSettingsKeys.Section, $"{UiSettingsKeys.ChartSlotPrefix}1TimeFrame", "H4");
        await settings.Received(1).SetAsync(UiSettingsKeys.Section, $"{UiSettingsKeys.ChartSlotPrefix}1SymbolId", "ETH/USDT");

        // The shared keys stay the primary's: a slot never writes them.
        await settings.DidNotReceive().SetAsync(UiSettingsKeys.WorkspaceSection, UiSettingsKeys.WorkspaceSymbolId, Arg.Any<string>());
        await settings.DidNotReceive().SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartTimeFrame, Arg.Any<string>());

        // The untouched slots wrote nothing at all.
        await settings.DidNotReceive().SetAsync(UiSettingsKeys.Section, $"{UiSettingsKeys.ChartSlotPrefix}2TimeFrame", Arg.Any<string>());
    }

    [Fact]
    public async Task AUserLayoutPick_PersistsPerPick_AndReusesTheSessionSlots()
    {
        var (charts, settings, _, slotIds) = CreateKit();

        await charts.SetLayoutCommand.Execute(ChartLayoutMode.OneByTwo);
        await AwaitSlotsSettledAsync(charts.Slot1);
        var firstSlot = charts.Slot1;

        await charts.SetLayoutCommand.Execute(ChartLayoutMode.TwoByTwo);
        await AwaitSlotsSettledAsync(charts.Slot1, charts.Slot2, charts.Slot3);

        await charts.SetLayoutCommand.Execute(ChartLayoutMode.Single);
        await charts.SetLayoutCommand.Execute(ChartLayoutMode.TwoByTwo);
        await AwaitSlotsSettledAsync(charts.Slot1, charts.Slot2, charts.Slot3);

        charts.Slot1.Should().BeSameAs(firstSlot, "slots are never torn down within the session — a switch back re-shows them");
        slotIds.Should().BeEquivalentTo(["1", "2", "3"], "every slot is created exactly once across the whole mode walk");

        await settings.Received(1).SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartLayout, "OneByTwo");
        await settings.Received(2).SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartLayout, "TwoByTwo");
        await settings.Received(1).SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartLayout, "Single");
    }

    [Fact]
    public async Task ASettledSeamDragPersistsThroughTheSettingsPipeline()
    {
        var (charts, settings, _, _) = CreateKit();

        // The grid's ride feeds the splitter's writeback into the property;
        // a burst of two writes settles into ONE debounced persist of the
        // last value, clamped to the usable minimum.
        charts.SlotColumnWidth = new GridLength(330);
        charts.SlotColumnWidth = new GridLength(340);
        await Task.Delay(400);
        await AvaloniaDispatcherPump.DrainAsync();

        await settings.Received(1).SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartSlotColumnWidth, 340d);
        await settings.DidNotReceive().SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartSlotColumnWidth, 330d);

        charts.TopRowHeight = new GridLength(430);
        await Task.Delay(400);
        await AvaloniaDispatcherPump.DrainAsync();

        await settings.Received(1).SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartGridTopRowHeight, 430d);
    }

    [Fact]
    public void AnExternalSeamWriterAppliesLive_AndNeverRewrites()
    {
        var (charts, settings, changes, _) = CreateKit();

        changes.OnNext(new SettingsChange(UiSettingsKeys.Section, UiSettingsKeys.ChartSlotColumnWidth, null, 350d));
        charts.SlotColumnWidth.Value.Should().Be(350, "an external writer applies its value live");

        changes.OnNext(new SettingsChange(UiSettingsKeys.Section, UiSettingsKeys.ChartSlotColumnWidth, 350d, -50d));
        charts.SlotColumnWidth.Value.Should().Be(UiSettingsKeys.MinChartSlotColumnWidth, "a negative stored width clamps to the usable minimum");

        changes.OnNext(new SettingsChange(UiSettingsKeys.Section, UiSettingsKeys.ChartSlotColumnWidth, -50d, double.PositiveInfinity));
        charts.SlotColumnWidth.Value.Should().Be(UiSettingsKeys.DefaultChartSlotColumnWidth, "a non-finite stored width falls back to the shipped default");

        changes.OnNext(new SettingsChange(UiSettingsKeys.Section, UiSettingsKeys.ChartGridTopRowHeight, null, 460d));
        charts.TopRowHeight.Value.Should().Be(460, "the top row's external writer applies live the same way");

        // The caches led every change: the persistence pipeline saw an
        // already-applied value each time and stayed silent. The facts run
        // on a plain thread (no awaitable shape needed — no CS4014 lane).
        settings.DidNotReceive().SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartSlotColumnWidth, Arg.Any<double>());
        settings.DidNotReceive().SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartGridTopRowHeight, Arg.Any<double>());
    }

    [Fact]
    public void AnExternalLayoutWriterAppliesLive_GarbageKeepsTheStandingMode_AndNeitherWrites()
    {
        var (charts, settings, changes, slotIds) = CreateKit();

        changes.OnNext(new SettingsChange(UiSettingsKeys.Section, UiSettingsKeys.ChartLayout, null, "TwoByTwo"));

        changes.OnNext(new SettingsChange(UiSettingsKeys.Section, UiSettingsKeys.ChartLayout, "TwoByTwo", "Hexagonal"));

        charts.Layout.Should().Be(ChartLayoutMode.TwoByTwo, "an unrecognized external write keeps the standing mode");
        slotIds.Should().BeEquivalentTo(["1", "2", "3"], "the garbage push never rebuilt the slots");
        settings.DidNotReceive().SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartLayout, Arg.Any<string>());
    }

    [Fact]
    public async Task AGarbageSeamRestore_FallsBackToTheDefaults()
    {
        var (charts, settings, _, _) = CreateKit();
        // The kit's echo stub serves the substitute's honest fresh-profile
        // shape; a hand-edited file's out-of-range magnitudes read as
        // infinities — the more specific matcher wins the restore's reads.
        settings.GetAsync<double>(UiSettingsKeys.Section, UiSettingsKeys.ChartSlotColumnWidth, Arg.Any<double>())
            .Returns(Task.FromResult(double.PositiveInfinity));
        settings.GetAsync<double>(UiSettingsKeys.Section, UiSettingsKeys.ChartGridTopRowHeight, Arg.Any<double>())
            .Returns(Task.FromResult(double.PositiveInfinity));

        await charts.InitializeAsync();
        await AvaloniaDispatcherPump.DrainAsync();

        charts.SlotColumnWidth.Value.Should().Be(UiSettingsKeys.DefaultChartSlotColumnWidth, "a non-finite width falls back to the default");
        charts.TopRowHeight.Value.Should().Be(UiSettingsKeys.DefaultChartGridTopRowHeight, "a non-finite height falls back to the default");
    }

    [Fact]
    public async Task AnInvertedSeamRestore_ClampsToTheUsableMinimum()
    {
        var (charts, settings, _, _) = CreateKit();
        settings.GetAsync<double>(UiSettingsKeys.Section, UiSettingsKeys.ChartSlotColumnWidth, Arg.Any<double>())
            .Returns(Task.FromResult(-50d));
        settings.GetAsync<double>(UiSettingsKeys.Section, UiSettingsKeys.ChartGridTopRowHeight, Arg.Any<double>())
            .Returns(Task.FromResult(-50d));

        await charts.InitializeAsync();
        await AvaloniaDispatcherPump.DrainAsync();

        charts.SlotColumnWidth.Value.Should().Be(UiSettingsKeys.MinChartSlotColumnWidth, "a negative width clamps to the usable minimum");
        charts.TopRowHeight.Value.Should().Be(UiSettingsKeys.MinChartGridTopRowHeight, "a negative height clamps to the usable minimum");
    }

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

    /// <summary>
    /// One wired layout ViewModel over a substitute settings service and
    /// the chart recipe's slot factory: the factory records every call and
    /// serves a real chart ViewModel per slot number, so a restored tiled
    /// mode exercises the genuine per-slot persistence keys.
    /// </summary>
    private static (WorkspaceChartsViewModel Charts, ISettingsService Settings, Subject<SettingsChange> Changes, List<string> SlotIds) CreateKit()
    {
        var settings = Substitute.For<ISettingsService>();
        var changes = new Subject<SettingsChange>();
        settings.Changes.Returns(changes);
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
        // The honest fresh-profile shape: the substitute answers the
        // stored default back, exactly what the real service does for an
        // absent key (a bare 0 would clamp to the usable minimum instead).
        settings.GetAsync<double>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>())
            .Returns(ci => Task.FromResult(ci.ArgAt<double>(2)));
        settings.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);

        var adapter = new MainWorld.FakeMarketDataAdapter("demo", "Demo", "BTC/USDT", "ETH/USDT");
        var activeExchanges = new ActiveExchangeService(
            new IMarketDataAdapter[] { adapter },
            settings,
            NullLogger<ActiveExchangeService>.Instance);

        var slotIds = new List<string>();
        var charts = new WorkspaceChartsViewModel(
            settings,
            slotId =>
            {
                slotIds.Add(slotId ?? string.Empty);
                return MainWorld.CreateChart(activeExchanges, settings, slotId: slotId);
            },
            NullLogger<WorkspaceChartsViewModel>.Instance);

        return (charts, settings, changes, slotIds);
    }
}
