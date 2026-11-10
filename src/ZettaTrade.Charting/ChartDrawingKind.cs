namespace ZettaTrade.Charting;

/// <summary>
/// The drawing tools the chart's annotation layer offers (T-091): the kinds
/// a trader places over candles. Drawings anchor to (bar-open time, price)
/// pairs — never to bar indices — so a pan append (T-033 prepends older bars
/// and shifts indices) or a time frame rebase keeps every drawing standing.
/// </summary>
public enum ChartDrawingKind
{
    /// <summary>A two-anchor trend line found on the price-time plane.</summary>
    TrendLine,

    /// <summary>A one-anchor horizontal line pinned to one price across the span the chart shows.</summary>
    HorizontalLevel,

    /// <summary>A two-anchor Fibonacci retracement: the levels sit between the anchors' prices and span the anchors' time range.</summary>
    FibRetracement,
}
