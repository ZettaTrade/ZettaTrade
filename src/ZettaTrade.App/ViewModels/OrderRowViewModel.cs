namespace ZettaTrade.App.ViewModels;

using ReactiveUI;
using ReactiveUI.Primitives;

/// <summary>
/// One display row of the orders tables in the positions panel. The parent
/// view model precomputes the localized display strings at load time — the
/// row itself is immutable display data, not a localization surface.
/// </summary>
public sealed record OrderRowViewModel
{
    /// <summary>Adapter order id, stable across sections (journal vs open orders).</summary>
    public required string OrderId { get; init; }

    /// <summary>Normalized symbol (BASE/QUOTE).</summary>
    public required string Symbol { get; init; }

    /// <summary>Localized side (Buy/Sell).</summary>
    public required string Side { get; init; }

    /// <summary>Quantity, invariant culture.</summary>
    public required string Quantity { get; init; }

    /// <summary>Order price, invariant culture.</summary>
    public required string Price { get; init; }

    /// <summary>Localized order status.</summary>
    public required string Status { get; init; }

    /// <summary>Formatted creation timestamp.</summary>
    public required string Opened { get; init; }

    /// <summary>
    /// Commission the executed fill carried, invariant-formatted; an order
    /// without recorded execution fee data shows the localized dash — an
    /// honest unknown, never a fabricated zero.
    /// </summary>
    public required string Fee { get; init; }

    /// <summary>
    /// True only for statuses a cancel request can act on (Pending,
    /// Submitted, PartiallyFilled); drives the Cancel button enable state.
    /// </summary>
    public required bool IsCancellable { get; init; }

    /// <summary>
    /// True only for orders the amend flow can act on: a working status
    /// (Pending, Submitted, PartiallyFilled) AND an amendable type. Drives
    /// the Amend button enable state.
    /// </summary>
    public required bool IsAmendable { get; init; }

    /// <summary>
    /// True while the row's amend flow is in flight: the row stays in the
    /// list and shows the amending state until the replace settles.
    /// </summary>
    public required bool IsAmending { get; init; }

    /// <summary>
    /// Per-row cancel command, created and wired by the parent view model;
    /// it closes over the full <c>Order</c> so the cancel request always
    /// carries the adapter's own symbol and id.
    /// </summary>
    public required ReactiveCommand<RxVoid, RxVoid> CancelCommand { get; init; }

    /// <summary>
    /// Per-row amend command, created and wired by the parent view model;
    /// it closes over the full <c>Order</c> so the amend flow always reads
    /// the adapter's fresh state for the right target.
    /// </summary>
    public required ReactiveCommand<RxVoid, RxVoid> AmendCommand { get; init; }
}
