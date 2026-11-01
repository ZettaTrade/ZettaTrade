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
    /// True only for statuses a cancel request can act on (Pending,
    /// Submitted, PartiallyFilled); drives the Cancel button enable state.
    /// </summary>
    public required bool IsCancellable { get; init; }

    /// <summary>
    /// Per-row cancel command, created and wired by the parent view model;
    /// it closes over the full <c>Order</c> so the cancel request always
    /// carries the adapter's own symbol and id.
    /// </summary>
    public required ReactiveCommand<RxVoid, RxVoid> CancelCommand { get; init; }
}
