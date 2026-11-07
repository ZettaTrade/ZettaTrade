namespace ZettaTrade.UI.Tests.Charting;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.UI.Charting;

/// <summary>
/// Trade tape ring buffer behavior: bounded memory (a push past the capacity
/// evicts the oldest trade), newest-first copies bounded by the asked row
/// count, and a push+copy frame cost that leaves a burst second far inside
/// the 60 fps budget.
/// </summary>
public sealed class TradeTapeRingBufferTests
{
    #region Test Output

    private readonly ITestOutputHelper _output;

    public TradeTapeRingBufferTests(ITestOutputHelper output)
    {
        _output = output;
    }

    #endregion
    #region Test Data

    /// <summary>Synthetic trade: an ascending price and alternating side keep rows distinguishable.</summary>
    private static MarketTrade Trade(int index) =>
        new()
        {
            Id = $"t{index}",
            Symbol = Symbol.Parse("BTC/USDT"),
            Side = index % 2 == 0 ? OrderSide.Buy : OrderSide.Sell,
            Price = new Price(100m + index),
            Quantity = new Quantity(index + 1),
            Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(index),
        };

    #endregion

    #region Construction

    [Fact]
    public void Constructor_NonPositiveCapacity_Throws()
    {
        var construct = () => new TradeTapeRingBuffer(0);

        construct.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_CapacityAndEmptyCount()
    {
        var ring = new TradeTapeRingBuffer(64);

        ring.Capacity.Should().Be(64);
        ring.Count.Should().Be(0);
    }

    #endregion

    #region Push & Order

    [Fact]
    public void Push_GrowsCountAndServesNewestFirst()
    {
        var ring = new TradeTapeRingBuffer(64);

        for (var i = 0; i < 10; i++)
        {
            ring.Push(Trade(i));
        }

        ring.Count.Should().Be(10);

        var newest = new List<MarketTrade>();
        ring.CopyNewestFirst(newest, 64);

        newest.Should().HaveCount(10);
        newest[0].Id.Should().Be("t9", "the newest trade is served first");
        newest[9].Id.Should().Be("t0");
    }

    [Fact]
    public void Push_PastCapacity_EvictsTheOldestAndStaysBounded()
    {
        const int Capacity = 8;
        var ring = new TradeTapeRingBuffer(Capacity);

        for (var i = 0; i < Capacity + 40; i++)
        {
            ring.Push(Trade(i));
        }

        ring.Count.Should().Be(Capacity, "memory stays bounded when the feed outpaces the tape");

        var newest = new List<MarketTrade>();
        ring.CopyNewestFirst(newest, Capacity);

        newest[0].Id.Should().Be($"t{Capacity + 39}", "the newest trade survives the evictions");
        newest.Should().OnlyContain(t => t.Id != "t0", "the oldest trades were evicted");
    }

    [Fact]
    public void Push_WrapsAroundTheArrayWithoutTearingTheOrder()
    {
        const int Capacity = 4;
        var ring = new TradeTapeRingBuffer(Capacity);

        // Two full laps: every slot was reused at least twice.
        for (var i = 0; i < 2 * Capacity; i++)
        {
            ring.Push(Trade(i));
        }

        var newest = new List<MarketTrade>();
        ring.CopyNewestFirst(newest, Capacity);

        newest.Select(t => t.Id)
            .Should().Equal(
                $"t{2 * Capacity - 1}",
                $"t{2 * Capacity - 2}",
                $"t{2 * Capacity - 3}",
                $"t{2 * Capacity - 4}");
    }

    [Fact]
    public void Push_NullTrade_Throws()
    {
        var ring = new TradeTapeRingBuffer(4);

        var push = () => ring.Push(null!);

        push.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region Copy

    [Fact]
    public void CopyNewestFirst_ClearsTheTargetFirst()
    {
        var ring = new TradeTapeRingBuffer(8);
        ring.Push(Trade(1));

        var target = new List<MarketTrade> { Trade(999), Trade(998) };
        ring.CopyNewestFirst(target, 8);

        target.Should().ContainSingle("a frame plans only the ring's trades, never leftovers")
            .Which.Id.Should().Be("t1");
    }

    [Fact]
    public void CopyNewestFirst_BoundsTheCopyToTheAskedRows()
    {
        var ring = new TradeTapeRingBuffer(64);
        for (var i = 0; i < 20; i++)
        {
            ring.Push(Trade(i));
        }

        var newest = new List<MarketTrade>();
        ring.CopyNewestFirst(newest, maxCount: 3);

        newest.Select(t => t.Id).Should().Equal("t19", "t18", "t17");
    }

    [Fact]
    public void CopyNewestFirst_NegativeCount_Throws()
    {
        var ring = new TradeTapeRingBuffer(4);
        ring.Push(Trade(1));

        var copy = () => ring.CopyNewestFirst(new List<MarketTrade>(), maxCount: -1);

        copy.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Clear_EmptiesTheRing()
    {
        var ring = new TradeTapeRingBuffer(4);
        for (var i = 0; i < 3; i++)
        {
            ring.Push(Trade(i));
        }

        ring.Clear();
        ring.Count.Should().Be(0);

        // The ring restarts cleanly: pushes after a clear serve as if virgin.
        ring.Push(Trade(10));
        var newest = new List<MarketTrade>();
        ring.CopyNewestFirst(newest, 4);

        newest.Should().ContainSingle().Which.Id.Should().Be("t10");
    }

    #endregion

    #region Performance

    /// <summary>Untimed frames that prime JIT paths before the measured burst.</summary>
    private const int WarmupFrames = 10;

    /// <summary>
    /// One frame's work at the stated burst load: a 1000 trades/s feed lands
    /// about 17 trades per 60 fps frame — the ring absorbs them in O(1) and
    /// the copy stays at the visible row count. The pinned budget is the
    /// frame-plan family's: the median and mean of the burst stay below the
    /// frame budget (a genuine regression inflates every frame); the worst
    /// frame is reported to the test output for observation only, because a
    /// parallel test host's scheduler slice can steal tens of milliseconds
    /// from one isolated frame without the sustained rate leaving budget.
    /// </summary>
    [Fact]
    public void PushAndCopy_SustainAFrameAtBurstLoad_InsideTheSixtyFpsBudget()
    {
        const int TradesPerSecond = 1000;
        const int FramesPerSecond = 60;
        const int TradesPerFrame = TradesPerSecond / FramesPerSecond;
        const int BurstFrames = 60;
        const int VisibleRows = 60;
        const double FrameBudgetMs = 16.0;

        var ring = new TradeTapeRingBuffer(TradeTapeRingBuffer.DefaultCapacity);
        var target = new List<MarketTrade>(VisibleRows);

        // Warmup: fill the ring so the steady-state path (with evictions) is
        // inside the measured window, JIT included.
        for (var i = 0; i < TradeTapeRingBuffer.DefaultCapacity; i++)
        {
            ring.Push(Trade(i));
        }

        for (var i = 0; i < WarmupFrames; i++)
        {
            for (var t = 0; t < TradesPerFrame; t++)
            {
                ring.Push(Trade(-t));
            }

            ring.CopyNewestFirst(target, VisibleRows);
        }

        var frames = new double[BurstFrames];
        var tradeIndex = 0;
        for (var frame = 0; frame < BurstFrames; frame++)
        {
            var started = Stopwatch.GetTimestamp();
            for (var t = 0; t < TradesPerFrame; t++)
            {
                ring.Push(Trade(tradeIndex++));
            }

            ring.CopyNewestFirst(target, VisibleRows);
            var stopped = Stopwatch.GetTimestamp();

            target.Should().HaveCount(VisibleRows);
            frames[frame] = (stopped - started) * 1000d / Stopwatch.Frequency;
        }

        Array.Sort(frames);
        var median = (frames[(BurstFrames / 2) - 1] + frames[BurstFrames / 2]) / 2d;
        var average = frames.Average();
        var worst = frames[BurstFrames - 1];
        _output.WriteLine(
            "ring frame: median {0:F4} ms, avg {1:F4} ms, worst {2:F4} ms over {3} frames at ~{4} trades/s (budget {5} ms)",
            median,
            average,
            worst,
            BurstFrames,
            TradesPerSecond,
            FrameBudgetMs);

        median.Should().BeLessThan(FrameBudgetMs);
        average.Should().BeLessThan(FrameBudgetMs);
    }

    #endregion
}
