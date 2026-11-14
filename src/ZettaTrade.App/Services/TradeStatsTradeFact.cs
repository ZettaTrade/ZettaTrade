namespace ZettaTrade.App.Services;

using System;

/// <summary>
/// The per-trade risk facts the trade-statistics accumulator derives while
/// it books one closing fill (T-251): the trade's own window (the weighted
/// average entry's position opened at <see cref="OpenedAt"/> and closed at
/// <see cref="ClosedAt"/>) and the R-multiple against the journal's stop
/// context when one is present.
/// <para>
/// The list rides the accumulator's realized ledger 1:1 — one fact per
/// booked trade, in the same booking order, keyed for joining with the
/// ledger row by (exchange id, closing order id). Lane-gated closes (T-211)
/// book neither a ledger row nor a fact: the lane filters outcomes, and
/// these are outcomes.
/// </para>
/// <para>
/// The R-multiple speaks the measure ruler's canon (T-200): the signed
/// per-unit realized PnL divided by the |entry − stop| risk distance, null
/// on a missing stop context or a zero risk distance — the division never
/// substitutes a default. The stop context reads the recorded journal
/// triggers verbatim: the closing row's own trigger first (a filled
/// protective stop IS the risk level), then the most recent trigger among
/// the position's opening rows (the ticket's sizer stop riding the entry
/// leg); a stop-entry whose trigger equals its own fill price self-refuses
/// through the same zero-risk guard.
/// </para>
/// </summary>
/// <param name="ClosedAt">When the trade closed (the closing fill's journal time, UTC).</param>
/// <param name="OpenedAt">When the trade's position opened (the opening fill's journal time, UTC; the MAE/MFE window's start).</param>
/// <param name="ExchangeId">Venue the trade rode; the join's key half.</param>
/// <param name="Symbol">Traded symbol (the Core normalized spelling).</param>
/// <param name="ClosingOrderId">The closing journal row's order id; the join's key half.</param>
/// <param name="IsLong">Whether the closed position was long (the MAE/MFE derivation's side).</param>
/// <param name="EntryPrice">The position's weighted-average entry price.</param>
/// <param name="RMultiple">The signed R-multiple against the stop context; null without one.</param>
public sealed record TradeStatsTradeFact(
    DateTimeOffset ClosedAt,
    DateTimeOffset OpenedAt,
    string ExchangeId,
    string Symbol,
    string ClosingOrderId,
    bool IsLong,
    decimal EntryPrice,
    decimal? RMultiple);
