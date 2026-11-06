namespace ZettaTrade.App.Services;

/// <summary>
/// One summed balance row of the aggregated view: the asset total across
/// every source that serves that asset. Sources that do not provide
/// balances contribute nothing — never a fabricated zero.
/// </summary>
public sealed record AggregatedBalance
{
    /// <summary>Asset code (e.g. "USDT").</summary>
    public required string Asset { get; init; }

    /// <summary>Summed amount of the asset across the providing sources.</summary>
    public required decimal Amount { get; init; }
}
