namespace ZettaTrade.UI.Charting;

using Avalonia;

/// <summary>
/// One laid-out heatmap tile (T-121): the plot-space rect the squarified
/// layout gave the tile and the change percent its color maps by. The
/// widget draws straight from the list and hit-tests against the same
/// list, so what a click lands on is exactly what the frame drew - the
/// two views can never disagree.
/// </summary>
/// <param name="SymbolText">The normalized symbol text the tile prints and the click route carries.</param>
/// <param name="Change24hPercent">The 24h change percent the tile color maps by; null stays neutral.</param>
/// <param name="Bounds">The tile's plot-space rect (the layout's rank order kept).</param>
public readonly record struct HeatmapTileRect(
    string SymbolText,
    decimal? Change24hPercent,
    Rect Bounds);
