namespace ZettaTrade.App.Services;

using System.Collections.Concurrent;
using ZettaTrade.Core.Entities;

/// <summary>
/// Thread-safe in-memory journal of the orders placed during this session.
/// App-lifetime singleton: order placements enqueue, the positions panel
/// reads newest-first snapshots. Capacity is bounded, so the journal never
/// grows without limit during long sessions.
/// </summary>
public sealed class OrderJournal : IOrderJournal
{
    #region Constants

    /// <summary>Maximum number of orders kept in memory.</summary>
    public const int Capacity = 50;

    #endregion Constants

    private readonly ConcurrentQueue<Order> _orders = new();

    /// <inheritdoc/>
    public void RecordPlaced(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        _orders.Enqueue(order);
        while (_orders.Count > Capacity)
        {
            _orders.TryDequeue(out _);
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<Order> GetRecent()
    {
        // Snapshot of the queue; flipping into newest-first order. A placement
        // racing with the copy only decides which entries made it in, never
        // breaks the consumers (they all see an immutable Order[]).
        var snapshot = _orders.ToArray();
        Array.Reverse(snapshot);
        return snapshot;
    }
}
