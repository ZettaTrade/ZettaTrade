namespace ZettaTrade.Infrastructure.Export;

using System;

/// <summary>
/// One trade statement's filter echo (T-219): the filter applied to the
/// scan that feeds the bundle, carried at BeginAsync and written into the
/// manifest's filter block verbatim — a null rides as the explicit null
/// (the honest absent, the materialized shape's manifest bytes).
/// </summary>
public sealed record StatementExportHeader(
    string? SymbolFilter,
    string? ExchangeFilter,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    bool? IsPaperFilter);
