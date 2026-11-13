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
/// The sweep engine's end-to-end facts (T-212): one grid sweep loads its
/// window from the cache ONCE, replays the cartesian product row-major
/// (fresh host, ledger and seeded draw per parameterization — each a full,
/// byte-stable replay), ranks the rows deterministically (net descending,
/// ties broken by the enumeration order), and answers a cooperative stop
/// at a parameterization boundary only — never mid-run — keeping every
/// row whose replay had completed. The same grid over the same cache is
/// <see cref="BacktestSweepResult.ToCanonicalText"/>-identical. The
/// single-flight gate covers the whole sweep: a run or sweep started
/// while a sweep is in flight refuses exactly as a second run would.
/// Every exercise runs OFFLINE: the fake candle cache is the only data source.
/// </summary>
public sealed class BacktestSweepTests
{
    #region Fixture

    private static readonly DateTimeOffset Base = new(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);

    private static readonly Symbol BtcUsdt = Symbol.Parse("BTC/USDT");

    /// <summary>The demo adapter identity the fixture caches under.</summary>
    private const string DemoAdapter = "demo";

    /// <summary>
    /// One deterministic bar of the shared sawtooth series (the same shape
    /// the run facts replay): a half-rising, half-falling path the demo
    /// strategy crosses over.
    /// </summary>
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

    private sealed class TestCatalog : IStrategyCatalog
    {
        public TestCatalog(params IStrategy[] strategies)
        {
            CatalogList = strategies;
        }

        private IReadOnlyList<IStrategy> CatalogList { get; }

        public IReadOnlyList<IStrategy> All => CatalogList;

        public IStrategy? Get(string strategyId) => CatalogList.FirstOrDefault(strategy => strategy.Id == strategyId);
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

    /// <summary>The sweep's template: the demo adapter's H1 series, 64 bars, a non-zero slip so both knobs matter.</summary>
    private static BacktestRunRequest Template(
        int bars = 64,
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

    private static BacktestSweepRequest Sweep(
        BacktestSweepParameter firstParameter = BacktestSweepParameter.LongNotionalFraction,
        decimal[]? firstValues = null,
        BacktestSweepParameter secondParameter = BacktestSweepParameter.SlippageValue,
        decimal[]? secondValues = null) => new()
        {
            Template = Template(),
            FirstAxis = new BacktestSweepAxis(
            firstParameter,
            firstValues ?? [0.5m, 0.25m]),
            SecondAxis = new BacktestSweepAxis(
            secondParameter,
            secondValues ?? [1m, 2m]),
        };

    private static Task SeedAsync(TestCandleCache cache, int count, string adapterId = DemoAdapter) =>
        cache.WriteThroughAsync(adapterId, BtcUsdt, TimeFrame.H1, Series(count));

    #endregion Fixture

    [Fact]
    public async Task Sweep_SameGridSameCache_ProducesByteIdenticalCanonicalText()
    {
        var (service, cache) = Build();
        await SeedAsync(cache, 128);

        var first = await service.SweepAsync(Sweep());
        var second = await service.SweepAsync(Sweep());

        first.IsCompleted.Should().BeTrue();
        second.IsCompleted.Should().BeTrue();
        // The whole sweep's canonical text: byte-identical across sweeps.
        first.ToCanonicalText().Should().Be(second.ToCanonicalText());
        // And every ranked row's own report is byte-stable in its ranked position.
        first.Rows.Should().HaveCount(second.Rows.Count);
        for (var i = 0; i < first.Rows.Count; i++)
        {
            first.Rows[i].Report.ToCanonicalText().Should().Be(second.Rows[i].Report.ToCanonicalText());
            first.Rows[i].Rank.Should().Be(second.Rows[i].Rank);
            first.Rows[i].Index.Should().Be(second.Rows[i].Index);
            first.Rows[i].FirstValue.Should().Be(second.Rows[i].FirstValue);
            first.Rows[i].SecondValue.Should().Be(second.Rows[i].SecondValue);
        }
    }

    [Fact]
    public async Task Sweep_RanksByNetDescending_AndNetIsTheEquityOverTheBalance()
    {
        var (service, cache) = Build();
        await SeedAsync(cache, 128);

        var result = await service.SweepAsync(Sweep());

        result.IsCompleted.Should().BeTrue();
        result.Rows.Should().HaveCount(4);
        result.Rows.Select(row => row.Rank).Should().Equal([1, 2, 3, 4]);
        result.Rows.Select(row => row.Net).Should().Equal(
            result.Rows.Select(row => row.Net).OrderByDescending(net => net),
            "the table is ranked, best net first");
        foreach (var row in result.Rows)
        {
            row.Net.Should().Be(row.Report.Statistics.FinalEquity - row.Report.Request.InitialQuoteAmount);
            // Each row's report carries the cell's own knob pair.
            row.Report.Request.LongNotionalFraction.Should().Be(row.FirstValue);
            row.Report.Request.SlippageValue.Should().Be(row.SecondValue);
        }
    }

    [Fact]
    public async Task Sweep_DuplicateCells_TieBreaksByEnumerationOrder()
    {
        var (service, cache) = Build();
        await SeedAsync(cache, 128);

        // A grid of four IDENTICAL parameterizations: every net equal, so
        // the ranking's only honest order left is the enumeration order.
        var result = await service.SweepAsync(Sweep(
            firstValues: [0.5m, 0.5m],
            secondValues: [1m, 1m]));

        result.IsCompleted.Should().BeTrue();
        result.Rows.Should().HaveCount(4);
        for (var i = 0; i < result.Rows.Count; i++)
        {
            result.Rows[i].Rank.Should().Be(i + 1);
            result.Rows[i].Index.Should().Be(i);
        }

        // And the duplicates were actually replayed, not deduplicated:
        // every cell's report carries its own knob pair.
        result.Rows.Select(row => row.FirstValue).Should().OnlyContain(value => value == 0.5m);
        result.Rows.Select(row => row.SecondValue).Should().OnlyContain(value => value == 1m);
    }

    [Fact]
    public async Task Sweep_CanonicalText_CarriesTheHeaderTheAxesAndEveryRow()
    {
        var (service, cache) = Build();
        await SeedAsync(cache, 128);

        var result = await service.SweepAsync(Sweep());
        var text = result.ToCanonicalText();

        text.Should().StartWith("BacktestSweepReport v1");
        text.Should().Contain("StrategyId=" + SampleCrossStrategy.StrategyId);
        text.Should().Contain("Axis[0] parameter=LongNotionalFraction values=0.5,0.25");
        text.Should().Contain("Axis[1] parameter=SlippageValue values=1,2");
        text.Should().Contain("Outcome=Completed");
        text.Should().Contain("Row[1] index=0");
        text.Should().Contain("Row[2] index=");
        text.Should().Contain("Row[3] index=");
        text.Should().Contain("Row[4] index=");
    }

    [Fact]
    public async Task Sweep_LoadsTheWindowOnce_ReplaysEveryRow()
    {
        var (service, cache) = Build();
        await SeedAsync(cache, 128);

        var result = await service.SweepAsync(Sweep());

        result.IsCompleted.Should().BeTrue();
        result.Rows.Should().HaveCount(4);
        // The window loaded exactly once: every parameterization replayed
        // the SAME bars, the load's cost stays one walk-back.
        cache.NewestReadCount.Should().Be(1);
        // Every row replayed the full window (the same honest bar count —
        // the template's 64-bar target over the 128-bar series).
        result.Rows.Select(row => row.Report.Equity.Count)
            .Should().OnlyContain(count => count == 64);
    }

    [Fact]
    public async Task Sweep_CancelAfterFirstRow_KeepsTheCompletedRows_NeverMidRun()
    {
        var (service, cache) = Build();
        await SeedAsync(cache, 128);

        var completed = new List<BacktestRunEvent>();
        using var source = new CancellationTokenSource();
        using var subscription = service.Events.Subscribe(
            @event =>
            {
                if (@event.Kind == BacktestRunEventKind.Completed)
                {
                    completed.Add(@event);
                    // The stop request fires the moment the FIRST row's
                    // replay completes: the boundary check between
                    // parameterizations must land there — the second row
                    // never starts, nothing half-run can exist.
                    source.Cancel();
                }
            },
            ex => throw ex);

        var result = await service.SweepAsync(Sweep(), source.Token);

        result.Outcome.Should().Be(BacktestSweepOutcome.Cancelled);
        result.IsCompleted.Should().BeFalse();
        result.Rows.Should().HaveCount(1, "the first parameterization completed before the stop");
        result.Rows[0].Rank.Should().Be(1);
        result.Rows[0].Index.Should().Be(0);
        result.Rows[0].Report.Request.LongNotionalFraction.Should().Be(0.5m);
        result.Rows[0].Report.Request.SlippageValue.Should().Be(1m);
        // The row is a complete, byte-stable report (never half-run).
        result.Rows[0].Report.Equity.Should().HaveCount(64);
        completed.Should().HaveCount(1);
    }

    [Fact]
    public async Task Sweep_CancelledMidLoad_YieldsCancelledWithZeroRows_NoEvents()
    {
        var (service, cache) = Build();
        await SeedAsync(cache, 64);
        cache.HoldNewestRead();

        var events = new List<BacktestRunEvent>();
        using var subscription = service.Events.Subscribe(
            events.Add,
            ex => throw ex);

        using var cancellation = new CancellationTokenSource();
        var sweep = service.SweepAsync(Sweep(), cancellation.Token);
        await cache.WhenNewestReadArrives;
        cancellation.Cancel();
        cache.ReleaseHold();

        var result = await sweep;
        result.Outcome.Should().Be(BacktestSweepOutcome.Cancelled);
        result.Rows.Should().BeEmpty();
        // The sweep never started a parameterization: the stream carries
        // per-run events only, and none went out.
        events.Should().BeEmpty();
    }

    [Fact]
    public async Task Sweep_UnknownStrategy_IsRefused()
    {
        var (service, cache) = Build();
        await SeedAsync(cache, 128);

        var refuse = async () => await service.SweepAsync(
            Sweep() with { Template = Template() with { StrategyId = "ghost-strategy" } });

        (await refuse.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*ghost-strategy*");
    }

    [Fact]
    public async Task Run_WhileSweepInFlight_IsRefused()
    {
        var (service, cache) = Build();
        await SeedAsync(cache, 64);
        cache.HoldNewestRead();

        var sweep = service.SweepAsync(Sweep(), CancellationToken.None);
        await cache.WhenNewestReadArrives;

        var refuse = async () => await service.RunAsync(Template());
        (await refuse.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*in flight*");

        cache.ReleaseHold();
        var result = await sweep;
        result.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task Sweep_WhileRunInFlight_IsRefused()
    {
        var (service, cache) = Build();
        await SeedAsync(cache, 64);
        cache.HoldNewestRead();

        var run = service.RunAsync(Template(), CancellationToken.None);
        await cache.WhenNewestReadArrives;

        var refuse = async () => await service.SweepAsync(Sweep());
        (await refuse.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*in flight*");

        cache.ReleaseHold();
        var result = await run;
        result.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task Sweep_SecondSweepWhileFirstInFlight_IsRefused()
    {
        var (service, cache) = Build();
        await SeedAsync(cache, 64);
        cache.HoldNewestRead();

        var first = service.SweepAsync(Sweep(), CancellationToken.None);
        await cache.WhenNewestReadArrives;

        var refuse = async () => await service.SweepAsync(Sweep());
        (await refuse.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*in flight*");

        cache.ReleaseHold();
        var result = await first;
        result.IsCompleted.Should().BeTrue();
    }
}
