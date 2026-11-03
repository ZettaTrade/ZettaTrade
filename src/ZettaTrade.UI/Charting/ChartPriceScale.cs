namespace ZettaTrade.UI.Charting;

using System;
using Avalonia;

/// <summary>
/// Pure vertical price-scale math for the Skia candlestick chart: the padded
/// spread of the visible window and the two-way price↔pixel mapping the
/// candles, the grid lines, the axis labels, the hover crosshair and the
/// hover price pill share. Extracted from the draw pass (T-028) so the hover
/// mapping is the SAME geometry the axes already draw — unit tests pin it
/// without pixels (the chart control itself is UIA-invisible).
/// </summary>
public static class ChartPriceScale
{
    #region Constants

    /// <summary>
    /// Vertical headroom the price mapping keeps inside the plot at its top
    /// and bottom: extreme candles, gridlines and the centered labels of the
    /// extreme ticks all stay clear of the plot edges. Owned here because it
    /// is part of the mapping itself (moved from the chart control in T-028).
    /// </summary>
    public const float HeadroomPx = 12f;

    /// <summary>
    /// The padding ratio the padded spread adds to the padded extremes
    /// beyond the raw candle spread.
    /// </summary>
    public const decimal PaddingRatio = 0.1m;

    #endregion

    #region Padded Spread

    /// <summary>
    /// The padded price spread of the visible window: the raw candle spread
    /// grows by <see cref="PaddingRatio"/> on BOTH ends. A flat window (the
    /// raw spread is zero — every bar prints one price) keeps a substituted
    /// unit raw range BEFORE padding, so the scale still has room and the
    /// single price maps to the plot's middle. <paramref name="lowest"/> is
    /// the visible window's minimum low, <paramref name="highest"/> its
    /// maximum high.
    /// </summary>
    /// <returns>The padded minimum, the padded maximum and their range.</returns>
    public static (decimal Min, decimal Max, decimal Range) PaddedSpread(
        decimal lowest,
        decimal highest)
    {
        var rawRange = highest - lowest;

        if (rawRange == 0m)
        {
            rawRange = 1m;
        }

        var padding = rawRange * PaddingRatio;
        var min = lowest - padding;
        var max = highest + padding;

        return (min, max, max - min);
    }

    #endregion

    #region Mapping

    /// <summary>
    /// Maps a price onto the plot's vertical axis: the padded scale owns
    /// the full candle spread and keeps unmapped headroom at both ends,
    /// so extreme candles — and the centered labels of the extreme ticks
    /// — stay clear of the plot edges. Candles, gridlines and labels all
    /// use this one mapping, so the scale stays honest.
    /// </summary>
    public static float PriceToY(
        Rect plotRect,
        decimal price,
        decimal minPrice,
        decimal priceRange)
    {
        var usableHeight = UsableHeight(plotRect);

        return ((float)plotRect.Bottom - HeadroomPx)
            - ((float)((double)((price - minPrice) / priceRange) * usableHeight));
    }

    /// <summary>
    /// The inverse mapping, for the hover crosshair: a plot's y position
    /// back onto the padded price scale. Outside the mapped band (the
    /// headroom strips, above the plot, the gutters) the mapping
    /// EXTRAPOLATES linearly — the hover price label prints an honest
    /// outside-band price, it never throws and never substitutes.
    /// </summary>
    public static decimal YToPrice(
        double y,
        Rect plotRect,
        decimal minPrice,
        decimal priceRange)
    {
        var usableHeight = UsableHeight(plotRect);
        var anchorY = (double)plotRect.Bottom - (double)HeadroomPx;
        var fraction = (anchorY - y) / usableHeight;

        return minPrice + (priceRange * (decimal)fraction);
    }

    /// <summary>
    /// The mapping's y span between the headroom anchors: the plot height
    /// minus both headrooms, never below one pixel — a plot sliver (the
    /// degenerate sub-headroom window) maps inside one pixel instead of
    /// inverting the scale, and a division by zero stays impossible for
    /// both directions.
    /// </summary>
    private static float UsableHeight(Rect plotRect) =>
        Math.Max(1f, (float)plotRect.Height - (2f * HeadroomPx));

    #endregion
}
