namespace ZettaTrade.Charting;

/// <summary>
/// The chart's own cumulative-volume-delta series (T-244): the line the
/// chart's ViewModel SYNTHESIZES outside the plugin catalog and publishes as
/// one extra oscillator. The sentinel Name keys the pane's persisted height
/// (the <see cref="ChartIndicatorPaneHeights"/> map), the label key
/// localizes the pane's caption per frame (the renderer reads it, the
/// catalog's plugin lines never set it).
/// </summary>
public static class ChartCvdSeries
{
    /// <summary>The CVD line's stable sentinel name (the pane layout and the persisted heights key on it).</summary>
    public const string Name = "Cvd";

    /// <summary>The resx key the pane's caption resolves from, per frame.</summary>
    public const string LabelKey = "Chart.Cvd.Pane";
}
