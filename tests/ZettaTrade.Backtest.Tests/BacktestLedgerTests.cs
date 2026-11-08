namespace ZettaTrade.Backtest.Tests;

using System;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Fees;

/// <summary>
/// The simulated ledger's paper-parity facts (T-113): the exact T-051-corrected
/// bookkeeping replayed on candle times — weighted-average entry over the
/// whole new size on an extension, a partial close never re-averages the
/// remainder, a flip stamps its fresh position at its own fill, a refused
/// fill never leaves a phantom (the book untouched, the refusal visible),
/// and commissions stamp from the venue's schedule with the honest unknown
/// keeping zero. Every number derives from the fill sequence — the replay's
/// only clock is the fill's candle time.
/// </summary>
public sealed class BacktestLedgerTests
{
    private static readonly DateTimeOffset Base = new(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The taker-only schedule: 10 bps, no minimum (101.5 notional → 1.015 rounded commission).</summary>
    private (BacktestLedger Ledger, IFeeScheduleProvider Provider) Ledger(decimal quote = 1_000_000m)
    {
        var provider = Substitute.For<IFeeScheduleProvider>();
        provider.GetSchedule("demo").Returns(new FeeSchedule(0m, 10m, 0m));
        return (new BacktestLedger("demo", Symbol.Parse("BTC/USDT"), provider, quote), provider);
    }

    /// <summary>The schedule-less registry: the honest unknown (no fabricated zero).</summary>
    private static BacktestLedger ScheduleLessLedger(decimal quote = 1_000_000m)
    {
        var provider = Substitute.For<IFeeScheduleProvider>();
        provider.GetSchedule("demo").Returns((FeeSchedule?)null);
        return new BacktestLedger("demo", Symbol.Parse("BTC/USDT"), provider, quote);
    }

    [Fact]
    public void Open_Extend_WeightedAverageEntry_OverTheWholeNewSize()
    {
        var (ledger, _) = Ledger();

        ledger.FillSide(OrderSide.Buy, 1m, 10m, Base).Should().BeTrue("the balance carries the first buy");
        ledger.FillSide(OrderSide.Buy, 1m, 14m, Base.AddHours(1)).Should().BeTrue();

        ledger.SignedQuantity.Should().Be(2m);
        var fills = ledger.Fills;
        fills.Should().HaveCount(2);

        // The realized check rides the close semantics: selling 1 at 16
        // realizes 4 exactly when the two entries averaged to 12.
        ledger.FillSide(OrderSide.Sell, 1m, 16m, Base.AddHours(2)).Should().BeTrue();
        var stats = ledger.ComputeStatistics(16m);
        stats.RealizedTotal.Should().Be(4m, "one unit closed at 16 - the 12 average");
        stats.Trades.Should().Be(1);
        stats.Wins.Should().Be(1);
    }

    [Fact]
    public void PartialClose_TheRemainder_KeepsItsAverageEntry()
    {
        var (ledger, _) = Ledger();

        ledger.FillSide(OrderSide.Buy, 2m, 10m, Base);
        ledger.FillSide(OrderSide.Sell, 1m, 12m, Base.AddHours(1));

        ledger.SignedQuantity.Should().Be(1m);
        var stats = ledger.ComputeStatistics(12m);
        stats.RealizedTotal.Should().Be(2m, "one unit closed at 12 from the 10 average");
        stats.Unrealized.Should().Be(2m, "the remainder marks 12 - 10 at the final close");

        // A second sell at 13 closes against the SAME average (10): 3 realized.
        ledger.FillSide(OrderSide.Sell, 1m, 13m, Base.AddHours(2)).Should().BeTrue();
        ledger.ComputeStatistics(13m).RealizedTotal.Should().Be(5m, "2 + 3, the average never re-averaged");
    }

    [Fact]
    public void Flip_TheRemainderStamps_FreshAtItsOwnFill()
    {
        var (ledger, _) = Ledger();

        ledger.FillSide(OrderSide.Buy, 2m, 10m, Base);
        // The exit overshoots: 2 close + 3 flip short, the shorts mark 12.
        ledger.FillSide(OrderSide.Sell, 5m, 12m, Base.AddHours(1));

        ledger.SignedQuantity.Should().Be(-3m);
        var afterFlip = ledger.ComputeStatistics(12m);
        afterFlip.Trades.Should().Be(1, "one round trip closed, the flip's remainder is NOT its own loser");
        afterFlip.RealizedTotal.Should().Be(4m, "(12 - 10) x 2");

        // Closing the shorts at 13 realizes (12 - 13) x 3 against the flip's own stamp: a loss.
        ledger.FillSide(OrderSide.Buy, 3m, 13m, Base.AddHours(2)).Should().BeTrue();
        var stats = ledger.ComputeStatistics(13m);
        stats.Trades.Should().Be(2);
        stats.Losses.Should().Be(1);
        stats.RealizedTotal.Should().Be(1m, "4 won - 3 lost against the flip's fresh average 12");
    }

    [Fact]
    public void Refusal_NoPhantomFill_TheBookStaysUntouchedAndTheReasonIsVisible()
    {
        var (ledger, _) = Ledger(quote: 5m);

        var placed = ledger.FillSide(OrderSide.Buy, 1m, 10m, Base);

        placed.Should().BeFalse("the simulated quote balance cannot carry the fill");
        ledger.Fills.Should().BeEmpty("a refused placement never fabricates a fill");
        ledger.SignedQuantity.Should().Be(0m);
        var stats = ledger.ComputeStatistics(10m);
        stats.Fills.Should().Be(0);
        stats.Volume.Should().Be(0m);
        ledger.Refusals.Should().ContainSingle();
        ledger.Refusals[0].Reason.Should().Contain("Insufficient simulated quote balance");
        ledger.Refusals[0].Time.Should().Be(Base);
    }

    [Fact]
    public void Commission_StampsFromTheSchedule_AndRidesOutsideTheBalance()
    {
        var (ledger, _) = Ledger();

        ledger.FillSide(OrderSide.Buy, 2m, 100m, Base).Should().BeTrue();

        // 200 notional x 10 bps = 0.2 (half-away rounding at 8 decimals).
        ledger.Fills[0].Commission.Should().Be(0.2m);
        // The balance carries only the trade's quote delta: the commission
        // never drains the simulated cash (the paper adapter's parity).
        var quoteDelta = ledger.Fills[0].Price.Value * ledger.Fills[0].Quantity.Value;
        ledger.ComputeStatistics(100m).CommissionTotal.Should().Be(0.2m);
        ledger.MarkEquity(100m).Should().Be(1_000_000m - quoteDelta + (2m * 100m),
            "quote cash minus the notional plus the open position marked — commissions ride the record");
    }

    [Fact]
    public void MissingSchedule_TheCommissionIsHonestUnknown()
    {
        var ledger = ScheduleLessLedger();

        ledger.FillSide(OrderSide.Buy, 1m, 100m, Base).Should().BeTrue();

        ledger.Fills[0].Commission.Should().BeNull("a venue without a schedule never fabricates a zero fee");
        ledger.ComputeStatistics(100m).CommissionTotal.Should().Be(0m, "no schedule stamped nothing");
    }

    [Fact]
    public void FillIds_AreTheDeterministicRunLocalSequence()
    {
        var (ledger, _) = Ledger();

        ledger.FillSide(OrderSide.Buy, 1m, 10m, Base);
        ledger.FillSide(OrderSide.Buy, 1m, 11m, Base.AddHours(1));

        ledger.Fills.Select(fill => fill.Id).Should().Equal("bt-000000", "bt-000001");
        ledger.Fills.Select(fill => fill.CreatedAt).Should().Equal(
            [Base, Base.AddHours(1)],
            "the replay's only clock is the fill's candle time");
        ledger.Fills[0].Type.Should().Be(OrderType.Market);
        ledger.Fills[0].Status.Should().Be(OrderStatus.Filled);
        ledger.Fills[0].Symbol.Normalized.Should().Be("BTC/USDT");
    }

    [Fact]
    public void Statistics_FoldTheEquityCurve_PeakFinalDrawdownAndRatios()
    {
        var (ledger, _) = Ledger(quote: 1_000m);

        ledger.FillSide(OrderSide.Buy, 1m, 10m, Base);
        ledger.Mark(Base, 20m);
        ledger.Mark(Base.AddHours(1), 8m);
        ledger.FillSide(OrderSide.Sell, 1m, 12m, Base.AddHours(2));
        ledger.Mark(Base.AddHours(2), 12m);

        var stats = ledger.ComputeStatistics(12m);

        stats.Fills.Should().Be(2);
        stats.Trades.Should().Be(1);
        stats.Wins.Should().Be(1);
        stats.WinRate.Should().Be(1m);
        stats.ProfitFactor.Should().BeNull("nothing was lost — never a fabricated infinity");
        stats.AverageWinner.Should().Be(2m);
        stats.PeakEquity.Should().Be(1010m, "starting quote + the marked walk up");
        stats.FinalEquity.Should().Be(1002m);
        stats.MaxDrawdown.Should().Be(12m, "peak 1010 - trough 998 (the mark at 8)");
    }

    [Fact]
    public void Statistics_OfNothingDecided_RatiosStayNullAndSizingFloors()
    {
        var (ledger, _) = Ledger(quote: 1_000m);

        ledger.MarkEquity(50m).Should().Be(1_000m);
        var stats = ledger.ComputeStatistics(50m);
        stats.WinRate.Should().BeNull();
        stats.AverageWinner.Should().BeNull();
        stats.AverageLoser.Should().BeNull();

        // The sizing floor: never buying marginally MORE than the budget.
        ledger.LongTargetQuantity(10m, 1m).Should().Be(100m, "1000 quote at 10 price");
        ledger.LongTargetQuantity(3m, 1m).Should().Be(333.33333333m, "floored at the journal's eight decimals");

        ledger.ComputeStatistics(10m).Trades.Should().Be(0, "no fills, no trips");
        stats.RealizedTotal.Should().Be(0m);
    }
}
