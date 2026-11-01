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

    /// <summary>Localized order type (Market/Limit/…).</summary>
    public required string Type { get; init; }

    /// <summary>Quantity, invariant culture.</summary>
    public required string Quantity { get; init; }

    /// <summary>Order price, invariant culture.</summary>
    public required string Price { get; init; }

    /// <summary>Localized order status.</summary>
    public required string Status { get; init; }
}
