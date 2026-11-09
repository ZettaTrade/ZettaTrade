namespace ZettaTrade.App.Tests.Services;

using System;
using System.Reactive.Subjects;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The performance HUD sampler's lifecycle (T-108): OFF — the shipped
/// default — the sampler holds nothing (the book stream has no observer,
/// the IsRunning gate is false): the zero-trace contract the off-state
/// test pins. START — the sampler observes the book stream, drains its
/// counters per tick and emits the snapshot's readouts.
/// </summary>
public sealed class PerformanceHudServiceTests
{
    /// <summary>A book snapshot stamped a moment ago (a fresh delivery).</summary>
    private static OrderBook FreshBook() => new()
    {
        Symbol = Symbol.Parse("BTC/USDT"),
        Bids = [],
        Asks = [],
        Timestamp = DateTimeOffset.UtcNow,
    };

    /// <summary>A book snapshot stamped in the far past (an aged delivery — the histogram's overflow bucket).</summary>
    private static OrderBook StaleBook() => new()
    {
        Symbol = Symbol.Parse("BTC/USDT"),
        Bids = [],
        Asks = [],
        Timestamp = DateTimeOffset.UtcNow.AddMinutes(-5),
    };

    [Fact]
    public void TheFreshSampler_IsOff_AndHoldsNoBookSubscription()
    {
        var books = new Subject<OrderBook>();
        var service = new PerformanceHudService(
            books,
            null,
            8192,
            NullLogger<PerformanceHudService>.Instance);

        service.IsRunning.Should().BeFalse("the HUD ships off");
        books.HasObservers.Should().BeFalse("the off state holds no subscription — zero traces");
        service.Snapshots.Should().NotBeNull("the lane exists for the ViewModel's later legs");
    }

    [Fact]
    public void StopWithoutStart_IsANoOp()
    {
        var books = new Subject<OrderBook>();
        var service = new PerformanceHudService(
            books,
            null,
            8192,
            NullLogger<PerformanceHudService>.Instance);

        service.Stop();

        service.IsRunning.Should().BeFalse();
        books.HasObservers.Should().BeFalse();
    }

    [Fact]
    public void Start_SubscribesTheBookStream_AndADoubleStartDoesNotDoubleIt()
    {
        var books = new Subject<OrderBook>();
        var service = new PerformanceHudService(
            books,
            null,
            8192,
            NullLogger<PerformanceHudService>.Instance);

        service.Start();
        service.Start();

        service.IsRunning.Should().BeTrue();
        books.HasObservers.Should().BeTrue("the running sampler taps the book stream");

        service.Stop();
        books.HasObservers.Should().BeFalse("a double start never doubles the legs — one stop kills them all");
    }

    [Fact]
    public void TheTick_DrainsTheCounters_IntoOneSnapshot()
    {
        var books = new Subject<OrderBook>();
        var service = new PerformanceHudService(
            books,
            () => 3,
            8192,
            NullLogger<PerformanceHudService>.Instance);
        service.ChartFrameDrain = () => 60;

        PerformanceSnapshot? sample = null;
        using var leg = service.Snapshots.Subscribe(s => sample = s);

        service.Start();
        books.OnNext(FreshBook());
        books.OnNext(FreshBook());
        books.OnNext(StaleBook());
        service.EmitSample();

        sample.Should().NotBeNull("one tick emits one snapshot");
        sample!.ChartFps.Should().Be(60, "the drained frame count over the 1 s window");
        sample.BookUpdatesPerSecond.Should().Be(3, "the drained book count");
        sample.WorkingSetBytes.Should().BeGreaterThan(0, "the process's working set is a real reading");
        sample.BackpressureBacklog.Should().Be(3, "the probe's depth at the tick");
        sample.BackpressureCapacity.Should().Be(8192);
        sample.LatencyBuckets.Should().HaveCount(6, "five bounded edges plus the overflow");
        sample.LatencyBuckets[0].Should().Be(2, "the fresh deliveries sit in the fastest bucket");
        sample.LatencyBuckets[^1].Should().Be(1, "the stale delivery overflows every edge");
        sample.LatencyBoundsMs.Should().Equal([1, 5, 20, 100, 500]);

        // The drains reset: the next tick samples a fresh window.
        service.EmitSample();
        sample!.BookUpdatesPerSecond.Should().Be(0, "the counters drained");
        sample.LatencyBuckets[0].Should().Be(0);

        service.Stop();
    }

    [Fact]
    public void TheTickWithoutADrain_ReadsAQuietChart()
    {
        var books = new Subject<OrderBook>();
        var service = new PerformanceHudService(
            books,
            null,
            8192,
            NullLogger<PerformanceHudService>.Instance);

        PerformanceSnapshot? sample = null;
        using var leg = service.Snapshots.Subscribe(s => sample = s);

        service.EmitSample();

        sample!.ChartFps.Should().Be(0, "no drain attached — the chart reads quiet, not broken");
        sample.BackpressureBacklog.Should().Be(0, "no probe attached");
    }
}
