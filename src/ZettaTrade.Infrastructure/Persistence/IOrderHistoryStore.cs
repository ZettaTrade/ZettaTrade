namespace ZettaTrade.Infrastructure.Persistence;

using ZettaTrade.Core.Entities;

/// <summary>
/// Persistent order journal backed by SQLite. Records every order the
/// adapter accepted and the subsequent cancellations, so the order history
/// view survives application restarts.
/// <para>
/// Lives in Infrastructure (like <c>IShellUtils</c>): persistence is an
/// application service, not a Core contract — Core interfaces stay protected
/// (supervisor-owned) and Core entities are reused as the public shape.
/// </para>
/// </summary>
public interface IOrderHistoryStore
{
    /// <summary>Insert or update the journal row for an accepted order.</summary>
    Task RecordPlacedAsync(Order order, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark an already recorded order cancelled at the given timestamp.
    /// Best-effort: a missing row is logged and ignored, never thrown.
    /// </summary>
    Task MarkCancelledAsync(
        string exchangeId,
        string orderId,
        DateTimeOffset cancelledAt,
        CancellationToken cancellationToken = default);

    /// <summary>The most recent journal rows, newest first.</summary>
    Task<IReadOnlyList<Order>> GetRecentAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete every journal row recorded for one exchange id and return how
    /// many rows left (the paper account reset's persistence half, T-192):
    /// only the named venue's rows leave, every other exchange's journal of
    /// record stays untouched.
    /// </summary>
    Task<int> DeleteExchangeAsync(string exchangeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Write the user's trade note and tags onto one journal row (T-211).
    /// Both fields are replaced whole (a null clears — the honest removal),
    /// and the row's other fields stay untouched: this is an annotation
    /// write, never an order rewrite. Returns false when no such row is
    /// recorded (the honest no-op — the caller decides what the surface
    /// shows).
    /// </summary>
    Task<bool> SetTradeMetaAsync(
        string exchangeId,
        string orderId,
        string? note,
        string? tags,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Read the trade meta for the named journal rows (T-211): one bulk
    /// query, rows with no recorded note and no tags simply don't come back.
    /// The result order is unspecified — key the entries by
    /// (ExchangeId, OrderId) on the caller's side.
    /// </summary>
    Task<IReadOnlyList<OrderTradeMeta>> GetTradeMetaAsync(
        IReadOnlyList<(string ExchangeId, string OrderId)> keys,
        CancellationToken cancellationToken = default);
}
