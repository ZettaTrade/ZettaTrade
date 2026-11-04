namespace ZettaTrade.Charting;

/// <summary>
/// One drawable indicator overlay: the indicator name and the computed
/// value series aligned with the chart's bars by index - the immutable
/// result the draw pass slices with the visible window. A null value
/// (the indicator's warmup head) breaks the polyline.
/// </summary>
public sealed record ChartIndicatorLine(string Name, IReadOnlyList<decimal?> Values);
