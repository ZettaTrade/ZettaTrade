namespace ZettaTrade.App.Services;

using System;
using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
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

    #region Private Fields

    private readonly ConcurrentQueue<Order> _orders = new();

    /// <summary>The placement announcer; <see cref="Placements"/> hides its observer surface.</summary>
    private readonly Subject<Order> _placements = new();

    /// <summary>The observer-safe face of <see cref="_placements"/>.</summary>
    private readonly IObservable<Order> _placementsStream;

    #endregion Private Fields

    #region Constructor

    /// <summary>
    /// The journal is app-lifetime; the placement stream rides the same
    /// lifetime and needs no disposal.
    /// </summary>
    public OrderJournal()
    {
        _placementsStream = _placements.AsObservable();
    }

    #endregion Constructor

    /// <summary>
    /// The placement stream (T-122): one emit per <see cref="RecordPlaced"/>,
    /// inline on the caller's thread — the flows record AFTER the adapter
    /// accepted, so a subscriber's read of the adapter's surface sees the
    /// placement this very announce carries.
    /// </summary>
    public IObservable<Order> Placements => _placementsStream;

    /// <inheritdoc/>
    public void RecordPlaced(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        _orders.Enqueue(order);
        while (_orders.Count > Capacity)
        {
            _orders.TryDequeue(out _);
        }

        _placements.OnNext(order);
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
