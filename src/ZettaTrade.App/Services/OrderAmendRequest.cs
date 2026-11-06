namespace ZettaTrade.App.Services;

using ZettaTrade.Core.Entities;

/// <summary>
/// User request to amend a working order: the order the amend targets plus
/// the dialog's edits. The carried <see cref="Working"/> order is only the
/// target handle — the amend flow re-reads the adapter's current state and
/// never trusts the possibly stale display row.
/// </summary>
public sealed record OrderAmendRequest
{
    /// <summary>The order the amend targets, as displayed when the flow started.</summary>
    public required Order Working { get; init; }

    /// <summary>The dialog's amended quantity (already parsed with the invariant culture).</summary>
    public required decimal NewQuantity { get; init; }

    /// <summary>
    /// The dialog's amended limit price; null keeps the original limit
    /// price. Only limit and stop-limit orders accept a price edit — the
    /// flow rejects the request otherwise instead of silently dropping it.
    /// </summary>
    public decimal? NewLimitPrice { get; init; }
}
