namespace ZettaTrade.UI.Tests.Charting;

using System;
using System.Collections.Generic;
using FluentAssertions;
using Xunit;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.UI.Charting;

/// <summary>
/// The liquidation ring's pins (T-242): a push past the capacity evicts the
/// OLDEST event (Count then stays at the capacity forever — a forced-order
/// storm can never grow the panel's memory), a copy serves the NEWEST
/// events first bounded by the asked row count, and a clear empties it.
/// </summary>
public sealed class LiquidationRingBufferTests
{
    #region Test Data

    private static LiquidationEvent Ev(int marker) => new()
    {
        Symbol = Symbol.Parse("BTC/USDT"),
        LiquidatedSide = LiquidationSide.Long,
        Price = new Price(marker),
        Quantity = new Quantity(1m),
        Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(marker),
    };

    private static List<decimal> PriceTrail(List<LiquidationEvent> events) =>
        events.ConvertAll(ev => ev.Price.Value);

    #endregion

    #region Capacity & Eviction

    [Fact]
    public void APushPastTheCapacity_EvictsTheOldestEvent()
    {
        var ring = new LiquidationRingBuffer(3);
        ring.Push(Ev(1));
        ring.Push(Ev(2));
        ring.Push(Ev(3));

        ring.Count.Should().Be(3, "the ring grows to its capacity");
        ring.Push(Ev(4));

        ring.Count.Should().Be(3, "a push past the capacity evicts instead of growing");
        var trail = new List<LiquidationEvent>();
        ring.CopyNewestFirst(trail, 10);
        PriceTrail(trail).Should().Equal(new decimal[] { 4m, 3m, 2m }, "the OLDEST event (1) fell out, the newest sits first");
    }

    [Fact]
    public void ARingWrapsWithoutLosingEvents()
    {
        var ring = new LiquidationRingBuffer(3);
        for (var i = 1; i <= 7; i++)
        {
            ring.Push(Ev(i));
        }

        var trail = new List<LiquidationEvent>();
        ring.CopyNewestFirst(trail, 10);
        PriceTrail(trail).Should().Equal(new decimal[] { 7m, 6m, 5m }, "seven pushes through a 3-ring carry exactly the newest three, in order");
    }

    #endregion

    #region Copy

    [Fact]
    public void TheCopy_IsBoundedByTheAskedRowCount()
    {
        var ring = new LiquidationRingBuffer(4);
        ring.Push(Ev(1));
        ring.Push(Ev(2));
        ring.Push(Ev(3));

        var trail = new List<LiquidationEvent>();
        ring.CopyNewestFirst(trail, 2);

        trail.Should().HaveCount(2, "a frame asks only for the rows its height can show");
        PriceTrail(trail).Should().Equal(new decimal[] { 3m, 2m }, "the wanted prefix is the NEWEST events, never the stale end");
    }

    [Fact]
    public void TheCopy_AskingMoreThanCarried_ServesAll()
    {
        var ring = new LiquidationRingBuffer(4);
        ring.Push(Ev(1));
        ring.Push(Ev(2));

        var trail = new List<LiquidationEvent>();
        ring.CopyNewestFirst(trail, 100);

        trail.Should().HaveCount(2);
        PriceTrail(trail).Should().Equal(2m, 1m);
    }

    [Fact]
    public void TheCopy_ClearsTheTargetFirst()
    {
        var ring = new LiquidationRingBuffer(2);
        ring.Push(Ev(1));

        var trail = new List<LiquidationEvent> { Ev(99) };
        ring.CopyNewestFirst(trail, 10);

        trail.Should().ContainSingle("the copy reuses (and clears) the frame's own list");
        trail[0].Price.Value.Should().Be(1m);
    }

    [Fact]
    public void ACopyFromAnEmptyRing_ServesNothing()
    {
        var ring = new LiquidationRingBuffer(4);

        var trail = new List<LiquidationEvent>();
        ring.CopyNewestFirst(trail, 10);

        trail.Should().BeEmpty();
    }

    #endregion

    #region Clear

    [Fact]
    public void AClear_EmptiesTheRing()
    {
        var ring = new LiquidationRingBuffer(4);
        ring.Push(Ev(1));
        ring.Push(Ev(2));
        ring.Clear();

        ring.Count.Should().Be(0);
        var trail = new List<LiquidationEvent>();
        ring.CopyNewestFirst(trail, 10);
        trail.Should().BeEmpty();

        ring.Push(Ev(3));
        ring.CopyNewestFirst(trail, 10);
        PriceTrail(trail).Should().Equal(new decimal[] { 3m }, "a cleared ring accepts pushes from a clean start");
    }

    #endregion

    #region Validation

    [Fact]
    public void AZeroCapacity_IsRejected()
    {
        var act = () => new LiquidationRingBuffer(0);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("capacity");
    }

    [Fact]
    public void ANegativeCapacity_IsRejected()
    {
        var act = () => new LiquidationRingBuffer(-1);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("capacity");
    }

    [Fact]
    public void ANegativeCopyCount_IsRejected()
    {
        var ring = new LiquidationRingBuffer(4);

        var act = () => ring.CopyNewestFirst(new List<LiquidationEvent>(), -1);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("maxCount");
    }

    [Fact]
    public void ANullPush_IsRejected()
    {
        var ring = new LiquidationRingBuffer(4);

        var act = () => ring.Push(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    #endregion
}
