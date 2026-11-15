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
/// The Monte Carlo sampler's facts (T-254): the closed trades extract from a
/// report's own fill sequence with the ledger's exact average-price
/// bookkeeping (an extension re-averages the whole new size, a partial close
/// never re-averages the remainder, a flip stamps its remainder at its own
/// fill), every path bootstrap-redraws the whole sequence with replacement
/// through the seeded generator, the percentiles fold nearest-rank, and the
/// same report over the same seed folds the byte-identical distribution.
/// Everything invalid is refused before any path samples; a cancelled
/// sampling answers the cancelled envelope, never a half-fold.
/// </summary>
public sealed class BacktestMonteCarloTests
{
    #region Fixture

    private static readonly DateTimeOffset Base = new(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);

    private static readonly Symbol BtcUsdt = Symbol.Parse("BTC/USDT");

    /// <summary>The demo adapter identity the fixture caches and books under.</summary>
    private const string DemoAdapter = "demo";

    /// <summary>The simulated starting quote balance (the report's own echo pins it too).</summary>
    private const decimal StartQuote = 1_000_000m;

    /// <summary>The zero-fee schedule: the fills' prices stay exactly the caller's.</summary>
    private static IFeeScheduleProvider ZeroFees()
    {
        var provider = Substitute.For<IFeeScheduleProvider>();
        provider.GetSchedule(DemoAdapter).Returns(new FeeSchedule(0m, 0m, 0m));
        return provider;
    }

    /// <summary>A ledger whose fills carry the exact bookkeeping a replay books.</summary>
    private static BacktestLedger Ledger() =>
        new(DemoAdapter, BtcUsdt, ZeroFees(), StartQuote);

    /// <summary>
    /// One completed report over the given fill sequence: the ledger books
    /// the fills itself (the extraction replays the same bookkeeping), the
    /// statistics fold from the ledger, and the equity curve carries one
    /// mark so the report shape stays complete.
    /// </summary>
    private static BacktestReport ReportOf(BacktestLedger ledger, params (OrderSide Side, decimal Quantity, decimal Price, int Hour)[] legs)
    {
        foreach (var (side, quantity, price, hour) in legs)
        {
            ledger.FillSide(side, quantity, price, Base.AddHours(hour)).Should().BeTrue("the fixture's book carries every leg");
        }

        var request = new BacktestRunRequest
        {
            StrategyId = SampleCrossStrategy.StrategyId,
            AdapterId = DemoAdapter,
            Symbol = BtcUsdt,
            Interval = TimeFrame.H1,
            Bars = BacktestRunRequest.MinBars,
            Seed = 7,
            InitialQuoteAmount = StartQuote,
        };
        var equity = new[] { new BacktestEquityPoint(Base, StartQuote), new BacktestEquityPoint(Base.AddHours(1), ledger.MarkEquity(100m)) };
        return new BacktestReport(request, equity, ledger.Fills, ledger.Refusals, ledger.ComputeStatistics(100m));
    }

    /// <summary>One journal-shaped fill the extraction reads, with explicit quantity and price knobs.</summary>
    private static Order FillOf(decimal filledQuantity, decimal price) => new()
    {
        Id = "fixture-fill",
        ExchangeId = DemoAdapter,
        Symbol = BtcUsdt,
        Side = OrderSide.Buy,
        Type = OrderType.Market,
        Quantity = new Quantity(Math.Max(filledQuantity, 0m)),
        Price = new Price(price),
        TimeInForce = TimeInForce.GTC,
        Status = OrderStatus.Filled,
        FilledQuantity = new Quantity(filledQuantity),
        CreatedAt = Base,
    };

    private static (BacktestService Service, TestCandleCache Cache) Build()
    {
        var cache = new TestCandleCache();
        var service = new BacktestService(
            new TestCatalog(new SampleCrossStrategy()),
            cache,
            ZeroFees(),
            NullLoggerFactory.Instance);
        return (service, cache);
    }

    private sealed class TestCatalog(params IStrategy[] strategies) : IStrategyCatalog
    {
        private readonly IStrategy[] _strategies = strategies;

        public IReadOnlyList<IStrategy> All => _strategies;

        public IStrategy? Get(string strategyId) => _strategies.FirstOrDefault(strategy => strategy.Id == strategyId);
    }

    #endregion Fixture

    #region Extraction

    [Fact]
    public void ExtractTradePnLs_BooksTheLedgersExactSequence_OpenExtendPartialCloseFlip()
    {
        var ledger = Ledger();
        // Open at 100, extend to an average of 105, close one at 120 (+15),
        // close the remainder at 90 (-15 against the long's average), the
        // flip stamps the short at 90 and the cover at 80 (+10): the exact
        // realized sequence the ledger booked.
        var report = ReportOf(
            ledger,
            (OrderSide.Buy, 1m, 100m, 0),
            (OrderSide.Buy, 1m, 110m, 1),
            (OrderSide.Sell, 1m, 120m, 2),
            (OrderSide.Sell, 2m, 90m, 3),
            (OrderSide.Buy, 1m, 80m, 4));

        var pnls = BacktestMonteCarlo.ExtractTradePnLs(report.Fills);

        pnls.Should().Equal([15m, -15m, 10m]);
        // The extraction replays the ledger's own bookkeeping: the totals
        // agree exactly, the resampled sequence IS the booked sequence.
        pnls.Sum().Should().Be(report.Statistics.RealizedTotal);
        pnls.Count.Should().Be(report.Statistics.Trades);
    }

    [Fact]
    public void ExtractTradePnLs_PartialClose_NeverReAveragesTheRemainder()
    {
        var ledger = Ledger();
        // Buy 2 at 10, sell one at 12 (+2), sell the remainder at 13 (+3):
        // both closes ride the SAME 10 average.
        var report = ReportOf(
            ledger,
            (OrderSide.Buy, 2m, 10m, 0),
            (OrderSide.Sell, 1m, 12m, 1),
            (OrderSide.Sell, 1m, 13m, 2));

        var pnls = BacktestMonteCarlo.ExtractTradePnLs(report.Fills);

        pnls.Should().Equal([2m, 3m]);
        pnls.Sum().Should().Be(report.Statistics.RealizedTotal);
    }

    [Fact]
    public void ExtractTradePnLs_RefusesAMalformedSequence()
    {
        var nullFill = () => BacktestMonteCarlo.ExtractTradePnLs([null!]);
        nullFill.Should().Throw<ArgumentNullException>();

        var emptyList = () => BacktestMonteCarlo.ExtractTradePnLs(new List<Order>());
        emptyList.Should().NotThrow("an empty sequence extracts an empty trade list honestly");

        // A fill that executed nothing poisons the sequence.
        var zeroQty = () => BacktestMonteCarlo.ExtractTradePnLs([FillOf(0m, 100m)]);
        zeroQty.Should().Throw<ArgumentOutOfRangeException>();

        // A non-positive execution price poisons the sequence.
        var zeroPrice = () => BacktestMonteCarlo.ExtractTradePnLs([FillOf(1m, 0m)]);
        zeroPrice.Should().Throw<ArgumentOutOfRangeException>();
    }

    #endregion Extraction

    #region Percentiles

    [Fact]
    public void Compute_DegenerateSequence_AllPathsFoldTheSameDistribution()
    {
        var ledger = Ledger();
        // One closed trade (+10): every path redraws exactly that trade, so
        // the distribution is degenerate — all five percentiles equal, the
        // drawdowns all zero, no path ends below the start.
        var report = ReportOf(
            ledger,
            (OrderSide.Buy, 1m, 100m, 0),
            (OrderSide.Sell, 1m, 110m, 1));

        var distribution = BacktestMonteCarlo.Compute(new BacktestMonteCarloRequest
        {
            Report = report,
            Paths = 100,
            Seed = 5,
        });

        distribution.Trades.Should().Be(1);
        distribution.Paths.Should().Be(100);
        distribution.Seed.Should().Be(5);
        var expected = StartQuote + 10m;
        distribution.FinalEquity.P5.Should().Be(expected);
        distribution.FinalEquity.P25.Should().Be(expected);
        distribution.FinalEquity.Median.Should().Be(expected);
        distribution.FinalEquity.P75.Should().Be(expected);
        distribution.FinalEquity.P95.Should().Be(expected);
        distribution.MaxDrawdown.P5.Should().Be(0m);
        distribution.MaxDrawdown.Median.Should().Be(0m);
        distribution.MaxDrawdown.P95.Should().Be(0m);
        distribution.PathsBelowStart.Should().Be(0);
        distribution.InitialEquity.Should().Be(StartQuote);
    }

    [Fact]
    public void Compute_SameSeed_FoldsTheByteIdenticalDistribution_DifferentSeed_DoesNot()
    {
        var ledger = Ledger();
        // Two distinct trades (+10, -5): the reshuffle has something to
        // reorder, so the seed is the distribution's only knob.
        var report = ReportOf(
            ledger,
            (OrderSide.Buy, 1m, 100m, 0),
            (OrderSide.Sell, 1m, 110m, 1),
            (OrderSide.Buy, 1m, 100m, 2),
            (OrderSide.Sell, 1m, 95m, 3));

        var first = BacktestMonteCarlo.Compute(new BacktestMonteCarloRequest { Report = report, Paths = 500, Seed = 1 });
        var second = BacktestMonteCarlo.Compute(new BacktestMonteCarloRequest { Report = report, Paths = 500, Seed = 1 });
        var other = BacktestMonteCarlo.Compute(new BacktestMonteCarloRequest { Report = report, Paths = 500, Seed = 2 });

        first.ToCanonicalText().Should().Be(second.ToCanonicalText(), "the same report over the same seed folds byte-identically");
        other.ToCanonicalText().Should().NotBe(first.ToCanonicalText(), "a different seed reshuffles differently");
        // The percentiles order honestly.
        first.FinalEquity.P5.Should().BeLessThanOrEqualTo(first.FinalEquity.P25);
        first.FinalEquity.P25.Should().BeLessThanOrEqualTo(first.FinalEquity.Median);
        first.FinalEquity.Median.Should().BeLessThanOrEqualTo(first.FinalEquity.P75);
        first.FinalEquity.P75.Should().BeLessThanOrEqualTo(first.FinalEquity.P95);
    }

    [Fact]
    public void Compute_CanonicalText_CarriesTheEchoTheKnobsAndBothBundles()
    {
        var ledger = Ledger();
        var report = ReportOf(
            ledger,
            (OrderSide.Buy, 1m, 100m, 0),
            (OrderSide.Sell, 1m, 110m, 1));

        var distribution = BacktestMonteCarlo.Compute(new BacktestMonteCarloRequest { Report = report, Paths = 200, Seed = 9 });
        var text = distribution.ToCanonicalText();

        text.Should().StartWith("BacktestMonteCarloReport v1");
        text.Should().Contain("StrategyId=" + SampleCrossStrategy.StrategyId);
        text.Should().Contain("AdapterId=" + DemoAdapter);
        text.Should().Contain("Symbol=BTC/USDT");
        text.Should().Contain("Interval=H1");
        text.Should().Contain("Paths=200");
        text.Should().Contain("Seed=9");
        text.Should().Contain("Trades=1");
        text.Should().Contain("InitialEquity=" + StartQuote.ToString(System.Globalization.CultureInfo.InvariantCulture));
        text.Should().Contain("FinalEquity p5=");
        text.Should().Contain("MaxDrawdown p5=");
        text.Should().Contain("PathsBelowStart=");
    }

    #endregion Percentiles

    #region Validation

    [Fact]
    public void Validate_RefusesEveryInvalidShape_NeverDefaults()
    {
        var ledger = Ledger();
        var report = ReportOf(
            ledger,
            (OrderSide.Buy, 1m, 100m, 0),
            (OrderSide.Sell, 1m, 110m, 1));

        var nullReport = () => new BacktestMonteCarloRequest { Report = null!, Paths = 100 }.Validate();
        nullReport.Should().Throw<ArgumentNullException>();

        var tooFewPaths = () => new BacktestMonteCarloRequest { Report = report, Paths = 99 }.Validate();
        tooFewPaths.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*between*");

        var tooManyPaths = () => new BacktestMonteCarloRequest { Report = report, Paths = 100_001 }.Validate();
        tooManyPaths.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*between*");

        // The work budget (paths x trades) refuses the pathological fold:
        // 210 trades over the maximum path count would draw 21M times.
        var crowded = Ledger();
        var legs = new List<(OrderSide, decimal, decimal, int)>();
        for (var trip = 0; trip < 210; trip++)
        {
            legs.Add((OrderSide.Buy, 1m, 100m, trip * 2));
            legs.Add((OrderSide.Sell, 1m, 110m, (trip * 2) + 1));
        }

        var crowdedReport = ReportOf(crowded, [.. legs]);
        crowdedReport.Statistics.Trades.Should().Be(210);
        var budget = () => new BacktestMonteCarloRequest { Report = crowdedReport, Paths = 100_000 }.Validate();
        budget.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*20000000 draws is the budget*");

        // A report with no closed trade has no sequence to resample.
        var openOnly = Ledger();
        openOnly.FillSide(OrderSide.Buy, 1m, 100m, Base).Should().BeTrue();
        var openReport = ReportOf(openOnly);
        var noTrades = () => new BacktestMonteCarloRequest { Report = openReport, Paths = 100 }.Validate();
        noTrades.Should().Throw<InvalidOperationException>().WithMessage("*closed no round trips*");

        // A statistics disagreement is a malformed report, never a guess.
        var mismatched = new BacktestStatistics(
            Fills: 2,
            Trades: 5,
            Wins: 1,
            Losses: 0,
            RealizedTotal: 10m,
            CommissionTotal: 0m,
            Volume: 200m,
            MaxDrawdown: 0m,
            PeakEquity: StartQuote + 10m,
            FinalEquity: StartQuote + 10m,
            Unrealized: 0m,
            AverageWinner: 10m,
            AverageLoser: null,
            WinRate: 1m,
            ProfitFactor: null);
        var disagreeing = new BacktestReport(report.Request, report.Equity, report.Fills, report.Refusals, mismatched);
        var disagreement = () => new BacktestMonteCarloRequest { Report = disagreeing, Paths = 100 }.Validate();
        disagreement.Should().Throw<InvalidOperationException>().WithMessage("*disagree*");
    }

    #endregion Validation

    #region Service Lane

    [Fact]
    public async Task MonteCarloAsync_SamplesACompletedRun_DistributionAgreesWithTheReport()
    {
        var (service, cache) = Build();
        await SeedAsync(cache, 600);

        var run = await service.RunAsync(Template());
        run.IsCompleted.Should().BeTrue();
        run.Report!.Statistics.Trades.Should().BeGreaterThan(0, "the pure rise keeps the strategy long and the dip closes it");

        var result = await service.MonteCarloAsync(new BacktestMonteCarloRequest
        {
            Report = run.Report,
            Paths = 1_000,
            Seed = 3,
        });

        result.IsCompleted.Should().BeTrue();
        var distribution = result.Distribution!;
        distribution.Trades.Should().Be(run.Report.Statistics.Trades);
        distribution.Paths.Should().Be(1_000);
        distribution.InitialEquity.Should().Be(run.Report.Request.InitialQuoteAmount);
        distribution.FinalEquity.P5.Should().BeLessThanOrEqualTo(distribution.FinalEquity.Median);
        distribution.FinalEquity.Median.Should().BeLessThanOrEqualTo(distribution.FinalEquity.P95);
    }

    [Fact]
    public async Task MonteCarloAsync_StopTokenFired_AnswersTheCancelledEnvelope()
    {
        var ledger = Ledger();
        var report = ReportOf(
            ledger,
            (OrderSide.Buy, 1m, 100m, 0),
            (OrderSide.Sell, 1m, 110m, 1));

        var (service, _) = Build();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await service.MonteCarloAsync(
            new BacktestMonteCarloRequest { Report = report, Paths = 1_000 },
            cancellation.Token);

        result.Outcome.Should().Be(BacktestMonteCarloOutcome.Cancelled);
        result.Distribution.Should().BeNull("the stop is the outcome, never a half-fold");
        result.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task MonteCarloAsync_InvalidShape_IsRefusedBeforeTheLane()
    {
        var (service, _) = Build();

        var refuse = async () => await service.MonteCarloAsync(
            new BacktestMonteCarloRequest { Report = null!, Paths = 100 });

        await refuse.Should().ThrowAsync<ArgumentNullException>();
    }

    /// <summary>One deterministic bar of the fixture's series: a pure rise —
    /// every bar's close tops the running high, so the demo strategy stands
    /// long throughout — with the final two bars dipping below the whole
    /// history. The first dip's close bottoms the running low straight from
    /// the long, so the exit fires and the report books one honest round
    /// trip for the sampling to resample.</summary>
    private static Bar TestBar(int index, int total)
    {
        if (index >= total - 2)
        {
            var open = 95m - (total - 1 - index);
            return new Bar(
                Time: Base.AddHours(index),
                Open: open,
                High: open + 1m,
                Low: open - 2m,
                Close: open - 1m,
                Volume: 10m);
        }

        var open2 = 100m + index * 0.5m;
        return new Bar(
            Time: Base.AddHours(index),
            Open: open2,
            High: open2 + 1m,
            Low: open2 - 0.5m,
            Close: open2 + 0.5m,
            Volume: 10m);
    }

    private static IReadOnlyList<Bar> Series(int count) =>
        Enumerable.Range(0, count).Select(index => TestBar(index, count)).ToList();

    private static Task SeedAsync(TestCandleCache cache, int count) =>
        cache.WriteThroughAsync(DemoAdapter, BtcUsdt, TimeFrame.H1, Series(count));

    /// <summary>The fixture's template: the demo adapter's H1 series over the fixture's own start balance.</summary>
    private static BacktestRunRequest Template(
        int bars = 600,
        int seed = 7,
        decimal slippage = 1m,
        decimal fraction = 0.25m) => new()
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

    #endregion Service Lane
}
