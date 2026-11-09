namespace ZettaTrade.App.Services;

/// <summary>
/// The stat the heatmap's tile weights map by (T-121). Both metrics are the
/// adapter ticker surface's own fields — the panel invents nothing for a
/// venue that fails to serve one: the feed only ranks the pairs whose
/// metric data actually ticked.
/// </summary>
public enum HeatmapMetric
{
    /// <summary>
    /// The 24h quote turnover: the last price times the ticker's base volume
    /// (the only stat the ticker surface provides for it — the cross ranks
    /// pairs on one scale, unlike base volume whose BTC- and DOGE- quoted
    /// numbers are not comparable).
    /// </summary>
    QuoteVolume24h = 0,

    /// <summary>The absolute 24h change percent (magnitude sizes the tile).</summary>
    Change24hPercent = 1,
}
