namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;

/// <summary>
/// The venue-spread math's pins (T-253): the median answers the sorted
/// mid (or the two-mids' mean) and null on empty, the basis-point delta
/// refuses a non-positive reference, the deltas ride the ANSWERING set's
/// median and dash below two answers (a single venue's delta against
/// itself would dress the absence up as a verdict), and the basis series
/// pairs ONLY over the shared bar-open times (the T-252 alignment axis)
/// with a non-positive close refusing its sample — every list sorted by
/// its key, so a recompute is bit-identical.
/// </summary>
public sealed class VenueSpreadMathTests
{
    [Fact]
    public void Median_AnOddAndAnEvenSet_AnswerTheSortedMids()
    {
        VenueSpreadMath.Median([3m, 1m, 2m]).Should().Be(2m);
        VenueSpreadMath.Median([4m, 1m, 3m, 2m]).Should().Be(2.5m, "the two mids' mean");
        VenueSpreadMath.Median([5m]).Should().Be(5m);
    }

    [Fact]
    public void Median_AnEmptySet_IsTheHonestNull()
    {
        VenueSpreadMath.Median([]).Should().BeNull();
    }

    [Fact]
    public void Bps_AOnePercentRise_IsOneHundredBasisPoints()
    {
        VenueSpreadMath.Bps(100m, 101m).Should().BeApproximately(100.0, 1e-9);
        VenueSpreadMath.Bps(100m, 99m).Should().BeApproximately(-100.0, 1e-9);
        VenueSpreadMath.Bps(100m, 100m).Should().Be(0.0);
    }

    [Fact]
    public void Bps_ANonPositiveReference_IsTheHonestNull()
    {
        VenueSpreadMath.Bps(0m, 5m).Should().BeNull("no ratio exists against a zero reference");
        VenueSpreadMath.Bps(-1m, 5m).Should().BeNull();
    }

    [Fact]
    public void DeltasVsMedian_TwoAnsweringVenues_MirrorAroundTheMedian()
    {
        // The lasts 99 and 101: the median 100, the deltas exactly ±100
        // bps (each venue's distance to the set's own robust reference).
        var deltas = VenueSpreadMath.DeltasVsMedian([99m, 101m]);

        deltas.Should().HaveCount(2);
        deltas[0].Should().BeApproximately(-100.0, 1e-9);
        deltas[1].Should().BeApproximately(100.0, 1e-9);
    }

    [Fact]
    public void DeltasVsMedian_TheMedianRidesTheAnsweringSet_Alone()
    {
        // One venue answered (101), one stayed silent (null): the honest
        // answer is the all-dash table — a spread against one venue is
        // the venue against itself, nothing; the median never borrows
        // the silence.
        var deltas = VenueSpreadMath.DeltasVsMedian([null, 101m]);

        deltas.Should().HaveCount(2);
        deltas[0].Should().BeNull();
        deltas[1].Should().BeNull();
    }

    [Fact]
    public void DeltasVsMedian_AnsweredAndSilentVenues_KeepTheSlots()
    {
        // Three venues, two answered (99, 101): the median 100, the
        // answered deltas ride it (exactly ±100 bps), the silent slot
        // stays null.
        var deltas = VenueSpreadMath.DeltasVsMedian([99m, null, 101m]);

        deltas.Should().HaveCount(3);
        deltas[0].Should().BeApproximately(-100.0, 1e-9);
        deltas[1].Should().BeNull();
        deltas[2].Should().BeApproximately(100.0, 1e-9);
    }

    [Fact]
    public void BasisSeries_SharedBarTimesOnly_TheBpsMathExact()
    {
        // The spot walk 100 → 110 over minutes 0, 15; the perp carries
        // the SAME closes scaled by 1.01 over minutes 0, 15, 30 — the
        // shared times are minutes 0 and 15, the basis a flat +100 bps
        // (a 1% premium); the perp's extra bar (minute 30) pairs with
        // nothing and never enters.
        var spot = new[] { Bar(0, 100m), Bar(15, 110m) };
        var perp = new[] { Bar(0, 101m), Bar(15, 111.1m), Bar(30, 112m) };

        var series = VenueSpreadMath.BasisSeries(spot, perp);

        series.Should().HaveCount(2, "the minute-30 bar pairs with no spot bar");
        series[0].Time.Should().Be(DateTimeOffset.UnixEpoch.AddMinutes(0));
        series[1].Time.Should().Be(DateTimeOffset.UnixEpoch.AddMinutes(15));
        series[0].Bps.Should().BeApproximately(100.0, 1e-9, "101 against 100 is exactly +1%");
        series[1].Bps.Should()
            .BeApproximately((double)((111.1m - 110m) / 110m) * 10000.0, 1e-9);
    }

    [Fact]
    public void BasisSeries_ANonPositiveClose_RefusesItsSample()
    {
        // A zero spot close is corrupt data, not a price: that bar's
        // sample refuses (the key stays absent), the healthy bars still
        // answer.
        var spot = new[] { Bar(0, 100m), Bar(15, 0m), Bar(30, 102m) };
        var perp = new[] { Bar(0, 101m), Bar(15, 101m), Bar(30, 103m) };

        var series = VenueSpreadMath.BasisSeries(spot, perp);

        series.Select(static sample => sample.Time).Should().Equal(
            DateTimeOffset.UnixEpoch.AddMinutes(0),
            DateTimeOffset.UnixEpoch.AddMinutes(30));
    }

    [Fact]
    public void BasisSeries_TheSeries_SortsByTimeWhateverTheInputOrderDid()
    {
        // The perp bars arrive newest-first (a cache's tail-first shape):
        // the series still ascends — the spark reads left to right.
        var spot = new[] { Bar(0, 100m), Bar(15, 101m), Bar(30, 102m) };
        var perp = new[] { Bar(30, 103m), Bar(0, 101m), Bar(15, 102m) };

        var series = VenueSpreadMath.BasisSeries(spot, perp);

        series.Select(static sample => sample.Time).Should().BeInAscendingOrder();
        series.Should().HaveCount(3);
    }

    private static Bar Bar(int minutesFromEpoch, decimal close) => new()
    {
        Time = DateTimeOffset.UnixEpoch.AddMinutes(minutesFromEpoch),
        Open = close,
        High = close,
        Low = close,
        Close = close,
        Volume = 1m,
    };
}
