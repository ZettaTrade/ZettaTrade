namespace ZettaTrade.App.Tests.Services;

using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Tests for the session order journal: newest-first snapshots, bounded
/// capacity with oldest-first eviction, and null-order rejection.
/// </summary>
public sealed class OrderJournalTests
{
    private static Order BuildOrder(string id) => new()
    {
        Id = id,
        ExchangeId = "paper",
        Symbol = Symbol.Parse("BTC/USDT"),
        Side = OrderSide.Buy,
        Type = OrderType.Limit,
        Quantity = new Quantity(0.01m),
        Price = new Price(50_000m),
        Status = OrderStatus.Filled,
        TimeInForce = TimeInForce.GTC,
        CreatedAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void GetRecent_ReturnsNewestFirstSnapshot()
    {
        // Arrange
        var journal = new OrderJournal();
        journal.RecordPlaced(BuildOrder("paper-1"));
        journal.RecordPlaced(BuildOrder("paper-2"));
        journal.RecordPlaced(BuildOrder("paper-3"));

        // Act
        var recent = journal.GetRecent();

        // Assert
        recent.Select(order => order.Id).Should().Equal("paper-3", "paper-2", "paper-1");
    }

    [Fact]
    public void RecordPlaced_BeyondCapacity_EvictsOldest()
    {
        // Arrange
        var journal = new OrderJournal();

        // Act
        for (var index = 0; index < OrderJournal.Capacity + 3; index++)
        {
            journal.RecordPlaced(BuildOrder($"paper-{index}"));
        }

        // Assert: exactly Capacity orders remain — the newest first, the
        // three oldest ("paper-0" .. "paper-2") evicted.
        var recent = journal.GetRecent();
        recent.Should().HaveCount(OrderJournal.Capacity);
        recent[0].Id.Should().Be($"paper-{OrderJournal.Capacity + 2}");
        recent[^1].Id.Should().Be("paper-3");
    }

    [Fact]
    public void RecordPlaced_WithNullOrder_Throws()
    {
        var journal = new OrderJournal();
        var act = () => journal.RecordPlaced(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
