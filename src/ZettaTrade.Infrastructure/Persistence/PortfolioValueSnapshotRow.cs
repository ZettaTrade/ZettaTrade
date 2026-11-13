namespace ZettaTrade.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// One portfolio-value snapshot as the store hands it over (T-209): the
/// row's accounting facts with the allocation still riding as its JSON
/// document. The consumer parses the allocation through
/// <see cref="PortfolioValueAllocationEntry.ParseAllocation"/> — the store
/// itself never deserializes what it wrote.
/// </summary>
public sealed record PortfolioValueSnapshotRow(
    string ExchangeId,
    DateTimeOffset TimestampUtc,
    string QuoteCurrency,
    decimal TotalValue,
    string AllocationJson);

/// <summary>
/// One allocation entry inside a snapshot's JSON document: the asset's
/// amount, the price it was valued at (null when unpriced) and its value
/// in the row's quote currency (null when unpriced — an unpriced asset
/// discloses its amount and stays out of the total; a snapshot never
/// invents a price). The JSON shape is camelCase on purpose: the
/// allocation is a stored document a consumer deserializes, and the
/// canonical wire shape keeps the keys unambiguous.
/// </summary>
public sealed record PortfolioValueAllocationEntry(
    string Asset,
    decimal Amount,
    decimal? Price,
    decimal? Value,
    bool Priced)
{
    /// <summary>Serialize the allocation list to its JSON document shape.</summary>
    public static string SerializeAllocation(IReadOnlyList<PortfolioValueAllocationEntry> entries) =>
        System.Text.Json.JsonSerializer.Serialize(
            entries,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });

    /// <summary>
    /// Parse the allocation document back; a malformed document throws —
    /// the caller discloses the fault instead of reading a fabricated list.
    /// </summary>
    public static IReadOnlyList<PortfolioValueAllocationEntry> ParseAllocation(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<List<PortfolioValueAllocationEntry>>(
            json,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
            })!;

    /// <summary>The priced entries' values summed: the allocation's own total.</summary>
    public static decimal PricedTotal(IEnumerable<PortfolioValueAllocationEntry> entries) =>
        entries.Where(entry => entry.Priced).Sum(entry => entry.Value!.Value);
}
