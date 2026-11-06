namespace ZettaTrade.App.ViewModels;

/// <summary>
/// One display row of the balances tables (a per-exchange entry or an
/// aggregated one). The parent view model precomputes the display strings
/// at load time — the row itself is immutable display data, not a
/// localization surface.
/// </summary>
public sealed record BalanceRowViewModel
{
    /// <summary>Asset code (e.g. "USDT").</summary>
    public required string Asset { get; init; }

    /// <summary>Balance amount, invariant culture (the ru-locale decimal trap).</summary>
    public required string Amount { get; init; }
}
