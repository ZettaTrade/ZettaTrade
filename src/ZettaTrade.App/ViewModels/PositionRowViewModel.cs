namespace ZettaTrade.App.ViewModels;

/// <summary>
/// One display row of the positions table in the positions panel. The parent
/// view model precomputes the localized display strings at load time — the
/// row itself is immutable display data, not a localization surface.
/// </summary>
public sealed record PositionRowViewModel
{
    /// <summary>Normalized symbol (BASE/QUOTE).</summary>
    public required string Symbol { get; init; }

    /// <summary>Localized side (Buy = long, Sell = short).</summary>
    public required string Side { get; init; }

    /// <summary>Position quantity, invariant culture.</summary>
    public required string Quantity { get; init; }

    /// <summary>Average entry price, invariant culture.</summary>
    public required string EntryPrice { get; init; }

    /// <summary>Formatted opening timestamp.</summary>
    public required string Opened { get; init; }
}
