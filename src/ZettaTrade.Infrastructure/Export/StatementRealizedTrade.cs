namespace ZettaTrade.Infrastructure.Export;

using System;

/// <summary>
/// One realized-trade row of the trade statement (T-210): a closing fill's
/// outcome as the fill-pair math booked it — the closed quantity against
/// the position's average entry, the realized PnL in the symbol's quote
/// currency (null when the symbol carries no quote asset — the honest
/// unknown, never a fabricated currency). The App's trade-stats accumulator
/// produces the rows during its scan; the export writers consume them.
/// <para>
/// T-211 riders: <see cref="ClosingOrderId"/> names the journal row the
/// closing fill rode (the trade's stable identity — the note and tags
/// annotation surface keys on it), and <see cref="Note"/>/<see cref="Tags"/>
/// carry the row's user annotations exactly as the journal keeps them. Null
/// annotations are the honest unannotated — the realized CSV's note/tags
/// columns join only when at least one row carries them, so the T-210
/// column shape stays byte-stable when no annotation exists.
/// </para>
/// </summary>
public sealed record StatementRealizedTrade(
    DateTimeOffset ClosedAt,
    string ExchangeId,
    string Symbol,
    string ClosingSide,
    string ClosingOrderId,
    decimal Quantity,
    decimal EntryPrice,
    decimal ExitPrice,
    decimal RealizedPnl,
    string? QuoteCurrency,
    string? Note,
    string? Tags);
