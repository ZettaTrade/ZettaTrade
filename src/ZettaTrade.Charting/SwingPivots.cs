namespace ZettaTrade.Charting;

using System;
using System.Collections.Generic;
using ZettaTrade.Core.Entities;

/// <summary>Which side of the series a swing pivot marks.</summary>
public enum SwingPivotKind
{
    /// <summary>A swing high: the bar's high dominates its arm.</summary>
    High,

    /// <summary>A swing low: the bar's low dominates its arm.</summary>
    Low,
}

/// <summary>
/// One swing pivot (T-249): the bar that dominates its neighborhood, which
/// side it dominates and how far the dominance reaches.
/// </summary>
/// <param name="Index">The pivot bar's index in the loaded series.</param>
/// <param name="Time">The pivot bar's open time.</param>
/// <param name="Price">The pivot's extreme (the bar's high for a swing
/// high, its low for a swing low) — the price the level rides.</param>
/// <param name="Kind">High or Low.</param>
/// <param name="Strength">The measured dominance: how many further bars
/// BEYOND the detection arm the extreme keeps dominating on BOTH sides (the
/// min of the two sides — the honest symmetric measure; the series bounds
/// stop the count without breaking it). Never below the lookback arm's own
/// dominance — the detector's filter speaks in the same units.</param>
public sealed record SwingPivot(
    int Index,
    DateTimeOffset Time,
    decimal Price,
    SwingPivotKind Kind,
    int Strength);

/// <summary>
/// The swing-pivot detector (T-249): the automatic S/R levels' raw
/// material. Pure and side-effect free — the goldens pin every pivot on
/// crafted series without a feed (the <see cref="VolumeProfile"/>
/// discipline).
/// <para>
/// The math, exactly: a bar is a swing-HIGH candidate when its high is
/// STRICTLY greater than every high in its left arm and at or over every
/// high in its right arm (the arms run <c>lookback</c> bars each side,
/// bounded by the series) — the left-strict/right-inclusive pair means a
/// plateau of equal highs resolves to its EARLIEST bar, exactly one pivot
/// per plateau; a swing LOW mirrors it (strict over the left, at or under
/// the right). The candidate's STRENGTH measures outward from the arm's
/// edges: the bars beyond the arm on each side are counted until the first
/// bar whose extreme reaches the pivot's own (equal or beyond breaks the
/// dominance — a retest is a break), the two counts take their min, and the
/// series bounds just stop the count (a head or a tail is not a break).
/// The <paramref name="minStrength"/> filter keeps only the pivots whose
/// measured dominance reaches it.
/// </para>
/// </summary>
public static class SwingPivots
{
    /// <summary>The default detection arm (bars each side) when no configuration says otherwise.</summary>
    public const int DefaultLookback = 10;

    /// <summary>The detection arm's legal floor: at least one bar each side.</summary>
    public const int MinLookback = 1;

    /// <summary>The default minimum measured strength when no configuration says otherwise.</summary>
    public const int DefaultMinStrength = 2;

    /// <summary>
    /// Detect the swing pivots of the loaded series. One pass over the
    /// candidate arms (each bar's arm is one bounded scan); the pivots come
    /// back in ascending index order. Pure: nothing outside the input is
    /// read, the input is never mutated.
    /// </summary>
    /// <param name="bars">The loaded ascending open-time bars — the WHOLE
    /// series, never a visible window (a window-scoped detector would
    /// flicker on every pan; the pan stability rides the series-scoped
    /// input).</param>
    /// <param name="lookback">The detection arm in bars each side; clamped
    /// into the legal range (at least one).</param>
    /// <param name="minStrength">The minimum measured strength a pivot must
    /// reach to survive; clamped at zero (no filter).</param>
    public static IReadOnlyList<SwingPivot> Detect(IReadOnlyList<Bar> bars, int lookback, int minStrength)
    {
        ArgumentNullException.ThrowIfNull(bars);

        var arm = Math.Max(MinLookback, lookback);
        var required = Math.Max(0, minStrength);

        var pivots = new List<SwingPivot>();
        for (var i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            var leftStart = Math.Max(0, i - arm);
            var rightEnd = Math.Min(bars.Count - 1, i + arm);

            var isHigh = true;
            var isLow = true;
            for (var j = leftStart; j <= rightEnd && (isHigh || isLow); j++)
            {
                if (j == i)
                {
                    continue;
                }

                // The left-strict / right-inclusive pair (the plateau's
                // earliest bar wins): strict over the left arm, at-or-over
                // the right arm.
                if (j < i)
                {
                    isHigh &= bars[j].High < bar.High;
                    isLow &= bars[j].Low > bar.Low;
                }
                else
                {
                    isHigh &= bars[j].High <= bar.High;
                    isLow &= bars[j].Low >= bar.Low;
                }
            }

            if (isHigh)
            {
                var strength = MeasureDominance(bars, i, arm, bar.High, isHigh: true);
                if (strength >= required)
                {
                    pivots.Add(new SwingPivot(i, bar.Time, bar.High, SwingPivotKind.High, strength));
                }
            }

            if (isLow)
            {
                var strength = MeasureDominance(bars, i, arm, bar.Low, isHigh: false);
                if (strength >= required)
                {
                    pivots.Add(new SwingPivot(i, bar.Time, bar.Low, SwingPivotKind.Low, strength));
                }
            }
        }

        return pivots;
    }

    /// <summary>
    /// Measure the pivot's dominance beyond the detection arm: the bars
    /// past the arm on each side are counted until the first bar whose
    /// extreme reaches the pivot's own (equal or beyond breaks — a retest is
    /// a break); the series bounds stop the count without breaking it; the
    /// two counts take their min.
    /// </summary>
    private static int MeasureDominance(IReadOnlyList<Bar> bars, int index, int arm, decimal price, bool isHigh)
    {
        var left = 0;
        for (var j = index - arm - 1; j >= 0; j--)
        {
            var broken = isHigh ? bars[j].High >= price : bars[j].Low <= price;
            if (broken)
            {
                break;
            }

            left++;
        }

        var right = 0;
        for (var j = index + arm + 1; j < bars.Count; j++)
        {
            var broken = isHigh ? bars[j].High >= price : bars[j].Low <= price;
            if (broken)
            {
                break;
            }

            right++;
        }

        return Math.Min(left, right);
    }
}
