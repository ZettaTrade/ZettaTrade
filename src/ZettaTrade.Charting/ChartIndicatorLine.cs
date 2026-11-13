namespace ZettaTrade.Charting;

using ZettaTrade.Plugins.Abstractions;

/// <summary>
/// One drawable indicator series: the indicator name and the computed
/// value series aligned with the chart's bars by index - the immutable
/// result the draw pass slices with the visible window. A null value
/// (the indicator's warmup head) breaks the polyline.
/// <para>The pane members (T-199) carry the plugin descriptor's pane
/// declaration through the pipeline: the kind splits the draw passes
/// (an overlay rides the candles' price scale, an oscillator renders in
/// its own sub-pane), the optional fixed bounds pin an oscillator's
/// canonical scale (RSI's 0-100) and the optional stored height is the
/// pane's persisted geometry (the splitter's settled value; null rides
/// the layout default). Overlays ignore the three pane members.</para>
/// </summary>
public sealed record ChartIndicatorLine(
    string Name,
    IReadOnlyList<decimal?> Values,
    IndicatorPaneKind PaneKind = IndicatorPaneKind.Overlay,
    decimal? ScaleMin = null,
    decimal? ScaleMax = null,
    double? PaneHeight = null);
