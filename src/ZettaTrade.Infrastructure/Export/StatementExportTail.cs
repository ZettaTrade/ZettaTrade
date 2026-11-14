namespace ZettaTrade.Infrastructure.Export;

using System;
using System.Collections.Generic;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// One trade statement's end-of-scan tail (T-219): the generation moment,
/// the fill-pair realized-trade ledger the accumulator booked across the
/// scan, and the traded surface's balance snapshot taken at export time —
/// the parts that only exist once the scan is over. CompleteAsync writes
/// them after the streamed sections and then the manifest LAST.
/// </summary>
public sealed record StatementExportTail(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<StatementRealizedTrade> RealizedTrades,
    IReadOnlyDictionary<string, Money> Balances);
