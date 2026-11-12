namespace ZettaTrade.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;

/// <summary>
/// The watchlist row's presentation helpers (T-120): the pure text and
/// geometry builders the row ViewModel binds — price/percent/volume
/// formatting with an injectable culture (the tests pass a fixed one, the
/// panel rides the UI culture), and the sparkline point mapping.
/// </summary>
public static class WatchlistRowPresentation
{
    #region Constants

    /// <summary>The sparkline canvas the rows draw into (logical pixels).</summary>
    public const double SparklineWidth = 96;

    /// <summary>See <see cref="SparklineWidth"/>: the canvas height.</summary>
    public const double SparklineHeight = 28;

    /// <summary>The volume suffixes: K for thousands, M for millions, B for billions.</summary>
    private static readonly string[] VolumeSuffixes = ["", "k", "M", "B"];

    #endregion Constants

    #region Public Methods

    /// <summary>
    /// Format a last price for the row: adaptive precision keeps big prices
    /// calm and small prices honest (two decimals above 100, three above 1,
    /// six below).
    /// </summary>
    /// <param name="price">The last price.</param>
    /// <param name="culture">The culture to format with (the panel uses the UI culture).</param>
    /// <returns>The formatted price text.</returns>
    public static string FormatPrice(decimal price, IFormatProvider culture) => (price switch
    {
        >= 100m => price.ToString("N2", culture),
        >= 1m => price.ToString("N3", culture),
        _ => price.ToString("N6", culture),
    });

    /// <summary>
    /// Format the 24h change as a signed percent: an explicit plus for gains
    /// (a minus for losses), always two decimals.
    /// </summary>
    /// <param name="changePercent">The 24h change in percent units.</param>
    /// <param name="culture">The culture to format with (the panel uses the UI culture).</param>
    /// <returns>The formatted signed percent text.</returns>
    public static string FormatSignedPercent(decimal changePercent, IFormatProvider culture) =>
        changePercent.ToString("+0.00'%';-0.00'%';0.00'%'", culture);

    /// <summary>
    /// Format a funding rate as a signed percent (T-197): the wire carries
    /// the per-interval rate as a FRACTION (0.0001 = 0.01%), so the display
    /// multiplies into the percent basis and keeps four decimals — a
    /// typical funding rate lives below the two-decimal resolution.
    /// </summary>
    /// <param name="fundingRate">The funding rate as the wire serves it (a fraction).</param>
    /// <param name="culture">The culture to format with (the panel uses the UI culture).</param>
    /// <returns>The formatted signed percent text.</returns>
    public static string FormatFundingRate(decimal fundingRate, IFormatProvider culture) =>
        (fundingRate * 100m).ToString("+0.0000'%';-0.0000'%';0.0000'%'", culture);

    /// <summary>
    /// Format a 24h volume compactly: a metric suffix above the thousand
    /// range, three significant digits kept.
    /// </summary>
    /// <param name="volume">The 24h volume in base units.</param>
    /// <param name="culture">The culture to format with (the panel uses the UI culture).</param>
    /// <returns>The formatted compact volume text.</returns>
    public static string FormatVolume(decimal volume, IFormatProvider culture)
    {
        var scale = 0;
        var scaled = volume;
        while (scaled >= 1000m && scale < VolumeSuffixes.Length - 1)
        {
            scaled /= 1000m;
            scale++;
        }

        return scaled.ToString("0.00", culture) + VolumeSuffixes[scale];
    }

    /// <summary>
    /// Map a price ring onto the sparkline canvas: the highest price rides
    /// the top (y = 0), the lowest the bottom (y = the canvas height), and
    /// the points spread evenly left to right. A flat series maps to the
    /// midline; fewer than two points map to an empty list (nothing worth
    /// drawing yet).
    /// </summary>
    /// <param name="prices">The ring of last prices, oldest first.</param>
    /// <param name="width">The canvas width.</param>
    /// <param name="height">The canvas height.</param>
    /// <returns>The mapped points for the polyline binding (<see cref="System.Collections.Generic.IList{Point}"/>'s shape).</returns>
    public static IList<Point> BuildSparklinePoints(
        IReadOnlyList<decimal> prices,
        double width,
        double height)
    {
        var points = new List<Point>(prices.Count);
        if (prices.Count < 2)
        {
            return points;
        }

        decimal min = prices[0];
        decimal max = prices[0];
        foreach (var price in prices)
        {
            min = Math.Min(min, price);
            max = Math.Max(max, price);
        }

        var span = max - min;
        for (var index = 0; index < prices.Count; index++)
        {
            var x = width * index / (prices.Count - 1);
            var relative = span == 0m ? 0.5 : (double)((prices[index] - min) / span);
            var y = height - relative * height;
            points.Add(new Point(x, y));
        }

        return points;
    }

    #endregion Public Methods
}
