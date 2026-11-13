namespace ZettaTrade.App.ViewModels;

/// <summary>
/// One display row of the latest value snapshot's allocation (T-209): the
/// asset's amount, its value in the snapshot's quote currency and the
/// honest unpriced state — an unpriced asset discloses its amount with a
/// localized not-priced value cell, never a fabricated figure. The parent
/// view model precomputes the display strings at load time — the row
/// itself is immutable display data, not a localization surface.
/// </summary>
public sealed record BalanceAllocationRowViewModel
{
    /// <summary>Asset code (e.g. "BTC").</summary>
    public required string Asset { get; init; }

    /// <summary>Balance amount, invariant culture (the ru-locale decimal trap).</summary>
    public required string Amount { get; init; }

    /// <summary>Value in the snapshot's quote currency, invariant culture; null when unpriced.</summary>
    public required string? Value { get; init; }

    /// <summary>True when no ticker priced the asset: the value cell shows the localized not-priced state.</summary>
    public required bool IsUnpriced { get; init; }
}
