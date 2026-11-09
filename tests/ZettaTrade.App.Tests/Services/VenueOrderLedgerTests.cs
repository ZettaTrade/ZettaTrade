namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Core.Entities;
using Core.Enums;
using Core.ValueObjects;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// Tests for the venue order journal reflector (T-127): every update the
/// venue stream publishes is recorded into the persistent journal in order
/// (an insert-or-update upsert the session writers stay collision-free
/// with), and a failed record is logged and never re-raised — the journal's
/// own corruption must not poison the live trading loop.
/// </summary>
public sealed class VenueOrderLedgerTests
{
    /// <summary>The bounded poll of these facts: the pump settles on another thread.</summary>
    private static readonly TimeSpan PollBudget = TimeSpan.FromSeconds(2);

    private readonly IOrderHistoryStore _history = Substitute.For<IOrderHistoryStore>();
    private readonly Subject<Order> _updates = new();

    private VenueOrderLedger CreateLedger() => new(
        _updates,
        _history,
        NullLogger<VenueOrderLedger>.Instance);

    private static Order NewOrder(string id, OrderStatus status, decimal filled) => new()
    {
        Id = id,
        ExchangeId = "bybit",
        ExchangeOrderId = $"venue-{id}",
        Symbol = Symbol.Parse("BTC/USDT"),
        Side = OrderSide.Buy,
        Type = OrderType.Limit,
        Quantity = new Quantity(1m),
        Price = new Price(100m),
        TimeInForce = TimeInForce.GTC,
        Status = status,
        FilledQuantity = new Quantity(filled),
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var start = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - start > PollBudget)
            {
                throw new TimeoutException("The venue ledger's pump did not settle within the budget.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }
    }

    [Fact]
    public async Task EveryVenueTransition_IsRecordedIntoTheJournalInOrder()
    {
        await using var ledger = CreateLedger();

        var recorded = new List<Order>();
        _history
            .RecordPlacedAsync(Arg.Do<Order>(recorded.Add), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        _updates.OnNext(NewOrder("o-1", OrderStatus.Submitted, 0m));
        _updates.OnNext(NewOrder("o-1", OrderStatus.PartiallyFilled, 0.4m));
        _updates.OnNext(NewOrder("o-1", OrderStatus.Filled, 1m));

        await WaitUntilAsync(() => recorded.Count >= 3);

        recorded.Should().HaveCount(3);
        recorded.Select(order => order.Status).Should().Equal(
            OrderStatus.Submitted,
            OrderStatus.PartiallyFilled,
            OrderStatus.Filled);
    }

    [Fact]
    public async Task AFailedRecord_IsLoggedAndNeverReraised()
    {
        await using var ledger = CreateLedger();

        var calls = 0;
        _history
            .RecordPlacedAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                calls++;
                if (calls <= 2)
                {
                    throw new InvalidOperationException("the journal is sealed");
                }

                return Task.CompletedTask;
            });

        _updates.OnNext(NewOrder("o-1", OrderStatus.Submitted, 0m));
        _updates.OnNext(NewOrder("o-2", OrderStatus.Submitted, 0m));
        _updates.OnNext(NewOrder("o-3", OrderStatus.Submitted, 0m));

        // The two failed records never re-raise; the third still records —
        // the live loop keeps flowing across the journal's fault window.
        await WaitUntilAsync(() => calls >= 3);
        calls.Should().Be(3);
    }
}
