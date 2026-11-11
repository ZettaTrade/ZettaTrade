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
using ZettaTrade.Infrastructure.Exchange;
using ZettaTrade.Infrastructure.Fees;
using ZettaTrade.Plugins.SampleStrategy;
using ZettaTrade.Strategies;

/// <summary>
/// The harness's end-to-end facts (T-113): a run loads its window from the
/// cache (the honest walk-back when the cache serves less), replays it
/// through a real T-111 host over the sample strategy, journals the stance
/// changes through the paper-parity ledger, folds the report, and ends
/// deterministically — the same request over the same cache is
/// <see cref="BacktestReport.ToCanonicalText"/>-identical, a zero-slip
/// replay is seed-independent, an unknown strategy is refused, concurrent
/// starts refuse the second, a cancelled run yields the envelope with no
/// report, the event order is Started → Progress… → Completed (the report
/// only on Completed), and an empty cache is the honest zero-activity
/// baseline. Every exercise runs OFFLINE: the fake candle cache is the
/// only data source.
/// </summary>
public sealed class BacktestServiceTests
{
    #region Fixture

    private static readonly DateTimeOffset Base = new(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);

    private static readonly Symbol BtcUsdt = Symbol.Parse("BTC/USDT");

    /// <summary>The demo adapter identity the fixture caches under.</summary>
    private const string DemoAdapter = "demo";

    /// <summary>
    /// One deterministic bar of the shared sawtooth series: a half-rising,
    /// half-falling path (the demo strategy crosses over it), closes lead
    /// open by one tick so CLOSED candles carry the walk.
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

    /// <summary>The full deterministic sawtooth, oldest first.</summary>
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

    /// <summary>Zero-fee schedule: the determinism facts keep the math neutral.</summary>
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

    private static BacktestRunRequest Request(
        int bars = BacktestRunRequest.DefaultBars,
        int seed = BacktestRunRequest.DefaultSeed,
        decimal slippage = BacktestRunRequest.DefaultSlippageValue) => new()
        {
            StrategyId = SampleCrossStrategy.StrategyId,
            AdapterId = DemoAdapter,
            Symbol = BtcUsdt,
            Interval = TimeFrame.H1,
            Bars = bars,
            Seed = seed,
            SlippageValue = slippage,
        };

    /// <summary>Seed the fixture's cache with the sawtooth (the write-through surface).</summary>
    private static Task SeedAsync(TestCandleCache cache, int count, string adapterId = DemoAdapter) =>
        cache.WriteThroughAsync(adapterId, BtcUsdt, TimeFrame.H1, Series(count));

    #endregion Fixture

    [Fact]
    public async Task Run_SameRequestSameCache_ProducesByteIdenticalCanonicalText()
    {
        var (service, cache) = Build();
        await SeedAsync(cache, 128);

        var first = await service.RunAsync(Request(seed: 7));
        var second = await service.RunAsync(Request(seed: 7));

        first.IsCompleted.Should().BeTrue();
        second.IsCompleted.Should().BeTrue();
        first.Report!.ToCanonicalText().Should().Be(second.Report!.ToCanonicalText());
    }

    [Fact]
    public async Task Run_ZeroSlippage_IsSeedIndependent()
    {
        var (service, cache) = Build();
        await SeedAsync(cache, 128);

        var seededOne = await service.RunAsync(Request(seed: 1));
        var seededOther = await service.RunAsync(Request(seed: 999_999));

        // The serialized request carries its own seed line, so the texts
        // differ by design; the deterministic CONTENT is the same shape:
        seededOther.Report!.Statistics.Should().Be(seededOne.Report!.Statistics,
            "zero slippage removes the only seeded draw: every seed replays the same curve");
        seededOne.Report.Fills.Select(fill => (fill.CreatedAt, fill.Price.Value)).Should()
            .Equal(seededOther.Report.Fills.Select(fill => (fill.CreatedAt, fill.Price.Value)));
        seededOne.Report.Equity.Should().Equal(seededOther.Report.Equity);
    }

    [Fact]
    public async Task Run_UnknownStrategy_IsRefused_InvalidOperationException()
    {
        var (service, cache) = Build();
        await SeedAsync(cache, 128);

        var refuse = async () => await service.RunAsync(Request() with { StrategyId = "ghost-strategy" });

        (await refuse.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*ghost-strategy*");
    }

    [Fact]
    public async Task Run_ConcurrentStarts_SecondRunIsRefused()
    {
        var (service, cache) = Build();
        await SeedAsync(cache, 64);
        cache.HoldNewestRead();

        var first = service.RunAsync(Request());
        await cache.WhenNewestReadArrives;
        // The first run already holds the single-flight gate (the cache read
        // runs strictly after the gate), so the second start must refuse.
        var second = async () => await service.RunAsync(Request());
        (await second.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*in flight*");

        cache.ReleaseHold();
        var result = await first;
        result.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task Run_CancelledMidLoad_YieldsTheEnvelope_NoReport()
    {
        var (service, cache) = Build();
        await SeedAsync(cache, 64);
        cache.HoldNewestRead();
        var events = new List<BacktestRunEvent>();
        using var subscription = service.Events.Subscribe(
            events.Add,
            ex => throw ex);

        using var cancellation = new CancellationTokenSource();
        var run = service.RunAsync(Request(), cancellation.Token);
        await cache.WhenNewestReadArrives;
        cancellation.Cancel();
        cache.ReleaseHold();

        var result = await run;
        result.Should().Be(BacktestRunResult.Cancelled);
        result.Report.Should().BeNull();
        // The run never started the replay: the stop is the only event.
        events.Select(@event => @event.Kind).Should().Equal([BacktestRunEventKind.Cancelled]);
    }

    [Fact]
    public async Task Run_Events_FollowStartedProgressCompleted()
    {
        var (service, cache) = Build();
        await SeedAsync(cache, 600);
        var events = new List<BacktestRunEvent>();
        using var subscription = service.Events.Subscribe(
            events.Add,
            ex => throw ex);

        var result = await service.RunAsync(Request(bars: 400));

        result.IsCompleted.Should().BeTrue();
        var kinds = events.Select(@event => @event.Kind).ToList();
        kinds.Should().NotBeEmpty();
        kinds[0].Should().Be(BacktestRunEventKind.Started);
        kinds[^1].Should().Be(BacktestRunEventKind.Completed);
        kinds.Count(kind => kind == BacktestRunEventKind.Started).Should().Be(1);
        kinds.Count(kind => kind == BacktestRunEventKind.Completed).Should().Be(1);
        kinds.Should().Contain(BacktestRunEventKind.Progress, "the 256-candle stride crosses the 400-bar target");
        // The terminal Completed event carries the very report the run returned.
        events[^1].Report.Should().NotBeNull();
        events[^1].Report!.ToCanonicalText().Should().Be(result.Report!.ToCanonicalText());
    }

    [Fact]
    public async Task Run_WalkBackPages_AssemblesTheHonestTargetWindow()
    {
        var (service, cache) = Build();
        await SeedAsync(cache, 60);
        cache.NewestPageSize = 4;
        cache.OlderPageSize = 3;

        var result = await service.RunAsync(Request(bars: 32));

        result.IsCompleted.Should().BeTrue();
        result.Report!.Equity.Should().HaveCount(32, "one equity point per replayed candle");
        // bars 28..59 arrived: the walk-back preceded the newest page exactly.
        result.Report.Equity[0].Time.Should().Be(Base.AddHours(28));
    }

    [Fact]
    public async Task Run_EmptyCache_IsTheHonestZeroActivityBaseline()
    {
        var (service, cache) = Build();
        await SeedAsync(cache, 0);

        var result = await service.RunAsync(Request(bars: 64));

        result.IsCompleted.Should().BeTrue();
        result.Report!.Statistics.Fills.Should().Be(0);
        result.Report.Statistics.Trades.Should().Be(0);
        result.Report.Equity.Should().BeEmpty();
        result.Report.Statistics.FinalEquity.Should().Be(
            PaperTradingAdapter.InitialQuoteAmount,
            "an empty cache replays nothing: the account just stands where it started");
    }
}
