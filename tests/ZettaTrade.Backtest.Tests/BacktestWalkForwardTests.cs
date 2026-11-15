namespace ZettaTrade.Backtest.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Fees;
using ZettaTrade.Plugins.SampleStrategy;
using ZettaTrade.Strategies;

/// <summary>
/// The walk-forward engine's facts (T-254): the loaded window carves into
/// the folds' consecutive equal segments (each split 50/50 into an
/// in-sample head the grid optimizes on and an out-of-sample tail the
/// chosen pair replays on), the whole study loads its window ONCE, and a
/// cooperative stop lands at a fold boundary only — a fold's row exists
/// only when its whole optimize-then-verify pass completed. The same study
/// over the same cache is <see cref="BacktestWalkForwardResult"/>
/// byte-identical. Everything invalid is refused before any replay runs.
/// </summary>
public sealed class BacktestWalkForwardTests
{
    #region Fixture

    private static readonly DateTimeOffset Base = new(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);

    private static readonly Symbol BtcUsdt = Symbol.Parse("BTC/USDT");

    /// <summary>The demo adapter identity the fixture caches and books under.</summary>
    private const string DemoAdapter = "demo";

    /// <summary>One deterministic bar of the shared sawtooth series (the sweep facts' shape).</summary>
    private static Bar TestBar(int index, int total)
    {
        var half = total / 2;
        var trend = index < half ? index : total - 1 - index;
        var open = 100m + trend * 0.5m;
        return new Bar(
            Time: Base.AddHours(index),
            Open: open,
            High: open + 1m,
            Low: open - 0.5m,
            Close: open + 0.5m,
            Volume: 10m);
    }

    private static IReadOnlyList<Bar> Series(int count) =>
        Enumerable.Range(0, count).Select(index => TestBar(index, count)).ToList();

    private static Task SeedAsync(TestCandleCache cache, int count) =>
        cache.WriteThroughAsync(DemoAdapter, BtcUsdt, TimeFrame.H1, Series(count));

    private sealed class TestCatalog(params IStrategy[] strategies) : IStrategyCatalog
    {
        private readonly IStrategy[] _strategies = strategies;

        public IReadOnlyList<IStrategy> All => _strategies;

        public IStrategy? Get(string strategyId) => _strategies.FirstOrDefault(strategy => strategy.Id == strategyId);
    }

    private static IFeeScheduleProvider ZeroFees()
    {
        var provider = Substitute.For<IFeeScheduleProvider>();
        provider.GetSchedule(DemoAdapter).Returns(new FeeSchedule(0m, 0m, 0m));
        return provider;
    }

    private static (BacktestService Service, TestCandleCache Cache) Build(params IStrategy[] strategies)
    {
        if (strategies.Length == 0)
        {
            strategies = [new SampleCrossStrategy()];
        }

        var cache = new TestCandleCache();
        var service = new BacktestService(
            new TestCatalog(strategies),
            cache,
            ZeroFees(),
            NullLoggerFactory.Instance);
        return (service, cache);
    }

    /// <summary>The study's template: the demo adapter's H1 series, 256 bars (two 128-bar folds at the default carve).</summary>
    private static BacktestRunRequest Template(
        int bars = 256,
        int seed = 7,
        decimal slippage = 1m,
        decimal fraction = 1m) => new()
        {
            StrategyId = SampleCrossStrategy.StrategyId,
            AdapterId = DemoAdapter,
            Symbol = BtcUsdt,
            Interval = TimeFrame.H1,
            Bars = bars,
            Seed = seed,
            SlippageValue = slippage,
            LongNotionalFraction = fraction,
        };

    private static BacktestWalkForwardRequest Study(
        BacktestRunRequest? template = null,
        BacktestSweepParameter firstParameter = BacktestSweepParameter.LongNotionalFraction,
        decimal[]? firstValues = null,
        BacktestSweepParameter secondParameter = BacktestSweepParameter.SlippageValue,
        decimal[]? secondValues = null,
        int folds = 2) => new()
        {
            Template = template ?? Template(),
            FirstAxis = new BacktestSweepAxis(
            firstParameter,
            firstValues ?? [0.5m, 0.25m]),
            SecondAxis = new BacktestSweepAxis(
            secondParameter,
            secondValues ?? [1m, 2m]),
            Folds = folds,
        };

    #endregion Fixture

    #region Fold Carve

    [Fact]
    public void FoldPlan_CarvesTheGoldenSegments()
    {
        var plan = Study(folds: 4).FoldPlan(260);

        plan.Should().HaveCount(4);
        // 260 bars / 4 folds = 65-bar segments (the 0 remainder bars unused),
        // each split 50/50 with the odd bar riding the out-of-sample tail.
        plan.Select(split => split.Offset).Should().Equal([0, 65, 130, 195]);
        plan.Select(split => split.InSampleBars).Should().OnlyContain(bars => bars == 32);
        plan.Select(split => split.OutOfSampleBars).Should().OnlyContain(bars => bars == 33);
    }

    [Fact]
    public void FoldPlan_RefusesAnEmptyWindow()
    {
        var refuse = () => Study(folds: 2).FoldPlan(0);

        refuse.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*empty*");
    }

    [Fact]
    public void FoldPlan_RefusesAWindowTooShortForTheFolds()
    {
        // 255 bars / 4 folds = 63-bar segments — one bar short of the two
        // 32-bar halves every honest replay needs.
        var refuse = () => Study(folds: 4).FoldPlan(255);

        refuse.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*each fold needs at least 64 bars*");
    }

    #endregion Fold Carve

    #region Validation

    [Fact]
    public void Validate_RefusesDuplicateAxesAndOutOfBoundsFolds()
    {
        var nullTemplate = () => new BacktestWalkForwardRequest
        {
            Template = null!,
            FirstAxis = new BacktestSweepAxis(BacktestSweepParameter.LongNotionalFraction, [1m]),
            SecondAxis = new BacktestSweepAxis(BacktestSweepParameter.SlippageValue, [1m]),
        }.Validate();
        nullTemplate.Should().Throw<ArgumentNullException>();

        var duplicate = () => Study(firstParameter: BacktestSweepParameter.SlippageValue).Validate();
        duplicate.Should().Throw<ArgumentException>().WithMessage("*different parameters*");

        var tooFew = () => Study(folds: 1).Validate();
        tooFew.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*between*");

        var tooMany = () => Study(folds: 65).Validate();
        tooMany.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*between*");
    }

    #endregion Validation

    #region End to End

    [Fact]
    public async Task Study_TwoFoldsOverATwoByTwoGrid_FoldsTwoHonestRows()
    {
        var (service, cache) = Build();
        await SeedAsync(cache, 256);

        var result = await service.WalkForwardAsync(Study());

        result.IsCompleted.Should().BeTrue();
        result.Request.Folds.Should().Be(2);
        result.Rows.Should().HaveCount(2);
        // The 256-bar window carves into two 128-bar segments, each split
        // 64/64 into its in-sample head and out-of-sample tail.
        for (var fold = 0; fold < 2; fold++)
        {
            var row = result.Rows[fold];
            row.Fold.Should().Be(fold + 1);
            row.InSampleBars.Should().Be(64);
            row.OutOfSampleBars.Should().Be(64);
            // The chosen pair rides the study's own axes (the honest membership).
            Study().FirstAxis.Values.Should().Contain(row.FirstValue);
            Study().SecondAxis.Values.Should().Contain(row.SecondValue);
            // The out-of-sample drawdown is the chosen replay's own fact.
            row.OutOfSampleMaxDrawdown.Should().BeGreaterThanOrEqualTo(0m);
        }

        // The aggregates fold from the rows themselves.
        result.TotalOutOfSampleNet.Should().Be(result.Rows.Sum(row => row.OutOfSampleNet));
        result.TotalInSampleNet.Should().Be(result.Rows.Sum(row => row.InSampleNet));
        result.WinningOutOfSampleFolds.Should().Be(result.Rows.Count(row => row.OutOfSampleNet > 0m));
    }

    [Fact]
    public async Task Study_SameStudyTwice_ProducesByteIdenticalCanonicalText()
    {
        var (service, cache) = Build();
        await SeedAsync(cache, 256);

        var first = await service.WalkForwardAsync(Study());
        var second = await service.WalkForwardAsync(Study());

        first.ToCanonicalText().Should().Be(second.ToCanonicalText());
        first.Rows.Should().HaveCount(second.Rows.Count);
        for (var i = 0; i < first.Rows.Count; i++)
        {
            first.Rows[i].Should().Be(second.Rows[i]);
        }
    }

    [Fact]
    public void Study_CanonicalText_CarriesTheEchoTheAxesAndEveryFold()
    {
        // The canonical text folds from the result alone: the golden facts
        // ride a completed study's own shape (the byte-stable serialization).
        var request = Study();
        var result = new BacktestWalkForwardResult(
            BacktestWalkForwardOutcome.Completed,
            request,
            [
                new BacktestWalkForwardRow(
                    Fold: 1,
                    InSampleBars: 64,
                    OutOfSampleBars: 64,
                    FirstValue: 0.5m,
                    SecondValue: 1m,
                    InSampleNet: 10m,
                    OutOfSampleNet: 4m,
                    OutOfSampleMaxDrawdown: 2m),
            ]);

        var text = result.ToCanonicalText();

        text.Should().StartWith("BacktestWalkForwardReport v1");
        text.Should().Contain("StrategyId=" + SampleCrossStrategy.StrategyId);
        text.Should().Contain("AdapterId=" + DemoAdapter);
        text.Should().Contain("Symbol=BTC/USDT");
        text.Should().Contain("Interval=H1");
        text.Should().Contain("Bars=256");
        text.Should().Contain("Seed=7");
        text.Should().Contain("Folds=2");
        text.Should().Contain("Outcome=Completed");
        text.Should().Contain("Axis[0] parameter=LongNotionalFraction values=0.5,0.25");
        text.Should().Contain("Axis[1] parameter=SlippageValue values=1,2");
        text.Should().Contain("Fold[1] isBars=64 oosBars=64 first=0.5 second=1 isNet=10 oosNet=4 oosDrawdown=2");
    }

    [Fact]
    public async Task Study_CancelAfterFirstFold_KeepsTheCompletedFold_NeverMidFold()
    {
        var (service, cache) = Build();
        await SeedAsync(cache, 256);

        var events = new List<BacktestRunEvent>();
        using var source = new CancellationTokenSource();
        using var subscription = service.Events.Subscribe(
            @event =>
            {
                events.Add(@event);
                if (@event.Kind == BacktestRunEventKind.Completed)
                {
                    // The stop fires the moment the FIRST fold's
                    // out-of-sample replay completes: the boundary check
                    // between folds must land there — the second fold never
                    // starts, nothing half-fold can exist.
                    source.Cancel();
                }
            },
            ex => throw ex);

        var result = await service.WalkForwardAsync(Study(), source.Token);

        result.Outcome.Should().Be(BacktestWalkForwardOutcome.Cancelled);
        result.Rows.Should().HaveCount(1, "the first fold's whole pass had completed, the second never started");
        result.Rows[0].Fold.Should().Be(1);
        result.Rows[0].InSampleBars.Should().Be(64);
        result.Rows[0].OutOfSampleBars.Should().Be(64);

        // The in-sample optimization replays are SILENT: only the first
        // fold's out-of-sample replay published — Started + Completed, no
        // progress line (64 bars sit under the replay's stride).
        events.Should().HaveCount(2);
        events[0].Kind.Should().Be(BacktestRunEventKind.Started);
        events[1].Kind.Should().Be(BacktestRunEventKind.Completed);
    }

    [Fact]
    public async Task Study_CancelledDuringLoad_YieldsCancelledWithZeroRows_NoEvents()
    {
        var (service, cache) = Build();
        await SeedAsync(cache, 256);
        cache.HoldNewestRead();

        var events = new List<BacktestRunEvent>();
        using var source = new CancellationTokenSource();
        using var subscription = service.Events.Subscribe(events.Add, ex => throw ex);

        var study = service.WalkForwardAsync(Study(), source.Token);
        await cache.WhenNewestReadArrives;
        source.Cancel();
        cache.ReleaseHold();

        var result = await study;

        result.Outcome.Should().Be(BacktestWalkForwardOutcome.Cancelled);
        result.Rows.Should().BeEmpty("no fold ever started — the load never answered");
        events.Should().BeEmpty("the stream carries per-run events only; the load publishes none");
    }

    [Fact]
    public async Task Study_ShortWindow_ThrowsBeforeAnyReplay()
    {
        var (service, cache) = Build();
        await SeedAsync(cache, 64);

        var events = new List<BacktestRunEvent>();
        using var subscription = service.Events.Subscribe(events.Add, ex => throw ex);

        // 64 bars / 2 folds = 32-bar segments: one bar short of the two
        // 32-bar halves — the carve refuses before any replay runs.
        var refuse = async () => await service.WalkForwardAsync(Study());

        (await refuse.Should().ThrowAsync<ArgumentOutOfRangeException>())
            .WithMessage("*each fold needs at least 64 bars*");
        events.Should().BeEmpty("nothing replayed, nothing published");
    }

    #endregion End to End

    #region Single-Flight Gate

    [Fact]
    public async Task Run_WhileWalkForwardInFlight_IsRefused()
    {
        var (service, cache) = Build();
        await SeedAsync(cache, 256);
        cache.HoldNewestRead();

        var study = service.WalkForwardAsync(Study(), CancellationToken.None);
        await cache.WhenNewestReadArrives;

        var refuse = async () => await service.RunAsync(Template(bars: 64));
        (await refuse.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*in flight*");

        cache.ReleaseHold();
        var result = await study;
        result.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task Study_WhileRunInFlight_IsRefused()
    {
        var (service, cache) = Build();
        await SeedAsync(cache, 64);
        cache.HoldNewestRead();

        var run = service.RunAsync(Template(bars: 64), CancellationToken.None);
        await cache.WhenNewestReadArrives;

        var refuse = async () => await service.WalkForwardAsync(Study());
        (await refuse.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*in flight*");

        cache.ReleaseHold();
        var result = await run;
        result.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task Study_WhileWalkForwardInFlight_IsRefused()
    {
        var (service, cache) = Build();
        await SeedAsync(cache, 256);
        cache.HoldNewestRead();

        var first = service.WalkForwardAsync(Study(), CancellationToken.None);
        await cache.WhenNewestReadArrives;

        var refuse = async () => await service.WalkForwardAsync(Study());
        (await refuse.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*in flight*");

        cache.ReleaseHold();
        var result = await first;
        result.IsCompleted.Should().BeTrue();
    }

    #endregion Single-Flight Gate

    #region Refusals

    [Fact]
    public async Task Study_UnknownStrategy_IsRefused()
    {
        var (service, cache) = Build();
        await SeedAsync(cache, 256);

        var refuse = async () => await service.WalkForwardAsync(Study(template: Template(bars: 256) with
        {
            StrategyId = "no-such-strategy",
        }));

        (await refuse.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*unknown to the catalog*");
    }

    #endregion Refusals
}
