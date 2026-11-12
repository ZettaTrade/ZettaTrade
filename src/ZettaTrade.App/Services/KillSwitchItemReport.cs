namespace ZettaTrade.App.Services;

using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The best-effort outcome of ONE item inside a kill-switch sweep (T-188):
/// the venue it rode, its symbol, the order id when one exists (the
/// cancelled order, or the placed exit), the terminal state and the fault
/// text. One item's fault produces exactly its own report — the sweep
/// continues with the rest either way.
/// </summary>
public sealed record KillSwitchItemReport
{
    /// <summary>The venue display name the item rode (the summary groups by it).</summary>
    public required string VenueName { get; init; }

    /// <summary>The item's symbol (the order's own, or the position's).</summary>
    public required Symbol Symbol { get; init; }

    /// <summary>The cancelled order's id, or the placed exit's id; null when none was reached.</summary>
    public string? OrderId { get; init; }

    /// <summary>What happened to this item in the sweep.</summary>
    public required KillSwitchItemOutcome Outcome { get; init; }

    /// <summary>The raw fault text for a failed item; null for the other outcomes.</summary>
    public string? Message { get; init; }
}
