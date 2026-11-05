namespace ZettaTrade.UI.Tests.Extensions;

using System;
using System.Collections.Generic;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;
using ZettaTrade.Core.Enums;
using ZettaTrade.UI.Extensions;

/// <summary>
/// The async subscription surface: every notification launches its awaited
/// run strictly serialized in emission order (the SerializeDialog route of
/// order book level presses), and an unhandled stream error reaches
/// exactly one error callback while the handle stays disposable.
/// </summary>
public sealed class ObservableAsyncExtensionsTests
{
    [Fact]
    public void SubscribeAsync_RunsEveryNotificationAwaited_InEmissionOrder()
    {
        var subject = new Subject<int>();
        var runs = new List<int>();
        var errors = new List<Exception>();

        using var subscription = subject.SubscribeAsync(
            item =>
            {
                runs.Add(item);
                return Task.CompletedTask; // the synchronous case every dialog route starts with
            },
            errors.Add);

        subject.OnNext(1);
        subject.OnNext(2);

        runs.Should().Equal([1, 2]);
        errors.Should().BeEmpty();
    }

    [Fact]
    public void SubscribeAsync_RoutesUnhandledStreamError_ToTheErrorCallback()
    {
        var subject = new Subject<decimal>();
        var runs = new List<decimal>();
        var failure = new InvalidOperationException("stream broke");
        var errors = new List<Exception>();

        using var subscription = subject.SubscribeAsync(
            item =>
            {
                runs.Add(item);
                return Task.CompletedTask;
            },
            errors.Add);

        subject.OnNext(10.5m);
        subject.OnError(failure);

        runs.Should().Equal([10.5m]);
        errors.Should().ContainSingle().Which.Should().BeSameAs(failure);
    }

    [Fact]
    public void SubscribeAsync_HotSourceWithoutSubscribers_PublishesNothing()
    {
        // The widget pattern: the Subject may emit before the window wires
        // its click stream (or to nobody at all) — it must not throw.
        var subject = new Subject<OrderSide>();
        var runs = new List<OrderSide>();

        subject.OnNext(OrderSide.Buy);

        using var subscription = subject.SubscribeAsync(
            side =>
            {
                runs.Add(side);
                return Task.CompletedTask;
            },
            _ => { });

        subject.OnNext(OrderSide.Sell);

        runs.Should().Equal([OrderSide.Sell]);
    }

    [Fact]
    public async Task SubscribeAsync_KeepsQueuedRunsParked_UntilThePendingRunCompletes()
    {
        // The serialization contract (BUG-025): three notifications fire
        // while the first run pends — the queued runs start one at a time
        // exactly when their predecessor completes, never concurrently
        // (the old Merge route started all three simultaneously).
        var subject = new Subject<int>();
        var started = new List<int>();
        var errors = new List<Exception>();
        var gates = new TaskCompletionSource[3];
        for (var i = 0; i < gates.Length; i++)
        {
            gates[i] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        using var subscription = subject.SubscribeAsync(
            item =>
            {
                started.Add(item);
                return gates[item - 1].Task;
            },
            errors.Add);

        subject.OnNext(1);
        subject.OnNext(2);
        subject.OnNext(3);

        started.Should().Equal([1]);

        gates[0].SetResult();
        await WaitUntilAsync(() => started.Count >= 2);
        started.Should().Equal([1, 2]);

        gates[1].SetResult();
        await WaitUntilAsync(() => started.Count >= 3);
        started.Should().Equal([1, 2, 3]);
        errors.Should().BeEmpty();
    }

    [Fact]
    public async Task SubscribeAsync_KeepsTheEmissionOrder_WhenRunsCompleteOutOfOrder()
    {
        // The pump advances strictly along the emission order: the last
        // queued run completing first does nothing to the starts, and the
        // parked runs light up exactly when their predecessors finished.
        var subject = new Subject<int>();
        var started = new List<int>();
        var errors = new List<Exception>();
        var gates = new TaskCompletionSource[3];
        for (var i = 0; i < gates.Length; i++)
        {
            gates[i] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        using var subscription = subject.SubscribeAsync(
            item =>
            {
                started.Add(item);
                return gates[item - 1].Task;
            },
            errors.Add);

        subject.OnNext(1);
        subject.OnNext(2);
        subject.OnNext(3);

        started.Should().Equal([1]);

        // The third run completes out of order — nothing may start.
        gates[2].SetResult();
        started.Should().Equal([1]);

        gates[0].SetResult();
        await WaitUntilAsync(() => started.Count >= 2);
        started.Should().Equal([1, 2]);

        gates[1].SetResult();
        await WaitUntilAsync(() => started.Count >= 3);
        started.Should().Equal([1, 2, 3]);
        errors.Should().BeEmpty();
    }

    /// <summary>
    /// Waits (bounded) until the serialized pump has advanced: the
    /// completion-to-start hop rides a task continuation, so the asserts
    /// need an await point. Meditation on the timing never decides — the
    /// pump settles within the window or the test fails.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> met)
    {
        var limit = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!met() && (DateTime.UtcNow < limit))
        {
            await Task.Delay(10);
        }

        met().Should().BeTrue("the serialized pump was expected to advance within the wait window");
    }
}
