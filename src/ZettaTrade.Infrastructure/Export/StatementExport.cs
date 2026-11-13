namespace ZettaTrade.Infrastructure.Export;

using System;
using System.Collections.Generic;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// One trade statement bundle's payload (T-210): the scanned journal orders
/// inside the statement's filter, the realized-trade ledger the fill-pair
/// math booked from them, the traded surface's balance snapshot taken at
/// export time, the applied filter echoed back, and the generation moment.
/// The service writes the CSV sections plus the JSON manifest from this one
/// immutable shape.
/// </summary>
public sealed record StatementExport(
    DateTimeOffset GeneratedAt,
    string? SymbolFilter,
    string? ExchangeFilter,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    bool? IsPaperFilter,
    IReadOnlyList<Order> Orders,
    IReadOnlyList<StatementRealizedTrade> RealizedTrades,
    IReadOnlyDictionary<string, Money> Balances);
