namespace ZettaTrade.Charting;

/// <summary>
/// The drawing tool the user arms in the palette (T-091). <see cref="None"/>
/// is the resting mode: the chart's pointer rides the pan/hover behavior
/// unchanged. Any armed tool supersedes panning for the placement gestures.
/// </summary>
public enum ChartDrawingTool
{
    /// <summary>No armed tool: idle chart behavior (pan, hover, select).</summary>
    None,

    /// <summary>Arm the trend line: click-drag places two corners.</summary>
    TrendLine,

    /// <summary>Arm the horizontal level: one click-drag places one anchor's price (time rides the press).</summary>
    HorizontalLevel,

    /// <summary>Arm the Fibonacci retracement: click-drag places two corners.</summary>
    FibRetracement,
}

/// <summary>
/// The extension point converting an armed tool into the drawing kind it
/// places (<see cref="None"/> has no kind — a placement never starts
/// without an armed tool).
/// </summary>
public static class ChartDrawingToolExtensions
{
    /// <summary>The kind an armed tool places.</summary>
    public static ChartDrawingKind ToKind(this ChartDrawingTool tool) => tool switch
    {
        ChartDrawingTool.TrendLine => ChartDrawingKind.TrendLine,
        ChartDrawingTool.HorizontalLevel => ChartDrawingKind.HorizontalLevel,
        ChartDrawingTool.FibRetracement => ChartDrawingKind.FibRetracement,
        _ => throw new InvalidOperationException("No drawing kind exists for the resting mode."),
    };
}
