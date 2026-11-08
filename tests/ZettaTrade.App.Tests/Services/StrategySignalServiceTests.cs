namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Strategies;

/// <summary>
/// The strategies hosting leg's feed facts (T-112): only PROVABLY closed
/// candles ever enter the host (the in-flight candle — whose cadence's
/// next open still runs at the delivery clock — is never judged), a
/// candle enters the pump EXACTLY ONCE per series (a re-covered delivery
/// — a flip back to a loaded set — drops its whole re-visit), and the
/// stance registry prunes to the delivered window's floor so the overlay
/// lookups never outgrow the displayed series. The demo crossover runs
/// the honest deterministic walk (neutral through the warmup, then the
/// trend), so a crafted monotonic series pins every fact without any
/// plugin machinery.
/// </summary>
public sealed class StrategySignalServiceTests
{
    /// <summary>Base instant the crafted candles step through (UTC, offset zero).</summary>
    private static readonly DateTimeOffset Base = new(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The demo's id (the strategies' own canonical one).</summary>
    private const string Demo = DemoGoldenCrossStrategy.StrategyId;

    /// <summary>The series identity the facts feed.</summary>
    private static readonly Symbol Symbol = Symbol.Parse("BTC/USDT");

    /// <summary>One rising H1 bar opened at <paramref name="hour"/> (close = 10 + hour).</summary>
    private static Bar HourBar(int hour, int count) =>
        new(Base.AddHours(hour), 9m + count, 10m + hour + 1m, 8m + hour, 10m + hour + 1m, 50m);

    /// <summary>A rising page: hourly bars, ascending closes, every bar provably closed at the clock.</summary>
    private static List<Bar> RisingBars(params int[] hours)
    {
        var bars = new List<Bar>(hours.Length);
        for (var index = 0; index < hours.Length; index++)
        {
            bars.Add(HourBar(hours[index], index));
        }

        return bars;
    }

    /// <summary>The service over the demo-only catalog with the pinned clock.</summary>
    private sealed record TestService(
        StrategySignalService Service,
        SignalSeriesRelay Relay,
        Func<DateTimeOffset> Clock)
    {
        /// <summary>The pinned delivery clock (default: long after the crafted base so the past bars are provably closed).</summary>
        public static TestService Create(Func<DateTimeOffset>? clock = null)
        {
            var relay = new SignalSeriesRelay();
            var pinned = clock ?? (() => Base.AddHours(11));
            var service = new StrategySignalService(
                relay.Pages,
                new FixedCatalog(new DemoGoldenCrossStrategy()),
                NullLoggerFactory.Instance,
                pinned);
            return new TestService(service, relay, pinned);
        }
    }

    /// <summary>The T-111 catalog seam, fixed to one strategy set.</summary>
    private sealed class FixedCatalog(params IReadOnlyList<IStrategy> strategies) : IStrategyCatalog
    {
        public IReadOnlyList<IStrategy> All => strategies;

        public IStrategy? Get(string strategyId) =>
            strategyId == Demo ? strategies[0] : null;
    }

    /// <summary>Poll until the signal registry carries the target count (the pump is a hosted task).</summary>
    private static async Task AwaitCountAsync(List<StrategySignal> seen, int target)
    {
        for (var spin = 0; spin < 120 && seen.Count < target; spin++)
        {
            await Task.Delay(25);
        }
    }

    /// <summary>Record the service's own broadcast until disposed.</summary>
    private sealed class SignalWatcher : IDisposable
    {
        /// <summary>The broadcast as it landed.</summary>
        public List<StrategySignal> Seen { get; }

        private readonly IDisposable _subscription;

        public SignalWatcher(StrategySignalService service)
        {
            Seen = [];
            _subscription = service.Signals.Subscribe(
                Seen.Add,
                error => throw new InvalidOperationException("The signal stream faulted", error));
        }

        public void Dispose() => _subscription.Dispose();
    }

    [Fact]
    public async Task Feed_PastClosedCandles_EveryDeliveredCandleJudgedOnce()
    {
        var test = TestService.Create();
        var watcher = new SignalWatcher(test.Service);
        try
        {
            await test.Service.InitializeAsync(CancellationToken.None);

            // Six rising H1 bars: the demo walks five honest neutrals through
            // the warmup and one flat-out long at the sixth candle.
            test.Relay.Publish(new ChartSeriesPage(Symbol, TimeFrame.H1, RisingBars(0, 1, 2, 3, 4, 5)));
            await AwaitCountAsync(watcher.Seen, 6);
            await Task.Delay(150); // nothing more arrives for an unmolested window

            watcher.Seen.Should().HaveCount(6, "each closed candle was evaluated exactly once");
            watcher.Seen[0].Stance.Should().Be(StrategyStance.Neutral, "the warmup is an honest absence");
            watcher.Seen[0].StrategyId.Should().Be(Demo, "the host always carries its own stamps");
            watcher.Seen[0].Candle.Time.Should().Be(Base);
            watcher.Seen[5].Stance.Should().Be(StrategyStance.Long, "the rising series crossed golden at the sixth candle");
            watcher.Seen[5].Candle.Time.Should().Be(Base.AddHours(5));

            var stances = test.Service.StancesFor(Symbol, TimeFrame.H1);
            stances.Should().HaveCount(6);
            stances[Base.AddHours(5)].Should().Be(StrategyStance.Long);
            stances[Base].Should().Be(StrategyStance.Neutral);
        }
        finally
        {
            watcher.Dispose();
        }
    }

    [Fact]
    public async Task Feed_ReCoveringDelivery_DropsItsWholeReVisit()
    {
        var test = TestService.Create();
        var watcher = new SignalWatcher(test.Service);
        try
        {
            await test.Service.InitializeAsync(CancellationToken.None);

            var first = new ChartSeriesPage(Symbol, TimeFrame.H1, RisingBars(0, 1, 2, 3, 4, 5));
            test.Relay.Publish(first);
            await AwaitCountAsync(watcher.Seen, 6);

            // The chart flips back to a longer-loaded set: its candles are all
            // opens the pump already consumed — nothing re-enters, nothing
            // re-judges (six evaluations before, six after).
            test.Relay.Publish(new ChartSeriesPage(Symbol, TimeFrame.H1, RisingBars(0, 1, 2, 3)));
            test.Relay.Publish(first);
            await Task.Delay(400);

            watcher.Seen.Should().HaveCount(6, "a candle enters the pump exactly once per series");
            test.Service.StancesFor(Symbol, TimeFrame.H1).Should().HaveCount(6);
        }
        finally
        {
            watcher.Dispose();
        }
    }

    [Fact]
    public async Task Feed_InFlightCandle_IsNeverJudged()
    {
        // The clock runs between the ninth bar's own open and its next open:
        // nine bars are provably closed, the tenth is the running candle.
        var test = TestService.Create(clock: () => Base.AddHours(9.5));
        var watcher = new SignalWatcher(test.Service);
        try
        {
            await test.Service.InitializeAsync(CancellationToken.None);

            test.Relay.Publish(new ChartSeriesPage(
                Symbol,
                TimeFrame.H1,
                RisingBars(0, 1, 2, 3, 4, 5, 6, 7, 8, 9)));
            await AwaitCountAsync(watcher.Seen, 9);
            await Task.Delay(150);

            watcher.Seen.Should().HaveCount(9, "the in-flight candle never enters the pump");
            watcher.Seen[^1].Candle.Time.Should().Be(Base.AddHours(8), "the last judged candle is the last provably closed one");

            var stances = test.Service.StancesFor(Symbol, TimeFrame.H1);
            stances.Should().NotContainKey(Base.AddHours(9), "the running candle's open holds no stance");
            stances.Should().ContainKey(Base.AddHours(8));
        }
        finally
        {
            watcher.Dispose();
        }
    }

    [Fact]
    public async Task Feed_WindowFloor_PrunesTheRegistry()
    {
        var test = TestService.Create();
        var watcher = new SignalWatcher(test.Service);
        try
        {
            await test.Service.InitializeAsync(CancellationToken.None);

            test.Relay.Publish(new ChartSeriesPage(Symbol, TimeFrame.H1, RisingBars(0, 1, 2, 3, 4, 5)));
            await AwaitCountAsync(watcher.Seen, 6);

            // A later load slides the window: hours three through eight. The
            // re-covered opens (three, four, five) drop, the new opens enter,
            // and the registry prunes strictly to the delivered window's
            // floor — hours zero, one and two are gone for good.
            test.Relay.Publish(new ChartSeriesPage(
                Symbol,
                TimeFrame.H1,
                RisingBars(3, 4, 5, 6, 7, 8)));
            await AwaitCountAsync(watcher.Seen, 9);
            await Task.Delay(150);

            watcher.Seen.Should().HaveCount(9, "three new candles joined, the re-covered three did not re-judge");

            var stances = test.Service.StancesFor(Symbol, TimeFrame.H1);
            stances.Keys.Select(static open => open.Hour).OrderBy(static hour => hour).Should().Equal(3, 4, 5, 6, 7, 8);
        }
        finally
        {
            watcher.Dispose();
        }
    }
}
