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
/// run in emission order (the SerializeDialog route of order book level
/// presses), and an unhandled stream error reaches exactly one error
/// callback while the handle stays disposable.
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
}
