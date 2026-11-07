namespace ZettaTrade.App.Services;

/// <summary>
/// The composition facts of one placement's confirmation summary (T-078):
/// the venue the order rides and the economics-informed lines it prints —
/// the estimated notional against the hard cap's headroom and the
/// estimated fee. Submit-time snapshots only: the summary composes ONCE
/// from the ticket's final state, and an unknown estimate stays unknown —
/// never a fabricated number.
/// </summary>
/// <param name="IsPaper">True when the order rides the paper adapter.</param>
/// <param name="LiveVenueName">
/// The non-paper venue's display name for the venue tag line.
/// </param>
/// <param name="EstimatedNotionalUsd">
/// The order's estimated notional, null when no anchor prices it.
/// </param>
/// <param name="EstimatedFee">
/// The schedule's estimated commission for the order's role, null when no
/// schedule answered.
/// </param>
public sealed record OrderPlaceFacts(
    bool IsPaper,
    string LiveVenueName,
    decimal? EstimatedNotionalUsd,
    decimal? EstimatedFee);
