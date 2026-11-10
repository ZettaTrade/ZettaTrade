namespace ZettaTrade.Charting;

/// <summary>
/// The drawings layer's pure math (T-091): the Fibonacci retracement levels
/// derived from the two anchors, the segment/point hit distances the
/// control turns into selection (its pixel mapping stays control-side —
/// this type only ever sees already-mapped coordinates), and the derived
/// pixel-space facts the draw pass and the hit zones share. Pure: no I/O,
/// no chart state, no mutation.
/// </summary>
public static class ChartDrawingGeometry
{
    /// <summary>
    /// The retracement ladder (decimal-exact): from the second anchor (0)
    /// back to the first (1) — the classic 23.6/38.2/50/61.8/78.6 swing set.
    /// </summary>
    public static readonly IReadOnlyList<decimal> FibonacciRatios =
    [
        0.000m, 0.236m, 0.382m, 0.500m, 0.618m, 0.786m, 1.000m,
    ];

    /// <summary>
    /// The retracement levels for two anchors: level <c>r</c> sits at
    /// <c>B - (B − A) * r</c> in price space (r = 0 is the second anchor's
    /// price, r = 1 the first's). Returns one entry per
    /// <see cref="FibonacciRatios"/>, in the ratios' order. The levels
    /// render across the anchors' time span; the time span it self derives
    /// from the anchors directly.
    /// </summary>
    public static IReadOnlyList<(decimal Ratio, decimal Price)> FibonacciLevels(ChartDrawingAnchor first, ChartDrawingAnchor second)
    {
        var span = second.Price - first.Price;

        var levels = new (decimal, decimal)[FibonacciRatios.Count];
        for (var i = 0; i < FibonacciRatios.Count; i++)
        {
            var ratio = FibonacciRatios[i];
            levels[i] = (ratio, second.Price - span * ratio);
        }

        return levels;
    }

    /// <summary>
    /// The shortest distance from a point to a segment (Euclidean, whatever
    /// space the coordinates live in): the segment's own projection when it
    /// lands inside, the nearest endpoint otherwise. The chart feeds pixel
    /// coordinates as scalar doubles — the threshold to a "hit" stays the
    /// caller's call.
    /// </summary>
    public static double DistanceToSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        var dx = bx - ax;
        var dy = by - ay;

        if (dx == 0 && dy == 0)
        {
            return Distance(px, py, ax, ay);
        }

        var t = ((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy);
        t = Math.Clamp(t, 0.0, 1.0);

        return Distance(px, py, ax + t * dx, ay + t * dy);
    }

    /// <summary>The Euclidean distance between two points in one space.</summary>
    public static double Distance(double x1, double y1, double x2, double y2)
    {
        var dx = x1 - x2;
        var dy = y1 - y2;

        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// The pixel affordance every drawing's body answers within: a segment
    /// (the trend line, the fib levels, the level's full-width line — its
    /// band answers at ANY x along the line) within the band of the
    /// pointer is a hit.
    /// </summary>
    public const double SegmentHitTolerancePx = 5.0;

    /// <summary>
    /// The radius (pixels) around a drawing anchor where a grab takes the
    /// anchor itself — the drag/edit handle — before the whole drawing's
    /// body gets the press.
    /// </summary>
    public const double HandleRadiusPx = 7.0;
}
