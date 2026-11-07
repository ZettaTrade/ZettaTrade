namespace ZettaTrade.App.Tests.Services;

using System;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;

/// <summary>
/// Pins for the calendar-aware in-series gap detector (T-086): a run of
/// expected-but-absent opens between two known bars is one gap, the
/// weekend the venue itself does not trade is never one, future opens
/// (beyond the horizon) are not missing yet, and a hole wider than the
/// detector's bound reports truncated — the bounded repair pass skips it,
/// the paging machinery continues. A non-ascending series trips the guard;
/// the caller keeps the view exactly as it stood. Pure facts.
/// </summary>
public sealed class CandleGapDetectorTests
{
    /// <summary>Thursday, 2026-01-01 10:00 UTC — an H1 cadence base far
    /// from any weekend edge the session facts use.</summary>
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

    /// <summary>A fresh horizon: nothing is missing beyond it.</summary>
    private static readonly DateTimeOffset Horizon = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>One hourly bar at <paramref name="hour"/> hours past the base epoch.</summary>
    private static Bar At(int hour) => new(
        Base.AddHours(hour),
        100m,
        101m,
        99m,
        100m,
        5);

    [Fact]
    public void AdjacentBarsHaveNoGaps()
    {
        CandleGapDetector.DetectGaps([At(0), At(1), At(2)], TimeFrame.H1, AssetClass.Crypto, Horizon)
            .Should().BeEmpty("contiguous candles leave the calendar satisfied everywhere");
    }

    [Fact]
    public void SingleAndEmptySeriesHaveNoPairsToCheck()
    {
        CandleGapDetector.DetectGaps([At(0)], TimeFrame.H1, AssetClass.Crypto, Horizon)
            .Should().BeEmpty();
        CandleGapDetector.DetectGaps([], TimeFrame.H1, AssetClass.Crypto, Horizon)
            .Should().BeEmpty("an empty series has no hole to repair");
    }

    [Fact]
    public void NonAscendingSeriesTripsTheGuard()
    {
        CandleGapDetector.DetectGaps([At(20), At(10)], TimeFrame.H1, AssetClass.Crypto, Horizon)
            .Should().BeNull("a contract violation keeps the view untouched, never 'repaired'");
        CandleGapDetector.DetectGaps([At(10), At(10)], TimeFrame.H1, AssetClass.Crypto, Horizon)
            .Should().BeNull("equal opens are the same violation");
    }

    [Fact]
    public void TheHoleBetweenTwoBarsIsOneMissingRun()
    {
        var gaps = CandleGapDetector.DetectGaps([At(0), At(2), At(3)], TimeFrame.H1, AssetClass.Crypto, Horizon);

        gaps.Should().HaveCount(1);
        gaps![0].FirstMissingOpen.Should().Be(Base.AddHours(1), "the walk starts at the open right after the left bar");
        gaps[0].LastMissingOpen.Should().Be(Base.AddHours(1));
        gaps[0].MissingBars.Should().Be(1);
        gaps[0].IsTruncated.Should().BeFalse();
    }

    [Fact]
    public void CryptoDailyCountsTheWeekendAsRealBars()
    {
        var friday = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var monday = new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);
        var weekend = new[]
        {
            new Bar(friday, 1m, 2m, 0m, 1m, 5),
            new Bar(monday, 1m, 2m, 0m, 1m, 5),
        };

        var gaps = CandleGapDetector.DetectGaps(weekend, TimeFrame.D1, AssetClass.Crypto, Horizon);

        gaps.Should().HaveCount(1, "the crypto venue trades both weekend days, so the calendar expects them");
        gaps![0].MissingBars.Should().Be(2);
        gaps[0].FirstMissingOpen.Should().Be(friday.AddDays(1));
        gaps[0].LastMissingOpen.Should().Be(friday.AddDays(2));
    }

    [Fact]
    public void MarketDailyNeverCountsTheClosedWeekendAsAGap()
    {
        var friday = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var monday = new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);
        var daySkips = new[]
        {
            new Bar(friday, 1m, 2m, 0m, 1m, 5),
            new Bar(monday, 1m, 2m, 0m, 1m, 5),
        };

        CandleGapDetector.DetectGaps(daySkips, TimeFrame.D1, AssetClass.Stock, Horizon)
            .Should().BeEmpty("the venue's own sessions define the cadence: Friday continues Monday");
    }

    [Fact]
    public void TheHorizonCutsFutureOpensOutOfTheRun()
    {
        var horizon = Base.AddHours(2.5);

        var gaps = CandleGapDetector.DetectGaps([At(0), At(3)], TimeFrame.H1, AssetClass.Crypto, horizon);

        gaps.Should().HaveCount(1);
        gaps![0].MissingBars.Should().Be(2, "the walk stops at the horizon: the third hour does not exist yet");
        gaps[0].LastMissingOpen.Should().Be(Base.AddHours(2));
        gaps[0].IsTruncated.Should().BeFalse("the horizon is not the detector's enumeration bound");
    }

    [Fact]
    public void TheExactlyBoundWideHoleIsOneWholeRepairPass()
    {
        // The right neighbor sits exactly one bound of expected bars above
        // the hole's first open: the whole run fits the detector's bound,
        // the neighbor itself ends the run — a hole this wide is still one
        // complete bounded fetch.
        var rightNeighbor = Base.AddHours(CandleGapDetector.MaxMissingBarsPerGap + 1);

        var gaps = CandleGapDetector.DetectGaps([At(0), new Bar(rightNeighbor, 1m, 2m, 0m, 1m, 5)], TimeFrame.H1, AssetClass.Crypto, Horizon);

        gaps.Should().HaveCount(1);
        var gap = gaps!.Single();
        gap.MissingBars.Should().Be(CandleGapDetector.MaxMissingBarsPerGap);
        gap.FirstMissingOpen.Should().Be(Base.AddHours(1));
        gap.LastMissingOpen.Should().Be(Base.AddHours(CandleGapDetector.MaxMissingBarsPerGap));
        gap.IsTruncated.Should().BeFalse("the run ends at the right neighbor — exactly one bounded fetch");
    }

    [Fact]
    public void TheWideHoleIsTruncatedAtTheDetectorBound()
    {
        // The right neighbor sits one bound PLUS one expected bar above the
        // hole's first open: the walk exhausts its bound while expected
        // opens still exist below the neighbor — the enumerated width stops
        // at the bound and the run reports truncated (the bounded repair
        // pass skips it; deeper history is the paging machinery's own job).
        var rightNeighbor = Base.AddHours(CandleGapDetector.MaxMissingBarsPerGap + 2);

        var gaps = CandleGapDetector.DetectGaps([At(0), new Bar(rightNeighbor, 1m, 2m, 0m, 1m, 5)], TimeFrame.H1, AssetClass.Crypto, Horizon);

        gaps.Should().HaveCount(1);
        var gap = gaps!.Single();
        gap.MissingBars.Should().Be(CandleGapDetector.MaxMissingBarsPerGap, "the enumerated width stops at the bound");
        gap.FirstMissingOpen.Should().Be(Base.AddHours(1));
        gap.LastMissingOpen.Should().Be(Base.AddHours(CandleGapDetector.MaxMissingBarsPerGap));
        gap.IsTruncated.Should().BeTrue("the run continues beyond the bound — the repair pass skips it");
    }

    [Fact]
    public void TwoHolesListInPairOrder()
    {
        var gaps = CandleGapDetector.DetectGaps([At(0), At(2), At(5)], TimeFrame.H1, AssetClass.Crypto, Horizon);

        gaps!.Should().HaveCount(2);
        gaps[0].Should().Be(new CandleGap(Base.AddHours(1), Base.AddHours(1), 1, false));
        gaps[1].Should().Be(new CandleGap(Base.AddHours(3), Base.AddHours(4), 2, false),
            "gaps list in the order the series' pairs carry them");
    }
}
