namespace ZettaTrade.App.ViewModels;

/// <summary>
/// One per-exchange section of the balances panel: the adapter's name, its
/// availability state and the rows it actually serves. Static state
/// captions (not provided, nothing served) ride the view's localized
/// templates — the record carries only dynamic visibility flags and data.
/// </summary>
public sealed record BalancesSourceViewModel
{
    /// <summary>Adapter display name (e.g. "Paper Trading").</summary>
    public required string AdapterName { get; init; }

    /// <summary>The adapter served balances; the rows show them.</summary>
    public required bool IsProvided { get; init; }

    /// <summary>The adapter carries no trading face; the explicit not-provided state shows instead of any figure.</summary>
    public required bool IsNotProvided { get; init; }

    /// <summary>The balance query failed; the localized fault detail shows instead of any figure.</summary>
    public required bool IsFailed { get; init; }

    /// <summary>Localized fault detail for a failed balance query; null unless <see cref="IsFailed"/>.</summary>
    public required string? FailureMessage { get; init; }

    /// <summary>The rows the adapter actually serves (empty until it serves entries).</summary>
    public required IReadOnlyList<BalanceRowViewModel> Rows { get; init; }

    /// <summary>True while <see cref="Rows"/> is non-empty; drives the table visibility.</summary>
    public bool HasRows => Rows.Count > 0;

    /// <summary>True while the adapter provided balances but served no entries (e.g. nothing traded yet).</summary>
    public bool IsProvidedButEmpty => IsProvided && !HasRows;
}
