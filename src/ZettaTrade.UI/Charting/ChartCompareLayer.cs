namespace ZettaTrade.UI.Charting;

using ZettaTrade.Core.Entities;

/// <summary>
/// One compare layer of the symbol-compare overlay (T-124): a second
/// fetched series for one extra symbol of the workspace's current
/// exchange, keyed by the symbol's normalized name — the legend chip's
/// label and the removal identity at once. The layer's bars keep their
/// own open times; the renderer aligns them against the main series'
/// visible window on every pass, never mutating the layer's own series.
/// The layer list is chart-level, transient state: it is not persisted
/// and not part of the workspace symbol preference.
/// </summary>
/// <param name="Label">The compared symbol's normalized name (the chip label and the removal key).</param>
/// <param name="Bars">The fetched series, ascending by open time.</param>
public sealed record ChartCompareLayer(string Label, IReadOnlyList<Bar> Bars);
