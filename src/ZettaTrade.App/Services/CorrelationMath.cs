namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using ZettaTrade.Core.Entities;

/// <summary>
/// The pure correlation math (T-252): log returns, the Pearson coefficient
/// and the N×N matrix assembly. Two series pair ONLY where their bar-open
/// times overlap — the cache serves each symbol's latest window
/// independently, so a symbol listed later (or a venue with a gap) owns
/// fewer shared bars, and a pair whose shared stretch cannot reach the
/// minimum overlap answers the honest dash (null), never a fabricated
/// coefficient computed over position-aligned bars that never traded the
/// same minutes. A series whose returns carry no variance (a flat close
/// run) is a dash the same way: Pearson's denominator vanishes, and a
/// number invented from it would lie.
/// <para>
/// Pure and side-effect free: the same inputs answer the same matrix on
/// every recompute — the shared-key pairs sort by time, so the
/// floating-point summation order is pinned and a recompute is
/// bit-identical, not merely close.
/// </para>
/// </summary>
public static class CorrelationMath
{
    /// <summary>
    /// The per-symbol log returns keyed by the bar-open time the return
    /// belongs to: bar <c>i</c>'s return is
    /// <c>ln(close[i] / close[i-1])</c> stamped at bar <c>i</c>'s open —
    /// the first bar of the series carries no prior close and no return.
    /// The cache's own answer shape (oldest first) is the input contract.
    /// </summary>
    /// <param name="series">The bars, oldest first.</param>
    /// <returns>The returns keyed by open time; an empty or one-bar series answers an empty map.</returns>
    /// <exception cref="ArgumentNullException">The series is null.</exception>
    public static Dictionary<DateTimeOffset, double> LogReturns(IReadOnlyList<Bar> series)
    {
        ArgumentNullException.ThrowIfNull(series);

        var returns = new Dictionary<DateTimeOffset, double>(series.Count);
        for (var i = 1; i < series.Count; i++)
        {
            var prior = series[i - 1].Close;
            if (prior <= 0m || series[i].Close <= 0m)
            {
                // A non-positive close is corrupt data, not a price: the
                // return is refused (the key stays absent — an honest
                // coverage hole), never a log-of-negative exception.
                continue;
            }

            returns[series[i].Time] = Math.Log((double)(series[i].Close / prior));
        }

        return returns;
    }

    /// <summary>
    /// The Pearson product-moment coefficient over two paired stretches —
    /// the honest dash (null) when the pairing cannot carry one: fewer
    /// than two pairs, or either stretch owning zero variance.
    /// </summary>
    /// <param name="x">The first stretch's returns.</param>
    /// <param name="y">The second stretch's returns, paired by index.</param>
    /// <returns>The coefficient in -1..1, or null when undefined.</returns>
    /// <exception cref="ArgumentNullException">A stretch is null.</exception>
    public static double? Pearson(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);

        if (x.Count != y.Count || x.Count < 2)
        {
            return null;
        }

        var sumX = 0.0;
        var sumY = 0.0;
        for (var i = 0; i < x.Count; i++)
        {
            sumX += x[i];
            sumY += y[i];
        }

        var meanX = sumX / x.Count;
        var meanY = sumY / y.Count;

        var covariance = 0.0;
        var varianceX = 0.0;
        var varianceY = 0.0;
        for (var i = 0; i < x.Count; i++)
        {
            var carryX = x[i] - meanX;
            var carryY = y[i] - meanY;
            covariance += carryX * carryY;
            varianceX += carryX * carryX;
            varianceY += carryY * carryY;
        }

        if (varianceX <= 0.0 || varianceY <= 0.0)
        {
            return null;
        }

        return covariance / Math.Sqrt(varianceX * varianceY);
    }

    /// <summary>
    /// The smallest shared-return count a pair's verdict asks: half the
    /// requested window (both series were read at the same window, so half
    /// of it is the stretch a listing gap may not eat through), floored at
    /// Pearson's own two-pair minimum.
    /// </summary>
    /// <param name="window">The requested returns window in bars.</param>
    /// <returns>The minimum overlap count.</returns>
    public static int MinOverlapReturns(int window)
    {
        var half = window / 2;
        return half < 2 ? 2 : half;
    }

    /// <summary>
    /// Assemble the N×N matrix: cell (i, j) pairs series i and j over
    /// their shared open times — below <paramref name="minOverlap"/>
    /// shared returns the cell is the honest dash. The diagonal answers
    /// the series' self-correlation (1 when its returns carry variance,
    /// the dash when they do not — a flat series is a coverage hole, not
    /// a perfect correlation with itself).
    /// </summary>
    /// <param name="seriesReturns">One returns map per symbol, in matrix order.</param>
    /// <param name="minOverlap">The smallest shared-return count a pair verdict accepts.</param>
    /// <returns>The row-major N×N matrix; null cells are the honest dashes.</returns>
    /// <exception cref="ArgumentNullException">The map list is null.</exception>
    public static double?[,] Matrix(
        IReadOnlyList<Dictionary<DateTimeOffset, double>> seriesReturns,
        int minOverlap)
    {
        ArgumentNullException.ThrowIfNull(seriesReturns);

        var size = seriesReturns.Count;
        var matrix = new double?[size, size];
        for (var i = 0; i < size; i++)
        {
            var left = seriesReturns[i];
            for (var j = 0; j < size; j++)
            {
                var right = seriesReturns[j];
                if (i == j)
                {
                    matrix[i, j] = SelfCorrelation(left);
                    continue;
                }

                // The shared stretch: the SMALLER map drives the scan; the
                // pairs sort by time so a recompute sums in the same order
                // and answers bit-identically.
                var (smaller, larger) = left.Count <= right.Count ? (left, right) : (right, left);
                var shared = new List<double>(smaller.Count);
                var paired = new List<double>(smaller.Count);
                foreach (var (time, value) in smaller.OrderBy(static pair => pair.Key))
                {
                    if (larger.TryGetValue(time, out var twin))
                    {
                        shared.Add(value);
                        paired.Add(twin);
                    }
                }

                matrix[i, j] = shared.Count < minOverlap
                    ? null
                    : Pearson(shared, paired);
            }
        }

        return matrix;
    }

    /// <summary>
    /// One series' self-correlation: 1 when the returns carry variance,
    /// the dash when they do not (a flat run defines no correlation).
    /// </summary>
    private static double? SelfCorrelation(Dictionary<DateTimeOffset, double> returns)
    {
        if (returns.Count < 2)
        {
            return null;
        }

        var values = returns.Values.ToList();
        return Pearson(values, values);
    }
}
