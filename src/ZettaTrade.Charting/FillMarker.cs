namespace ZettaTrade.Charting;

using ZettaTrade.Core.Enums;

/// <summary>
/// One execution's drawing payload the chart overlays (T-122): a small
/// diamond anchored at the fill's own price and at the candle the fill
/// time fell into — same placement discipline the signal markers keep
/// (T-112), keyed by time instead of a stance's open.
/// </summary>
public sealed record FillMarker
{
    /// <summary>The moment the fill executed (UTC).</summary>
    public required DateTimeOffset Time { get; init; }

    /// <summary>The fill's side: the marker's color (buy rides the bullish palette, sell the bearish one).</summary>
    public required OrderSide Side { get; init; }

    /// <summary>The executed price the marker anchors at.</summary>
    public required decimal Price { get; init; }
}
