namespace ZettaTrade.UI.Charting;

using System;
using System.Collections.Generic;

/// <summary>
/// Pure price-domain transforms behind the chart's Y-axis scale modes
/// (T-090). The mapping pipeline stays linear: the draw pass computes the
/// padded spread and every pixel mapping in the TRANSFORMED domain (the
/// values <see cref="ChartPriceScale"/> already consumes), and axis values
/// come back to prices only at the edges — the axis labels and the hover
/// pill — so a mode is one decision at the spread and a wrap at every call
/// site, never a parallel rendering path. Guards follow the house
/// refused-not-defaulted rule adapted for rendering: a value outside a
/// mode's domain clamps to the domain's floor and DRAWS at the clamped
/// position (visible, hugging the scale's bottom), never throws and never
/// silently substitutes another mode.
/// </summary>
public static class ChartPriceTransform
{
    /// <summary>
    /// The log domain's floor when a window has NO positive price at all
    /// (every visible candle corrupt): log10(1) = 0 keeps the transform
    /// defined and every candle drawn at one visible level.
    /// </summary>
    public const decimal LogFallbackFloor = 1m;

    /// <summary>
    /// The smallest positive price among the visible candidates — the
    /// domain floor the log mode clamps non-positive prices to, so an
    /// invalid candle keeps drawing (visibly clamped) instead of dropping
    /// out or flipping the mode. When no candidate is positive the
    /// fallback floor keeps the domain defined.
    /// </summary>
    public static decimal PositiveFloor(IReadOnlyList<decimal> prices)
    {
        decimal floor = 0m;

        foreach (var price in prices)
        {
            if (price > 0m && (floor == 0m || price < floor))
            {
                floor = price;
            }
        }

        return floor > 0m ? floor : LogFallbackFloor;
    }

    /// <summary>
    /// The axis-domain value of one raw price: the identity for the linear
    /// mode, the base-10 logarithm for the log mode (a non-positive price
    /// clamps to <paramref name="floor"/> — the visible clamp), and the
    /// percent change over <paramref name="anchor"/> for the percent mode.
    /// The anchor is the window's first visible close; a corrupt anchor
    /// (zero or negative — it cannot reference a ratio) clamps to the
    /// floor the same way, so the percentages stay defined and visibly
    /// extreme instead of silently reading linear.
    /// </summary>
    public static decimal ToAxisValue(
        decimal price,
        PriceScaleMode mode,
        decimal anchor,
        decimal floor) => mode switch
        {
            PriceScaleMode.Logarithmic => (decimal)Math.Log10((double)ClampToFloor(price, floor)),
            PriceScaleMode.Percent => Percent(price, anchor, floor),
            _ => price,
        };

    /// <summary>
    /// The raw price behind one axis-domain value — the hover pill and the
    /// log-mode axis labels read prices, not domain values. The percent
    /// mode's inverse maps a percent-domain value back onto the raw price
    /// space (the anchored reference applied).
    /// </summary>
    public static decimal ToPrice(
        decimal value,
        PriceScaleMode mode,
        decimal anchor,
        decimal floor) => mode switch
        {
            PriceScaleMode.Logarithmic => (decimal)Math.Pow(10d, (double)value),
            PriceScaleMode.Percent => PercentAnchor(anchor, floor) * (1m + (value / 100m)),
            _ => value,
        };

    /// <summary>
    /// The percent read of one price over the anchored reference; the
    /// anchor clamps to the floor first (a corrupt anchor cannot
    /// reference a ratio).
    /// </summary>
    private static decimal Percent(decimal price, decimal anchor, decimal floor)
    {
        var reference = ClampToFloor(anchor, floor);

        return (price - reference) / reference * 100m;
    }

    /// <summary>The effective percent reference (the clamped anchor).</summary>
    private static decimal PercentAnchor(decimal anchor, decimal floor) => ClampToFloor(anchor, floor);

    /// <summary>Clamp into the positive domain: anything below the floor snaps up to it.</summary>
    private static decimal ClampToFloor(decimal price, decimal floor) => price < floor ? floor : price;
}
