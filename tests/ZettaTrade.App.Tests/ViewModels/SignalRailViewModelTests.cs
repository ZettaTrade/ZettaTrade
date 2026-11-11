namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using Avalonia.Controls;
using FluentAssertions;
using Livery.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ReactiveUI;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Strategies;

/// <summary>The half-day-agnostic status byte ReactiveUI.Primitives names RxVoid.</summary>
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// The signal rail's streaming facts (T-112): a burst inside one coalesce
/// window lands as ONE rows replacement (the flood bar), the rows ring
/// stays bounded at <see cref="SignalRailViewModel.MaxRows"/> with the
/// oldest evicted, a neutral stance is an honest absence that never
/// enters, rows rebuild newest-first, and a row press raises the row
/// itself over the prefill stream — the ticket facts, never an order.
/// The rail height facts (T-177): the walk restores stored, clamped or
/// garbage-safe default heights before the first frame and never rewrites
/// what it just applied; a settled drag persists once at the settled
/// height; an out-of-band write clamps at the persisted value; external
/// writers apply live without echoing.
/// Rows ride the dispatcher only through their marshaled assignment, so
/// the facts run inside the AvaloniaDispatcherSerial collection and drain
/// the pump to observe the settled rows deterministically.
/// </summary>
[Collection("AvaloniaDispatcherSerial")]
public sealed class SignalRailViewModelTests
{
    static SignalRailViewModelTests() => AvaloniaDispatcherPump.EnsureInstalled();

    /// <summary>Base instant the crafted signals step through (UTC, offset zero).</summary>
    private static readonly DateTimeOffset Base = new(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);

    private static readonly Symbol Symbol = Symbol.Parse("BTC/USDT");

    /// <summary>
    /// How long a test waits for the debounced drag persistence to reach
    /// the settings service before calling the arrangement broken (never
    /// hit in a healthy pipeline — the burst write lands right after the
    /// VM's quarter-second quiet time; keeps a stalled test diagnosable).
    /// </summary>
    private static readonly TimeSpan SettleWaitBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a "nothing was written" assertion keeps watching past the
    /// drag quiet time before declaring the window closed — double the
    /// quiet time, so a late write would still land inside the observation.
    /// </summary>
    private static readonly TimeSpan EchoQuietBudget = TimeSpan.FromMilliseconds(500);

    /// <summary>Height fuzz a float comparison tolerates (mirrors the VM's epsilon).</summary>
    private const double HeightEpsilon = 0.01;

    /// <summary>The localized-text fake: keys read as bracketed ids, formats name their arguments in order.</summary>
    private sealed class FakeLocalization : ILocalizationManager
    {
        public string CurrentLanguage => "en";

        public IReadOnlyList<LanguageInfo> AvailableLanguages => [];

        public Task SetLanguageAsync(string cultureCode, CancellationToken ct = default) => Task.CompletedTask;

        public string GetString(string key) => "[" + key + "]";

        public string GetString(string key, params object[] args) =>
            "[" + key + "/" + string.Join("|", args) + "]";

        public IObservable<string> LanguageChanged => Observable.Empty<string>();

        public IObservable<string> GetObservable(string key) => Observable.Empty<string>();
    }

    /// <summary>One long signal at <paramref name="hour"/> (per-id distinct candles, close 10 + hour).</summary>
    private static StrategySignal Long(string id, int hour) => Signal(id, hour, StrategyStance.Long);

    /// <summary>One signal with the crafted stance.</summary>
    private static StrategySignal Signal(string id, int hour, StrategyStance stance) =>
        new(id, Symbol, TimeFrame.H1, new Bar(Base.AddHours(hour), 9m, 12m, 8m, 10m + hour + 1m, 50m), stance);

    /// <summary>Build the rail with its own subject; the caller owns the subject.</summary>
    private static (SignalRailViewModel Rail, Subject<StrategySignal> Signals, FakeLocalization Loc) Build()
    {
        var signals = new Subject<StrategySignal>();
        var loc = new FakeLocalization();
        var rail = new SignalRailViewModel(
            signals,
            loc,
            Substitute.For<ISettingsService>(),
            NullLogger<SignalRailViewModel>.Instance);
        return (rail, signals, loc);
    }

    /// <summary>Let one coalesce window close (generous margin) and settle the marshaled assignment.</summary>
    private static async Task SettleAsync(TimeSpan? settle = null) =>
        await Task.Delay(settle ?? SignalRailViewModel.RailCoalesceWindow + TimeSpan.FromMilliseconds(200));

    [Fact]
    public async Task BurstInsideOneWindow_LandsAsOneRowsReplacement()
    {
        var (rail, signals, _) = Build();
        var updates = 0;
        rail.WhenAnyValue(vm => vm.Rows).Skip(1).Subscribe(_ => updates++);

        signals.OnNext(Long("demo", 0));
        signals.OnNext(Long("ema", 1));
        signals.OnNext(Long("rsi", 2));
        await SettleAsync();
        await AvaloniaDispatcherPump.DrainAsync();

        updates.Should().Be(1, "a burst within one window is ONE replacement, never a flood");
        rail.Rows.Should().HaveCount(3);
        rail.Rows[0].Source.Should().Be(Signal("rsi", 2, StrategyStance.Long), "the batch lands newest first");
        rail.Rows[0].Source.StrategyId.Should().Be("rsi");
        rail.HasRows.Should().BeTrue();
        rail.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public async Task RowsRing_StaysBounded_WithOldestEvicted()
    {
        var (rail, signals, _) = Build();

        for (var hour = 0; hour < SignalRailViewModel.MaxRows + 4; hour++)
        {
            signals.OnNext(Long("demo", hour));
            if (hour % 4 == 3)
            {
                await SettleAsync(TimeSpan.FromMilliseconds(350));
            }
        }

        await SettleAsync(TimeSpan.FromMilliseconds(350));
        await AvaloniaDispatcherPump.DrainAsync();

        rail.Rows.Should().HaveCount(SignalRailViewModel.MaxRows);
        rail.Rows[0].Source.Candle.Time.Should().Be(
            Base.AddHours(SignalRailViewModel.MaxRows + 3),
            "the newest reading rides the top, the ring evicted the oldest tail");
        rail.Rows[^1].Source.Candle.Time.Should().Be(
            Base.AddHours(4),
            "the eviction falls off exactly past the bounded count");
    }

    [Fact]
    public async Task ABurstWiderThanTheRing_LandsBoundedAtTheCapacity()
    {
        var (rail, signals, _) = Build();

        // 14 readings inside ONE coalesce window: the ring still holds at
        // its capacity — the burst's oldest tail falls off with the rest
        // (the T-177 scroll probe caught the unbounded-burst gap).
        for (var hour = 0; hour < SignalRailViewModel.MaxRows + 2; hour++)
        {
            signals.OnNext(Long("demo", hour));
        }

        await SettleAsync();
        await AvaloniaDispatcherPump.DrainAsync();

        rail.Rows.Should().HaveCount(
            SignalRailViewModel.MaxRows,
            "a burst wider than the ring never grows the rail past it");
        rail.Rows[0].Source.Candle.Time.Should().Be(
            Base.AddHours(SignalRailViewModel.MaxRows + 1),
            "the newest reading rides the top");
        rail.Rows[^1].Source.Candle.Time.Should().Be(
            Base.AddHours(2),
            "the burst's oldest tail is the eviction");
    }

    [Fact]
    public async Task NeutralStance_IsAnHonestAbsence_NeverEntersTheRail()
    {
        var (rail, signals, _) = Build();

        signals.OnNext(Signal("ema", 0, StrategyStance.Neutral));
        signals.OnNext(Long("rsi", 1));
        signals.OnNext(Signal("macd", 2, StrategyStance.Neutral));
        await SettleAsync();
        await AvaloniaDispatcherPump.DrainAsync();

        rail.Rows.Should().HaveCount(1, "neutral is not a reading");
        rail.Rows[0].Source.StrategyId.Should().Be("rsi");
    }

    [Fact]
    public async Task RowPress_RaisesTheRowItself_ThroughThePrefillStream()
    {
        var (rail, signals, _) = Build();
        var prefills = new List<SignalRailRow>();
        using var seen = rail.RowPrefillRequested.Subscribe(prefills.Add);

        signals.OnNext(Long("demo", 0));
        await SettleAsync();
        await AvaloniaDispatcherPump.DrainAsync();

        await rail.Rows[0].PrefillCommand.Execute(RxVoid.Default);

        prefills.Should().ContainSingle();
        prefills[0].Should().Be(rail.Rows[0], "the sink raises the pressed row, never dispatches it");
    }

    [Fact]
    public void Row_ComposesThePrefillFacts_AtItsArrival()
    {
        var local = new FakeLocalization();
        var rail = new SignalRailViewModel(
            new Subject<StrategySignal>(),
            local,
            Substitute.For<ISettingsService>(),
            NullLogger<SignalRailViewModel>.Instance);

        var longRow = SignalRailRow.For(Signal("sma-golden-cross", 5, StrategyStance.Long), local, rail);
        longRow.Side.Should().Be(OrderSide.Buy, "a long reading maps to the buy side");
        longRow.IsEntryLong.Should().BeTrue();
        longRow.IsEntryFlat.Should().BeFalse();
        longRow.StanceLabel.Should().Be("[Signal.Rail.StanceLong]");
        longRow.SymbolText.Should().Be("BTC/USDT");
        longRow.PriceText.Should().Be("16.00", "close 16 renders with the ticket's own decimal floor");
        longRow.CandleTime.Should().Be("2026-03-02 05:00");
        longRow.Tooltip.Should().Be(
            "[Signal.Rail.Row.Tooltip/sma-golden-cross|BTC/USDT|16.00|2026-03-02 05:00]",
            "the tooltip composes at arrival: strategy, symbol, close, time");

        var flatRow = SignalRailRow.For(Signal("ema", 6, StrategyStance.Flat), local, rail);
        flatRow.Side.Should().Be(OrderSide.Sell, "a flat reading maps to the sell side");
        flatRow.IsEntryFlat.Should().BeTrue();
        flatRow.StanceLabel.Should().Be("[Signal.Rail.StanceFlat]");
    }

    [Fact]
    public async Task SettleFlags_AreHonestThroughTheRing()
    {
        var (rail, signals, _) = Build();

        await Task.Delay(50);
        await AvaloniaDispatcherPump.DrainAsync();
        rail.IsEmpty.Should().BeTrue();
        rail.HasRows.Should().BeFalse();

        signals.OnNext(Long("demo", 0));
        await SettleAsync();
        await AvaloniaDispatcherPump.DrainAsync();

        rail.Rows.Should().ContainSingle();
        rail.IsEmpty.Should().BeFalse();
        rail.HasRows.Should().BeTrue();
    }

    // The rail height facts (T-177): the height is persisted state riding
    // the workspace-geometry pipeline shape (T-038), row-wise.

    /// <summary>
    /// The walk with the third-party default read keeps the layout every
    /// session saw before the splitter existed: the old 172 px cap.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_NoStoredHeight_KeepsTheShippedDefault()
    {
        var kit = await CreateHeightKitAsync();

        CarriesPixelHeight(kit.Rail.RailHeight, UiSettingsKeys.DefaultSignalRailHeight);
        kit.Rail.MinRailHeight.Should().Be(UiSettingsKeys.MinSignalRailHeight);
        kit.Rail.MaxRailHeight.Should().Be(UiSettingsKeys.MaxSignalRailHeight);
    }

    /// <summary>
    /// The stored height applies at the walk, and the walk never writes
    /// the value it just read back.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_RestoresTheStoredHeight_WithoutRewritingIt()
    {
        var kit = await CreateHeightKitAsync(storedHeight: 300);

        CarriesPixelHeight(kit.Rail.RailHeight, 300);
        await WaitPastQuietTimeAsync();
        await kit.Settings.DidNotReceive().SetAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<double>());
    }

    /// <summary>
    /// Whatever the settings reader let through non-finite (an out-of-range
    /// JSON magnitude such as <c>1e999</c>, a hand-edited file) falls back
    /// to the shipped default instead of eating the chart's band.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_GarbageStoredHeight_FallsBackToTheDefault()
    {
        var kit = await CreateHeightKitAsync(storedHeight: double.PositiveInfinity);

        CarriesPixelHeight(kit.Rail.RailHeight, UiSettingsKeys.DefaultSignalRailHeight);
    }

    /// <summary>
    /// A stored height outside the usable band clamps into it on restore,
    /// silently (the clamp is not a user choice worth a rewrite).
    /// </summary>
    [Fact]
    public async Task InitializeAsync_OutOfBandStoredHeights_ClampIntoTheUsableBand()
    {
        var low = await CreateHeightKitAsync(storedHeight: 50);
        CarriesPixelHeight(low.Rail.RailHeight, UiSettingsKeys.MinSignalRailHeight);
        await WaitPastQuietTimeAsync();
        await low.Settings.DidNotReceive().SetAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<double>());

        var high = await CreateHeightKitAsync(storedHeight: 5000);
        CarriesPixelHeight(high.Rail.RailHeight, UiSettingsKeys.MaxSignalRailHeight);
        await WaitPastQuietTimeAsync();
        await high.Settings.DidNotReceive().SetAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<double>());
    }

    /// <summary>
    /// A settled rail drag persists ONCE, at the settled height: the
    /// burst's per-pixel arrangement collapses behind the debounce (a
    /// preview write of an intermediate height never reaches the settings
    /// file).
    /// </summary>
    [Fact]
    public async Task DragSettles_TheBurstPersistsOnceAtTheSettledHeight()
    {
        var kit = await CreateHeightKitAsync();

        // Act: one drag burst — a few writebacks a pixel apart, then settle
        kit.Rail.RailHeight = new GridLength(200);
        kit.Rail.RailHeight = new GridLength(260);
        var signal = ArmWriteSignal(kit.Settings);
        kit.Rail.RailHeight = new GridLength(280);

        // Assert
        var settled = await signal.Task.WaitAsync(SettleWaitBudget);
        settled.Should().Be(280, "the burst persists the height the drag settled at");
        await kit.Settings.Received(1).SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.SignalRailHeight,
            280d);

        // Act: a second, later burst is a new difference and persists too
        kit.Rail.RailHeight = new GridLength(320);
        settled = await ArmWriteSignal(kit.Settings).Task.WaitAsync(SettleWaitBudget);

        settled.Should().Be(320);
        await kit.Settings.Received(1).SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.SignalRailHeight,
            320d);
    }

    /// <summary>
    /// A write past the usable band clamps at the persisted value: the
    /// splitter's own row clamp stops real drags, and any programmatic
    /// write beyond it never banks a chart-eating height.
    /// </summary>
    [Fact]
    public async Task AWriteAboveTheMaximum_ClampsThePersistedValue()
    {
        var kit = await CreateHeightKitAsync();

        kit.Rail.RailHeight = new GridLength(600);

        var settled = await ArmWriteSignal(kit.Settings).Task.WaitAsync(SettleWaitBudget);

        settled.Should().Be(UiSettingsKeys.MaxSignalRailHeight);
        await kit.Settings.Received(1).SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.SignalRailHeight,
            UiSettingsKeys.MaxSignalRailHeight);
    }

    /// <summary>
    /// An external writer re-applies the rail height live without a
    /// restart: matching keys apply, unrelated keys and non-double
    /// payloads drop.
    /// </summary>
    [Fact]
    public async Task TheSettingsServiceChange_AppliesTheHeightLive_WithoutEchoingIt()
    {
        var kit = await CreateHeightKitAsync();

        kit.Changes.OnNext(new SettingsChange(
            UiSettingsKeys.Section,
            UiSettingsKeys.Theme,
            "dark",
            "light"));
        kit.Changes.OnNext(new SettingsChange(
            UiSettingsKeys.Section,
            UiSettingsKeys.SignalRailHeight,
            UiSettingsKeys.DefaultSignalRailHeight,
            "not a number"));
        CarriesPixelHeight(kit.Rail.RailHeight, UiSettingsKeys.DefaultSignalRailHeight,
            "an unrelated key and a non-double payload both leave the height alone");

        kit.Changes.OnNext(new SettingsChange(
            UiSettingsKeys.Section,
            UiSettingsKeys.SignalRailHeight,
            UiSettingsKeys.DefaultSignalRailHeight,
            320.0));

        CarriesPixelHeight(kit.Rail.RailHeight, 320);
        await WaitPastQuietTimeAsync();
        await kit.Settings.DidNotReceive().SetAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<double>());
    }

    /// <summary>
    /// The applied external change is silent on the settings service, and
    /// a real drag AFTER the application still persists its (now
    /// different) settled height.
    /// </summary>
    [Fact]
    public async Task TheSettingsServiceChange_TheEchoIsSilent_ARealDragStillPersists()
    {
        var kit = await CreateHeightKitAsync();

        // Act: an external writer applies a height live
        kit.Changes.OnNext(new SettingsChange(
            UiSettingsKeys.Section,
            UiSettingsKeys.SignalRailHeight,
            UiSettingsKeys.DefaultSignalRailHeight,
            320.0));

        // Assert: the application stayed silent
        await WaitPastQuietTimeAsync();
        await kit.Settings.DidNotReceive().SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.SignalRailHeight,
            320.0);

        // Act: the user re-dragged the rail after the application
        var signal = ArmWriteSignal(kit.Settings);
        kit.Rail.RailHeight = new GridLength(360);

        var settled = await signal.Task.WaitAsync(SettleWaitBudget);

        settled.Should().Be(360);
        await kit.Settings.Received(1).SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.SignalRailHeight,
            360d);
    }

    /// <summary>
    /// A wired height kit: the settings substitute answers the rail height
    /// read, the changes stream is an open subject, and the rail stands
    /// where the startup walk leaves it — constructed and initialized.
    /// </summary>
    private static async Task<HeightKit> CreateHeightKitAsync(
        double storedHeight = UiSettingsKeys.DefaultSignalRailHeight)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<double>(
                Arg.Any<string>(),
                Arg.Is<string>(key => key == UiSettingsKeys.SignalRailHeight),
                Arg.Any<double>())
            .Returns(storedHeight);
        var changes = new Subject<SettingsChange>();
        settings.Changes.Returns(changes);

        var rail = new SignalRailViewModel(
            new Subject<StrategySignal>(),
            new FakeLocalization(),
            settings,
            NullLogger<SignalRailViewModel>.Instance);
        await rail.InitializeAsync();

        return new HeightKit(rail, settings, changes);
    }

    /// <summary>
    /// Arm the write signal: the NEXT settings write with a double payload
    /// completes the returned task with its value (a deterministic wait for
    /// the debounced burst; a second burst arms a fresh signal, since each
    /// arm re-configures the write handler).
    /// </summary>
    private static TaskCompletionSource<double> ArmWriteSignal(ISettingsService settings)
    {
        var signal = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        settings.SetAsync<double>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>())
            .Returns(callInfo =>
            {
                signal.TrySetResult(callInfo.ArgAt<double>(2));
                return Task.CompletedTask;
            });
        return signal;
    }

    /// <summary>Give the quiet-time window a chance to invalidate a silence claim.</summary>
    private static Task WaitPastQuietTimeAsync() => Task.Delay(EchoQuietBudget);

    /// <summary>Assert an absolute (pixel) grid length carries the expected height.</summary>
    private static void CarriesPixelHeight(GridLength actual, double expected, string because = "")
    {
        actual.IsAbsolute.Should().BeTrue("the rail height is an absolute pixel length");
        actual.Value.Should().BeApproximately(expected, HeightEpsilon, because);
    }
}

/// <summary>One wired rail height pipeline with every collaborator a test drives.</summary>
internal sealed record HeightKit(
    SignalRailViewModel Rail,
    ISettingsService Settings,
    Subject<SettingsChange> Changes);
