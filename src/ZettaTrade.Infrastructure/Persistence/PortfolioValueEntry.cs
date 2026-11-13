namespace ZettaTrade.Infrastructure.Persistence;

using System;

/// <summary>
/// One persisted portfolio-value snapshot (T-209): the trading account's
/// total value in its quote currency plus the per-asset allocation that
/// sums to it (the allocation rides as a JSON document — one small table
/// stores nothing but the accounting record itself, the schema grows only
/// when the record grows). Keyed by (ExchangeId, TimestampUtc): a value
/// history is per trading account, newest rows win the read.
/// </summary>
public sealed class PortfolioValueEntry
{
    /// <summary>Trading account identifier the snapshot belongs to.</summary>
    public required string ExchangeId { get; init; }

    /// <summary>When the snapshot was taken (UTC).</summary>
    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>The quote currency every value in the row is expressed in.</summary>
    public required string QuoteCurrency { get; init; }

    /// <summary>The portfolio's total value: the priced assets' values summed.</summary>
    public required decimal TotalValue { get; init; }

    /// <summary>
    /// The per-asset allocation as a JSON document (the camelCase
    /// <see cref="PortfolioValueAllocationEntry"/> shape): the priced
    /// entries' values sum to <see cref="TotalValue"/>, the unpriced ones
    /// disclose their amounts and stay out of the total.
    /// </summary>
    public required string AllocationJson { get; init; }
}
