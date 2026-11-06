namespace ZettaTrade.Infrastructure.Persistence;

using ZettaTrade.Core.Entities;

/// <summary>
/// Read-only, paged queries over the persistent order journal for the
/// trade-statistics dashboard. Keyset paging keeps six-figure histories
/// responsive: every page opens strictly after the previous page's last
/// order instead of skipping a growing offset, so the whole scan stays a
/// bounded number of indexed-seek reads — never a first-load wall.
/// <para>
/// Lives in Infrastructure next to <see cref="IOrderHistoryStore"/>
/// (persistence is an application service, not a Core contract — Core
/// contracts stay untouched; the rows map onto the Core <see cref="Order"/>
/// shape the panels already serve).
/// </para>
/// </summary>
public interface IOrderStatsQuery
{
    /// <summary>
    /// Read the next chronological page of journal orders matching the
    /// filter: ascending by (CreatedAt, ExchangeId, OrderId), strictly after
    /// <paramref name="after"/> — the previous page's last order, null to
    /// start at the scope's beginning. <paramref name="pageSize"/> is
    /// clamped to a bounded window; a page shorter than the requested size
    /// means the scope is exhausted (an empty page reads exhaust too).
    /// </summary>
    Task<IReadOnlyList<Order>> ReadPageAsync(
        OrderStatsFilter filter,
        Order? after,
        int pageSize,
        CancellationToken cancellationToken = default);
}
