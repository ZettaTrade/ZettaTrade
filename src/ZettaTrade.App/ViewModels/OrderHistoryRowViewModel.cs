namespace ZettaTrade.App.ViewModels;

/// <summary>
/// One display row of the order history window. The parent view model
/// precomputes the localized display strings at load time — the row itself
/// is immutable display data, not a localization surface.
/// </summary>
public sealed record OrderHistoryRowViewModel
{
    /// <summary>Formatted creation timestamp.</summary>
    public required string Opened { get; init; }

    /// <summary>Exchange identifier the order was placed on.</summary>
    public required string Exchange { get; init; }

    /// <summary>Normalized symbol (BASE/QUOTE).</summary>
    public required string Symbol { get; init; }

    /// <summary>Localized side (Buy/Sell).</summary>
    public required string Side { get; init; }

    /// <summary>
    /// True for a buy order: drives the side chip's state class (T-157) —
    /// the same long/short reading the localized side text carries.
    /// </summary>
    public required bool IsBuySide { get; init; }

    /// <summary>
    /// True for a sell order: the chip's other state class. OrderSide is
    /// Buy/Sell only, so this is exactly the negation.
    /// </summary>
    public bool IsSellSide => !IsBuySide;

    /// <summary>Localized order type (Market/Limit/…).</summary>
    public required string Type { get; init; }

    /// <summary>Quantity, invariant culture.</summary>
    public required string Quantity { get; init; }

    /// <summary>Order price, invariant culture.</summary>
    public required string Price { get; init; }

    /// <summary>Localized order status.</summary>
    public required string Status { get; init; }

    /// <summary>
    /// Commission the order's execution carried, invariant-formatted; an
    /// order without recorded execution fee data shows the localized dash —
    /// an honest unknown, never a fabricated zero.
    /// </summary>
    public required string Fee { get; init; }

    /// <summary>
    /// The order's execution flags as a localized list (post-only,
    /// reduce-only). An empty cell is the honest "no flags": an order
    /// placed without either contract shows nothing, never a dash.
    /// </summary>
    public required string Flags { get; init; }

    /// <summary>
    /// The journal row's user trade note (T-211); null is the honest
    /// no-note. An applied edit replaces the row instance.
    /// </summary>
    public string? Note { get; init; }

    /// <summary>
    /// The journal row's canonical comma-joined tag list (T-211); null is
    /// the honest no-tags. An applied edit replaces the row instance.
    /// </summary>
    public string? Tags { get; init; }

    /// <summary>The row's order id; the annotation write's key half.</summary>
    public required string OrderId { get; init; }
}
