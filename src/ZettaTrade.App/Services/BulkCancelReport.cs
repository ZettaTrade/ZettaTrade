namespace ZettaTrade.App.Services;

using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The best-effort outcome of ONE order inside a bulk cancel pass: the
/// adapter's own id and symbol, the terminal state, and the fault text. One
/// item's fault produces exactly its own report — the pass continues with
/// the rest either way.
/// </summary>
public sealed record BulkCancelReport
{
    /// <summary>Adapter order id.</summary>
    public required string OrderId { get; init; }

    /// <summary>The order's own symbol (the one a cancel command must carry).</summary>
    public required Symbol Symbol { get; init; }

    /// <summary>What happened to this order in the pass.</summary>
    public required BulkCancelReportOutcome Outcome { get; init; }

    /// <summary>The raw fault text for a failed item; null for the other outcomes.</summary>
    public string? Message { get; init; }
}
