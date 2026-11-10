namespace ZettaTrade.Charting;

/// <summary>
/// The drawings' snapping helpers (T-091): placing or dragging an anchor
/// clings to the chart's own grid — the price axis' rungs and the bars'
/// open times — so a level lands ON the price the chart labels and a line
/// corner lands ON a bar edge instead of floating between them. Pure: the
/// caller supplies the candidate grid values and their own axis-to-double
/// mapping, and decides whether the snap's gap is close enough to accept.
/// </summary>
public static class ChartDrawingSnapping
{
    /// <summary>
    /// The nearest grid value to the pointer's value, or null when the
    /// candidate list is empty. The mapping delegate reads each candidate
    /// in the space <paramref name="value"/> lives in (prices and bar-open
    /// times both land one world-line: the caller maps a
    /// <see cref="DateTimeOffset"/> through its own epoch projection). The
    /// caller compares the resulting gap against its own snap threshold —
    /// this helper never invents a distance rule.
    /// </summary>
    public static T? Nearest<T>(double value, IReadOnlyList<T> candidates, Func<T, double> toDouble)
        where T : struct
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var bestIndex = 0;
        var bestDistance = Math.Abs(toDouble(candidates[0]) - value);

        for (var i = 1; i < candidates.Count; i++)
        {
            var distance = Math.Abs(toDouble(candidates[i]) - value);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = i;
            }
        }

        return candidates[bestIndex];
    }
}
