namespace ZettaTrade.App.Services;

using ZettaTrade.Core.Entities;

/// <summary>
/// In-memory journal of the orders placed during this application session.
/// Fed by the new-order dialog after each successful placement; feeds the
/// "recent orders" section of the positions panel. This is deliberately
/// session-scoped: the persistent, adapter-sourced order history is a
/// separate concern that does not belong to the trading adapters.
/// </summary>
public interface IOrderJournal
{
    /// <summary>Record an order that the trading adapter accepted.</summary>
    void RecordPlaced(Order order);

    /// <summary>Snapshot of the recent orders, newest first.</summary>
    IReadOnlyList<Order> GetRecent();
}
