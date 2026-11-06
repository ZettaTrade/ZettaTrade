namespace ZettaTrade.App.Services;

/// <summary>
/// One point-in-time balances view: the per-exchange outcomes exactly as the
/// adapters served them, plus the per-asset aggregate over the providing
/// sources. The panel reads both sides from this single snapshot, so a
/// switch of any source can never tear the two sections apart.
/// </summary>
public sealed record BalancesSnapshot
{
    /// <summary>Per-exchange availability outcomes, in composition order.</summary>
    public required IReadOnlyList<BalanceSource> Sources { get; init; }

    /// <summary>Per-asset sums across the sources that actually serve balances.</summary>
    public required IReadOnlyList<AggregatedBalance> Aggregated { get; init; }
}
