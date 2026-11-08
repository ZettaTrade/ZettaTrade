namespace ZettaTrade.Charting;

using ZettaTrade.Core.Entities;

/// <summary>
/// The visual role of a strategy-signal marker registered on the chart
/// (T-112): <see cref="Long"/> renders an up-triangle above the candle's
/// close level (a bullish stance read), <see cref="Flat"/> a down-triangle
/// below it (a stand-aside/exit read). The enum mirrors its own layer on
/// purpose: Charting stays a leaf and does not reference the strategies
/// module, so the marker carries a role value the App maps from the
/// strategy stance.
/// </summary>
public enum SignalMarkerRole
{
    /// <summary>Bullish stance at the marker's candle, drawn as an up-triangle.</summary>
    Long = 0,

    /// <summary>Stand-aside stance at the marker's candle, drawn as a down-triangle.</summary>
    Flat = 1,
}

/// <summary>
/// One strategy-signal marker the chart overlays: anchored to the signal's
/// own closed candle (addressed by its open time, carrying the candle's
/// close price the marker level maps onto) and the stance's visual role.
/// Placement is exact by construction: the draw pass resolves the marker's
/// candle in the displayed series by open time and rides the same bar-slot
/// and price mapping the candles use, so a marker can never sit next to
/// its signal's candle.
/// </summary>
/// <param name="Candle">The closed candle the signal was stamped on.</param>
/// <param name="Role">The stance's visual role (up-triangle or down-triangle).</param>
public readonly record struct SignalMarker(Bar Candle, SignalMarkerRole Role);
