namespace ZettaTrade.UI.Charting;

/// <summary>
/// One pressed heatmap tile (T-121): the panel's click route payload. The
/// widget publishes the tile's own text; the parse (or the honest refusal
/// of an unparsable one) lives in the panel's ViewModel - the control
/// carries a string, never a substituted default.
/// </summary>
/// <param name="SymbolText">The pressed tile's normalized symbol text.</param>
public sealed record HeatmapTileClick(string SymbolText);
