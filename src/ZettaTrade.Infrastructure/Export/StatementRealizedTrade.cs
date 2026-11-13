namespace ZettaTrade.Infrastructure.Export;

using System;

/// <summary>
/// One realized-trade row of the trade statement (T-210): a closing fill's
/// outcome as the fill-pair math booked it — the closed quantity against
/// the position's average entry, the realized PnL in the symbol's quote
/// currency (null when the symbol carries no quote asset — the honest
/// unknown, never a fabricated currency). The App's trade-stats accumulator
/// produces the rows during its scan; the export writers consume them.
/// </summary>
public sealed record StatementRealizedTrade(
    DateTimeOffset ClosedAt,
    string ExchangeId,
    string Symbol,
    string ClosingSide,
    decimal Quantity,
    decimal EntryPrice,
    decimal ExitPrice,
    decimal RealizedPnl,
    string? QuoteCurrency);
