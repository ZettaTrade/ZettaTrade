namespace ZettaTrade.Charting;

using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The visual direction of one open position's footprint line (T-122):
/// <see cref="Long"/> renders the entry line with an up-triangle arrow,
/// <see cref="Short"/> a down-triangle one — the sign of the position's
/// own quantity decides, never a guessed default.
/// </summary>
public enum PositionDirection
{
    /// <summary>A positive signed quantity: the entry line reads a long stance.</summary>
    Long = 0,

    /// <summary>A negative signed quantity: the entry line reads a short stance.</summary>
    Short = 1,
}

/// <summary>
/// One open position's drawing payload the chart overlays (T-122): the
/// average entry price the line anchors at, the direction the arrow and
/// the palette carry, and the latest observed market price with its
/// unrealized PnL the floating tag snaps to. Latest price is absent until
/// the feed has observed one — the tag draws nothing rather than inventing
/// a price, exactly the PnL engine's own honesty.
/// </summary>
public sealed record PositionOverlay
{
    /// <summary>The stance the direction arrow and the line's color carry.</summary>
    public required PositionDirection Direction { get; init; }

    /// <summary>Quantity-weighted average entry price of the open position.</summary>
    public required decimal EntryPrice { get; init; }

    /// <summary>The latest market price observed for the symbol, if any.</summary>
    public decimal? LatestPrice { get; init; }

    /// <summary>
    /// Unrealized PnL at <see cref="LatestPrice"/> (zero while no price was
    /// observed): the floating tag prints its amount and quote currency.
    /// </summary>
    public Money UnrealizedPnl { get; init; }
}
