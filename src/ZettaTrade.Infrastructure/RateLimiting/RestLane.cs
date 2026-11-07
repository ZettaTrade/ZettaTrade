namespace ZettaTrade.Infrastructure.RateLimiting;

/// <summary>
/// The traffic lane of an outbound REST request (T-106): whether the request
/// serves an interactive user action or venue background work. The limiter
/// budgets the lanes from separate pools so a background burst (the T-086
/// deep-history backfill) can never starve or delay a user submit — the
/// whole point of the seam.
/// </summary>
public enum RestLane
{
    /// <summary>An interactive request (chart load, symbol search refresh): served from the main pool while tokens remain.</summary>
    User,

    /// <summary>A venue background request (deep-history backfill, catalog repair): served only from the capped background slack.</summary>
    Background,
}

/// <summary>
/// Lane resolution helpers for the adapters' call sites: the Core
/// <c>GetBarsAsync</c> face carries no lane parameter (Core is untouched),
/// so the lane is classified adapter-side from the request's own shape — an
/// interactive tail load reaches the freshest edge, background fetching
/// (the T-086 gap walk) requests ranges that end well in the past.
/// </summary>
public static class RestLaneResolver
{
    /// <summary>
    /// How close a bar request's newest edge must sit to the present to
    /// count as an interactive tail load (<see cref="RestLane.User"/>): the
    /// adapters' default end-time is "now", and a live chart load reaches
    /// the freshest open. Requests ending deeper in the past are background
    /// fetches — the T-086 gap walk whose segments stitch history — and
    /// take <see cref="RestLane.Background"/> with its capped slack. The
    /// classification is intentionally conservative: ambiguity defaults to
    /// the user lane, so background never gains user priority by error.
    /// </summary>
    public static readonly TimeSpan TailRequestGrace = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Classify the lane of a bar-range request by its newest edge: a range
    /// ending at (or inside the grace of) the present is an interactive
    /// tail load riding <see cref="RestLane.User"/>; a range ending in the
    /// past is background history stitching riding
    /// <see cref="RestLane.Background"/>.
    /// </summary>
    /// <param name="requestedTo">The request's end boundary (null means "to now", the adapters' default).</param>
    /// <param name="now">The classification stamp (the injected clock in facts; the adapter passes the wall clock).</param>
    /// <returns>The classified lane.</returns>
    public static RestLane LaneForBarRange(DateTimeOffset? requestedTo, DateTimeOffset now) =>
        requestedTo is null || (now - requestedTo.Value) <= TailRequestGrace
            ? RestLane.User
            : RestLane.Background;
}
