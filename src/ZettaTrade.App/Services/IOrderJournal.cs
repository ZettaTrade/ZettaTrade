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
    /// <summary>The placement stream: every <see cref="RecordPlaced"/> announce lands here.</summary>
    IObservable<Order> Placements { get; }

    /// <summary>Record an order that the trading adapter accepted.</summary>
    void RecordPlaced(Order order);

    /// <summary>Snapshot of the recent orders, newest first.</summary>
    IReadOnlyList<Order> GetRecent();

    /// <summary>
    /// Drain the session journal of one venue's rows (the paper account
    /// reset's session half, T-192): only the named exchange's placements
    /// leave, the other venues' rows keep their order.
    /// </summary>
    void RemoveVenue(string exchangeId);
}
