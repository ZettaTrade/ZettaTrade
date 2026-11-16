namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The CVD pane's aggregator goldens (T-244): a trade lands in the bar slot
/// whose window contains its timestamp (binary-searched over the standing
/// bars' own opens, never a grid-floor guess), the aggressor's side sizes
/// the delta (a Buy adds, a Sell subtracts, a NEUTRAL trade contributes
/// nothing — an only-neutral bar is the honest gap), a trade outside the
/// standing series' windows is unattributed, a non-positive size is dropped
/// (the BUG-027 discipline), the running sum carries through the gaps, and
/// the session-anchored reset (the shipped semantics, the VWAP anchor's
/// UTC-day rule) restarts the sum at every session break — both reset modes
/// pinned.
/// </summary>
public sealed class CvdAggregationTests
{
    private static readonly DateTimeOffset Base = new(2026, 3, 2, 12, 0, 0, TimeSpan.Zero);

    private static readonly Symbol Pair = Symbol.Parse("BTC/USDT");

    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private static Bar Bar(DateTimeOffset open) => new(open, 100m, 101m, 99m, 100m, 1000m);

    private static MarketTrade Trade(DateTimeOffset timestamp, OrderSide side, decimal size) => new()
    {
        Id = $"t{timestamp.Ticks}-{side}-{size}",
        Symbol = Pair,
        Side = side,
        Price = new Price(100m),
        Quantity = new Quantity(size),
        Timestamp = timestamp,
    };

    private static IReadOnlyList<Bar> ThreeBars() => [Bar(Base), Bar(Base.Add(Minute)), Bar(Base.AddMinutes(2))];

    /// <summary>A trade lands in the slot whose window contains its timestamp — the 12:01:40 beat rides the 12:01 bar.</summary>
    [Fact]
    public void ATradeLandsInTheWindowContainingItsTimestamp()
    {
        var bars = ThreeBars();
        var buckets = new Dictionary<DateTimeOffset, CvdAggregation.Bucket>();

        var attributed = CvdAggregation.AddTrade(
            bars, buckets, Trade(Base.AddMinutes(1).AddSeconds(40), OrderSide.Buy, 5m), Minute);

        attributed.Should().BeTrue("the beat sits inside the standing series' windows");
        buckets.Should().ContainKey(Base.AddMinutes(1), "the 12:01 bar's own slot took the trade");
        buckets.Should().HaveCount(1, "no neighbor slot was squeezed");

        var values = CvdAggregation.Aligned(bars, buckets, sessionReset: true);

        values.Should().Equal(
            (IEnumerable<decimal?>)[null, 5m, null],
            "the beat's own bar reads the delta, the untouched slots stay gaps");
    }

    /// <summary>The aggressor's side sizes the delta: a Buy adds, a Sell subtracts, the bar reads the net.</summary>
    [Fact]
    public void ABuyAddsAndASellSubtracts()
    {
        var bars = ThreeBars();
        var buckets = new Dictionary<DateTimeOffset, CvdAggregation.Bucket>();

        CvdAggregation.AddTrade(bars, buckets, Trade(Base.AddSeconds(20), OrderSide.Buy, 5m), Minute);
        CvdAggregation.AddTrade(bars, buckets, Trade(Base.AddSeconds(50), OrderSide.Sell, 2m), Minute);

        var values = CvdAggregation.Aligned(bars, buckets, sessionReset: true);

        values.Should().Equal(
            (IEnumerable<decimal?>)[3m, null, null],
            "the first bar's net delta is the summed buys minus the summed sells");
    }

    /// <summary>The running sum carries through the gaps: a gap bar renders null and contributes nothing, the next counted bar continues.</summary>
    [Fact]
    public void TheRunningSumCarriesThroughTheGaps()
    {
        var bars = ThreeBars();
        var buckets = new Dictionary<DateTimeOffset, CvdAggregation.Bucket>();

        CvdAggregation.AddTrade(bars, buckets, Trade(Base.AddSeconds(10), OrderSide.Buy, 2m), Minute);
        CvdAggregation.AddTrade(
            bars, buckets, Trade(Base.AddMinutes(2).AddSeconds(30), OrderSide.Sell, 1m), Minute);

        var values = CvdAggregation.Aligned(bars, buckets, sessionReset: true);

        values.Should().Equal(
            (IEnumerable<decimal?>)[2m, null, 1m],
            "the middle bar carried no sided trade (the honest gap), the third bar's value continues the standing sum");
    }

    /// <summary>A NEUTRAL trade (the wire's own no-aggressor absence) contributes nothing — an only-neutral bar is the honest gap.</summary>
    [Fact]
    public void AnOnlyNeutralBarIsTheHonestGap()
    {
        var bars = ThreeBars();
        var buckets = new Dictionary<DateTimeOffset, CvdAggregation.Bucket>();

        CvdAggregation.AddTrade(bars, buckets, Trade(Base.AddSeconds(10), OrderSide.None, 5m), Minute);

        CvdAggregation.Aligned(bars, buckets, sessionReset: true).Should().Equal(
            (IEnumerable<decimal?>)[null, null, null],
            "the volume is real but the side is unknowable — never a fabricated flat zero");

        CvdAggregation.AddTrade(
            bars, buckets, Trade(Base.AddMinutes(2).AddSeconds(10), OrderSide.Buy, 4m), Minute);

        CvdAggregation.Aligned(bars, buckets, sessionReset: true).Should().Equal(
            (IEnumerable<decimal?>)[null, null, 4m],
            "the neutral bar stays a gap and contributes nothing, the counted bar starts the sum fresh");
    }

    /// <summary>A trade outside the standing windows is unattributed: before the head, at or past the last bar's close.</summary>
    [Fact]
    public void AWindowlessTradeIsUnattributed()
    {
        var bars = ThreeBars();
        var buckets = new Dictionary<DateTimeOffset, CvdAggregation.Bucket>();

        CvdAggregation.AddTrade(bars, buckets, Trade(Base.AddSeconds(-1), OrderSide.Buy, 1m), Minute)
            .Should().BeFalse("a beat before the head has no standing slot");
        CvdAggregation.AddTrade(
                bars, buckets, Trade(Base.AddMinutes(3), OrderSide.Buy, 1m), Minute)
            .Should().BeFalse("the last bar's close is the window's end — a beat at or past it stands outside");
        CvdAggregation.AddTrade(
                bars, buckets, Trade(Base.AddMinutes(9), OrderSide.Sell, 1m), Minute)
            .Should().BeFalse("a beat far past the close stands outside");

        buckets.Should().BeEmpty("nothing was squeezed into a neighbor");
    }

    /// <summary>A boundary timestamp joins its own bar: a beat at a bar's open rides it, one a millisecond before the last close stands inside.</summary>
    [Fact]
    public void ABoundaryTimestampJoinsItsOwnBar()
    {
        var bars = ThreeBars();
        var buckets = new Dictionary<DateTimeOffset, CvdAggregation.Bucket>();

        CvdAggregation.AddTrade(bars, buckets, Trade(Base.AddMinutes(1), OrderSide.Buy, 1m), Minute)
            .Should().BeTrue("the beat at the 12:01 bar's own open rides that bar");
        buckets.Keys.Should().Equal(new[] { Base.AddMinutes(1) });

        var almostClose = CvdAggregation.AddTrade(
            bars, buckets, Trade(Base.AddMinutes(2).Add(Minute).Add(TimeSpan.FromMilliseconds(-1)), OrderSide.Buy, 1m), Minute);

        almostClose.Should().BeTrue("a beat a millisecond before the last bar's close stands inside it");
        buckets.Keys.Should().Equal(new[] { Base.AddMinutes(1), Base.AddMinutes(2) });
    }

    /// <summary>A non-positive size is dropped (the BUG-027 discipline): a broken wire row never fabricates a delta.</summary>
    [Fact]
    public void ANonPositiveSizeIsDropped()
    {
        var bars = ThreeBars();
        var buckets = new Dictionary<DateTimeOffset, CvdAggregation.Bucket>();

        CvdAggregation.AddTrade(bars, buckets, Trade(Base.AddSeconds(10), OrderSide.Buy, 0m), Minute)
            .Should().BeFalse("a zero size is a broken wire row");
        CvdAggregation.AddTrade(bars, buckets, Trade(Base.AddSeconds(20), OrderSide.Sell, -3m), Minute)
            .Should().BeFalse("a negative size is a broken wire row");

        buckets.Should().BeEmpty("no bucket was fabricated");
    }

    /// <summary>The session-anchored reset (the shipped semantics) restarts the sum at every UTC-day break; the plain mode carries it through.</summary>
    [Fact]
    public void TheSessionResetRestartsTheSumAtTheUtcDayBreak()
    {
        var bars = new[]
        {
            Bar(new DateTimeOffset(2026, 3, 1, 23, 58, 0, TimeSpan.Zero)),
            Bar(new DateTimeOffset(2026, 3, 1, 23, 59, 0, TimeSpan.Zero)),
            Bar(new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero)),
            Bar(new DateTimeOffset(2026, 3, 2, 0, 1, 0, TimeSpan.Zero)),
        };

        var buckets = new Dictionary<DateTimeOffset, CvdAggregation.Bucket>();

        // The two pre-break bars carry a Buy-5 then a Sell-2 (+5, then -2
        // netting 3), the two post-break bars carry 1-sized buys — the sums
        // hand-check to [5, 3] and then [1, 2] with the reset, [4, 5]
        // without it.
        var sides = new[] { OrderSide.Buy, OrderSide.Sell, OrderSide.Buy, OrderSide.Buy };
        var sizes = new[] { 5m, 2m, 1m, 1m };
        for (var i = 0; i < bars.Length; i++)
        {
            CvdAggregation.AddTrade(
                bars,
                buckets,
                Trade(bars[i].Time.AddSeconds(30), sides[i], sizes[i]),
                Minute);
        }

        CvdAggregation.Aligned(bars, buckets, sessionReset: true).Should().Equal(
            (IEnumerable<decimal?>)[5m, 3m, 1m, 2m],
            "the UTC-day break restarts the sum: the new session's first counted bar reads its own flow");
        CvdAggregation.Aligned(bars, buckets, sessionReset: false).Should().Equal(
            (IEnumerable<decimal?>)[5m, 3m, 4m, 5m],
            "without the reset the running sum carries across the session break");
    }

    /// <summary>Empty inputs answer the honest empties: no bars is an empty pane, a bucketless series is an all-gap pane.</summary>
    [Fact]
    public void TheEmptyInputsAnswerTheHonestEmpties()
    {
        var buckets = new Dictionary<DateTimeOffset, CvdAggregation.Bucket>();

        CvdAggregation.Aligned([], buckets, sessionReset: true).Should().BeEmpty();
        CvdAggregation.Aligned(ThreeBars(), buckets, sessionReset: true).Should().Equal(
            (IEnumerable<decimal?>)[null, null, null],
            "a bucketless window is an all-gap pane, never a fabricated stretch");
        CvdAggregation.AddTrade([], buckets, Trade(Base, OrderSide.Buy, 1m), Minute)
            .Should().BeFalse("an empty series has no slots to attribute into");
    }

    /// <summary>Buckets keyed outside the bars' own opens are ignored — the alignment reads the standing slots only.</summary>
    [Fact]
    public void KeysOutsideTheBarsAreIgnored()
    {
        var bars = ThreeBars();
        var standing = new CvdAggregation.Bucket();
        standing.Add(OrderSide.Buy, 5m);
        var stray = new CvdAggregation.Bucket();
        stray.Add(OrderSide.Sell, 1m);
        var buckets = new Dictionary<DateTimeOffset, CvdAggregation.Bucket>
        {
            [Base] = standing,
            [Base.AddSeconds(30)] = stray, // a slot the standing bars do not open
        };

        var values = CvdAggregation.Aligned(bars, buckets, sessionReset: true);

        values.Should().Equal(
            (IEnumerable<decimal?>)[5m, null, null],
            "the stray key never aligned onto a bar");
    }
}
