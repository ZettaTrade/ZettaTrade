namespace ZettaTrade.UI.Charting;

using System;
using System.Globalization;

/// <summary>
/// The heatmap tile's caption text builders (T-121): small pure formatters
/// the widget's captions bind, kept beside the layout so the tile text and
/// the tile geometry stay one domain. The percent shape mirrors the
/// watchlist rows' (+0.00/-0.00/0.00 two-decimal form); the charts render
/// their labels in the app's UI language, never the ambient thread culture
/// (<see cref="ChartTickCulture.Resolve"/> rides here).
/// </summary>
public static class HeatmapTileCaptions
{
    /// <summary>
    /// Format the 24h change as a signed percent: an explicit plus for
    /// gains, a minus for losses, always two decimals.
    /// </summary>
    /// <param name="changePercent">The 24h change in percent units.</param>
    /// <param name="culture">The culture to format with (the widget passes <see cref="ChartTickCulture.Resolve"/>'s).</param>
    /// <returns>The formatted signed percent text.</returns>
    public static string FormatChangePercent(decimal changePercent, IFormatProvider culture) =>
        changePercent.ToString("+0.00'%';-0.00'%';0.00'%'", culture);

    /// <summary>
    /// The tile captions' label culture: the app's UI language (the charts'
    /// convention), never the render thread's ambient culture.
    /// </summary>
    /// <returns>The resolved caption culture.</returns>
    public static CultureInfo Culture() => ChartTickCulture.Resolve();
}
