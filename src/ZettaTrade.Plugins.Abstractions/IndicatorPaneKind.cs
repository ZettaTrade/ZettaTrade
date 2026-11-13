namespace ZettaTrade.Plugins.Abstractions;

/// <summary>
/// The chart pane an indicator's series renders in (T-199). The default
/// overlay keeps every pre-2.2.0 plugin rendering exactly as before — the
/// host reads the declaration through the interface's default members, so
/// an implementation compiled against an older contract stays an overlay
/// without a rebuild.
/// </summary>
public enum IndicatorPaneKind
{
    /// <summary>The series rides the candles' own price scale, drawn over them (the T-034 shape).</summary>
    Overlay = 0,

    /// <summary>The series renders in its own sub-pane under the chart, scaled by its own value range (or the declared fixed bounds) — never the candles' price scale.</summary>
    Oscillator = 1,
}
