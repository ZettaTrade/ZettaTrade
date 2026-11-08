namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using FluentAssertions;
using Livery.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using ReactiveUI;
using Xunit;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
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
        var rail = new SignalRailViewModel(signals, loc, NullLogger<SignalRailViewModel>.Instance);
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
            NullLogger<SignalRailViewModel>.Instance);

        var longRow = SignalRailRow.For(Signal("demo-golden-cross", 5, StrategyStance.Long), local, rail);
        longRow.Side.Should().Be(OrderSide.Buy, "a long reading maps to the buy side");
        longRow.IsEntryLong.Should().BeTrue();
        longRow.IsEntryFlat.Should().BeFalse();
        longRow.StanceLabel.Should().Be("[Signal.Rail.StanceLong]");
        longRow.SymbolText.Should().Be("BTC/USDT");
        longRow.PriceText.Should().Be("16.00", "close 16 renders with the ticket's own decimal floor");
        longRow.CandleTime.Should().Be("2026-03-02 05:00");
        longRow.Tooltip.Should().Be(
            "[Signal.Rail.Row.Tooltip/demo-golden-cross|BTC/USDT|16.00|2026-03-02 05:00]",
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
}
