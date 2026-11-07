namespace ZettaTrade.Infrastructure.Persistence;

using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The local candle cache served to the chart and the other candle-reading
/// surfaces (T-085). Writes ride WRITE-THROUGH: every adapter-served bar set
/// is persisted keyed (adapter, symbol, interval) and upserted by open time,
/// so overlaps conflate instead of duplicating, and the next start's
/// bootstrap reads a fresh enough history without touching the network.
/// <para>
/// Lives in Infrastructure (like <c>IOrderHistoryStore</c>): persistence is
/// an application service, not a Core contract — Core interfaces stay
/// protected (supervisor-owned) and Core entities are reused as the public
/// shape.
/// </para>
/// </summary>
public interface ICandleCache
{
    /// <summary>
    /// The newest bars of one series, oldest first — the shape the chart's
    /// bootstrap window feeds on. An empty result is the honest cold-cache
    /// answer (nothing persisted yet), never an error.
    /// </summary>
    Task<IReadOnlyList<Bar>> GetLatestAsync(
        string adapterId,
        Symbol symbol,
        TimeFrame timeFrame,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persist one adapter-served bar set: each bar upserts on the composite
    /// key by open time, and the series is trimmed back to the configured
    /// eviction cap. Concurrency-safe: the database side decides
    /// insert-or-update, so racing writers merge instead of colliding.
    /// </summary>
    Task WriteThroughAsync(
        string adapterId,
        Symbol symbol,
        TimeFrame timeFrame,
        IReadOnlyList<Bar> bars,
        CancellationToken cancellationToken = default);
}
