namespace ZettaTrade.UI.Charting;

/// <summary>
/// One heatmap tile as the layout and the widget consume it (T-121): the
/// display text, the 24h change percent the tile colors by (null while the
/// pair has not ticked - the tile keeps the neutral fill) and the rank
/// weight the tile sizes by (null or non-positive - the pair is unpriced
/// or unranked - owns no area). The panel's feed shapes its own snapshot
/// rows into exactly this record; the layout treats the input order as the
/// rank order and never re-sorts behind the feed's back.
/// </summary>
/// <param name="SymbolText">The normalized symbol text the tile prints and the click route carries.</param>
/// <param name="Change24hPercent">The 24h change percent the color maps by; null stays neutral.</param>
/// <param name="Weight">The rank-metric weight the area maps by; null or non-positive owns no area.</param>
public readonly record struct HeatmapTile(
    string SymbolText,
    decimal? Change24hPercent,
    decimal? Weight);
