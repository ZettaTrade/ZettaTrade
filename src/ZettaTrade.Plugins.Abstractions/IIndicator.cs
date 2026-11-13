namespace ZettaTrade.Plugins.Abstractions;

/// <summary>
/// One indicator implementation the charting seam renders as a line overlay
/// (T-034): a plugin assembly offers concrete implementations the host's
/// indicator catalog resolves by <see cref="Name"/> for a configured period.
/// <see cref="Apply"/> must be pure and cheap - the chart pipeline recomputes
/// it on every bars delivery, every older-history prepend and every
/// indicator settings change. The input is the SDK-owned
/// <see cref="IndicatorBar"/> shape (contract 2.0.0, T-163): plugins never
/// see the application's internal entities, only this assembly.
/// </summary>
public interface IIndicator
{
    /// <summary>Canonical indicator name, fixed by the implementation (e.g. "SMA").</summary>
    string Name { get; }

    /// <summary>
    /// Compute the indicator's series over the given bars: one value per
    /// bar, aligned by index. The warmup stretch an implementation cannot
    /// compute yet (the head of a moving average over its period, for
    /// example) is null. The input is the ascending open-time candle series
    /// the chart holds; the output never mutates it.
    /// </summary>
    IReadOnlyList<decimal?> Apply(IReadOnlyList<IndicatorBar> bars);

    /// <summary>
    /// The chart pane the series renders in (T-199, contract 2.2.0): an
    /// overlay rides the candles' own price scale, an oscillator renders in
    /// its own sub-pane under the chart. The default keeps every
    /// implementation that predates the declaration an overlay — the pane
    /// split is opt-in per indicator, never a surprise for an existing
    /// plugin.
    /// </summary>
    IndicatorPaneKind PaneKind => IndicatorPaneKind.Overlay;

    /// <summary>
    /// The oscillator pane's fixed lower scale bound, when the indicator's
    /// values live on a canonical bounded scale (RSI's 0-100, for example).
    /// null (the default) lets the pane auto-scale to its own visible
    /// values, the same honest window discipline the main plot applies.
    /// Overlays never read it.
    /// </summary>
    decimal? ScaleMin => null;

    /// <summary>
    /// The oscillator pane's fixed upper scale bound (RSI's 100, for
    /// example); null (the default) auto-scales. A declared bound never
    /// clips the polyline: values outside the band still draw (the pane
    /// clamps the MAPPING, not the data — the line runs off the pane's top
    /// or bottom edge exactly as a value outside the main plot's window
    /// would).
    /// </summary>
    decimal? ScaleMax => null;
}
