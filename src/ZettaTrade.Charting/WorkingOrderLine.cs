namespace ZettaTrade.Charting;

using ZettaTrade.Core.Enums;

/// <summary>
/// One working order's drawing payload the chart overlays (T-122): a
/// full-width dashed price line at the order's own price, colored by its
/// side. Only price-bearing orders are drawn — a market or a trailing
/// stop carries no standing price level, so it owns no line (an honest
/// absence, never an invented level).
/// </summary>
public sealed record WorkingOrderLine
{
    /// <summary>The order's side: the line's color (buy rides the bullish palette, sell the bearish one).</summary>
    public required OrderSide Side { get; init; }

    /// <summary>The standing price level the dashed line anchors at.</summary>
    public required decimal Price { get; init; }
}
