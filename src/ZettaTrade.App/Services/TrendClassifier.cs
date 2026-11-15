namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using ZettaTrade.Core.Entities;

/// <summary>
/// The pure trend classifier (T-239): one series, one verdict. Three legs
/// must AGREE before a directional state — the EMA slope over the recent
/// window, the Wilder ADX at or above the trend threshold, and the swing
/// structure (higher highs and higher lows over the swing window for a
/// bull, lower for a bear). Any disagreement degrades to the honest range
/// state. A series too short for any leg is the caller's warming state —
/// the classifier throws on it, never fabricates a verdict from thin
/// data (the standing honesty rule: an empty cache degrades, it never
/// invents).
/// </summary>
public static class TrendClassifier
{
    /// <summary>
    /// The swing-structure window in bars: the last this many bars split
    /// into halves and their extremes compared — a newer half printing
    /// both a higher high and a higher low is the bull structure, both
    /// lower the bear one.
    /// </summary>
    public const int SwingLookback = 40;

    /// <summary>The EMA-slope window in bars: at least 3, scaling with the EMA period.</summary>
    public static int SlopeLookback(int emaPeriod) => Math.Max(3, emaPeriod / 4);

    /// <summary>
    /// The smallest series the classifier accepts for the given tuning:
    /// the ADX leg needs two ADX periods of smoothed ranges before its
    /// first value exists, the slope leg the EMA's seed average plus its
    /// slope window, and the structure leg the full swing window — the
    /// sum covers every leg, whichever period is the longer rides it.
    /// </summary>
    public static int MinimumBars(int emaPeriod, int adxPeriod) =>
        (adxPeriod * 2) + Math.Max(emaPeriod, SwingLookback) + SlopeLookback(emaPeriod);

    /// <summary>
    /// Classify one series: the three legs over the series' tail, the
    /// unanimous verdict or the range fallback.
    /// </summary>
    /// <param name="series">The bars, oldest first (the cache's own answer shape).</param>
    /// <param name="emaPeriod">The EMA slope period in bars (2..200).</param>
    /// <param name="adxPeriod">The Wilder ADX smoothing period in bars (2..100).</param>
    /// <param name="adxThreshold">The ADX level a series must reach for a directional state.</param>
    /// <returns>The verdict for the series' tail.</returns>
    /// <exception cref="ArgumentNullException">The series is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A period is below its floor.</exception>
    /// <exception cref="ArgumentException">The series is shorter than <see cref="MinimumBars"/> — the warming state's territory, never a fabricated verdict.</exception>
    public static MtfTrendState Classify(
        IReadOnlyList<Bar> series,
        int emaPeriod,
        int adxPeriod,
        decimal adxThreshold)
    {
        ArgumentNullException.ThrowIfNull(series);
        if (emaPeriod < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(emaPeriod), emaPeriod, "An EMA period below 2 owns no average.");
        }

        if (adxPeriod < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(adxPeriod), adxPeriod, "An ADX period below 2 owns no smoothing.");
        }

        var minimum = MinimumBars(emaPeriod, adxPeriod);
        if (series.Count < minimum)
        {
            throw new ArgumentException(
                $"A trend verdict needs at least {minimum} bars for the given tuning; the series owns {series.Count}.",
                nameof(series));
        }

        var bars = series as Bar[] ?? series.ToArray();
        var (slopeUp, slopeDown) = EmaSlope(bars, emaPeriod);
        var adx = WilderAdx(bars, adxPeriod);
        var trending = adx >= adxThreshold;
        var (structureUp, structureDown) = SwingStructure(bars);

        if (slopeUp && trending && structureUp)
        {
            return MtfTrendState.Bull;
        }

        if (slopeDown && trending && structureDown)
        {
            return MtfTrendState.Bear;
        }

        return MtfTrendState.Range;
    }

    /// <summary>
    /// The slope leg: the EMA's newest value against its value one slope
    /// window back — strictly higher rides up, strictly lower down, an
    /// exact tie is neither (the honest plain state).
    /// </summary>
    private static (bool Up, bool Down) EmaSlope(IReadOnlyList<Bar> bars, int emaPeriod)
    {
        var ema = EmaSeries(bars, emaPeriod);
        var lookback = SlopeLookback(emaPeriod);
        var newest = ema[^1];
        var prior = ema[ema.Count - 1 - lookback];
        return (newest > prior, newest < prior);
    }

    /// <summary>
    /// The EMA series: the first <paramref name="period"/> closes seed a
    /// simple average, every later close rides the standard recursion
    /// (previous + alpha × (close − previous), alpha = 2 / (period + 1)).
    /// </summary>
    private static List<decimal> EmaSeries(IReadOnlyList<Bar> bars, int period)
    {
        var alpha = 2m / (period + 1);
        var ema = 0m;
        for (var index = 0; index < period; index++)
        {
            ema += bars[index].Close;
        }

        ema /= period;
        var values = new List<decimal>(bars.Count - period + 1) { ema };
        for (var index = period; index < bars.Count; index++)
        {
            ema += alpha * (bars[index].Close - ema);
            values.Add(ema);
        }

        return values;
    }

    /// <summary>
    /// The strength leg: the Wilder ADX over the whole series, the newest
    /// value. The smoothed range and the directional movement sums ride
    /// Wilder's own recursion — the first <paramref name="period"/> values
    /// seed a simple average, every later value the (previous × (period −
    /// 1) + value) / period step; the ADX itself smooths the directional
    /// index once more in the same shape.
    /// </summary>
    private static decimal WilderAdx(IReadOnlyList<Bar> bars, int period)
    {
        decimal rangeSum = 0, plusSum = 0, minusSum = 0;
        for (var index = 1; index <= period; index++)
        {
            rangeSum += TrueRange(bars, index);
            plusSum += DirectionalMove(bars, index, bullish: true);
            minusSum += DirectionalMove(bars, index, bullish: false);
        }

        var diValues = new List<decimal> { DirectionalIndex(rangeSum, plusSum, minusSum) };
        for (var index = period + 1; index < bars.Count; index++)
        {
            rangeSum = rangeSum - (rangeSum / period) + TrueRange(bars, index);
            plusSum = plusSum - (plusSum / period) + DirectionalMove(bars, index, bullish: true);
            minusSum = minusSum - (minusSum / period) + DirectionalMove(bars, index, bullish: false);
            diValues.Add(DirectionalIndex(rangeSum, plusSum, minusSum));
        }

        var adx = diValues.Take(period).Average();
        for (var index = period; index < diValues.Count; index++)
        {
            adx = (adx * (period - 1) + diValues[index]) / period;
        }

        return adx;
    }

    /// <summary>The one true range: the bar's own span against the gaps to the prior close, the widest wins.</summary>
    private static decimal TrueRange(IReadOnlyList<Bar> bars, int index)
    {
        var bar = bars[index];
        var priorClose = bars[index - 1].Close;
        return Math.Max(
            bar.High - bar.Low,
            Math.Max(Math.Abs(bar.High - priorClose), Math.Abs(bar.Low - priorClose)));
    }

    /// <summary>
    /// The one directional move: the up move is the new high over the
    /// prior high, the down move the prior low under the new low — the
    /// wider of the pair owns the bar, the other side scores zero.
    /// </summary>
    private static decimal DirectionalMove(IReadOnlyList<Bar> bars, int index, bool bullish)
    {
        var up = bars[index].High - bars[index - 1].High;
        var down = bars[index - 1].Low - bars[index].Low;
        if (bullish)
        {
            return up > down && up > 0m ? up : 0m;
        }

        return down > up && down > 0m ? down : 0m;
    }

    /// <summary>The directional index: the ±DI pair's separation as a share of their sum; a frozen series owns none.</summary>
    private static decimal DirectionalIndex(decimal rangeSum, decimal plusSum, decimal minusSum)
    {
        if (rangeSum <= 0m)
        {
            return 0m;
        }

        var plusDi = 100m * plusSum / rangeSum;
        var minusDi = 100m * minusSum / rangeSum;
        var denominator = plusDi + minusDi;
        return denominator <= 0m ? 0m : 100m * Math.Abs(plusDi - minusDi) / denominator;
    }

    /// <summary>
    /// The structure leg: the swing window's two halves compared by their
    /// extremes — the newer half printing both a higher high and a higher
    /// low is the bull structure, both lower the bear one, anything else
    /// is neither (the honest plain state).
    /// </summary>
    private static (bool Up, bool Down) SwingStructure(IReadOnlyList<Bar> bars)
    {
        var window = Math.Min(SwingLookback, bars.Count);
        var newerHalf = window / 2;
        var older = bars.Skip(bars.Count - window).Take(newerHalf).ToArray();
        var newer = bars.Skip(bars.Count - newerHalf).ToArray();
        var olderHigh = older.Max(static bar => bar.High);
        var olderLow = older.Min(static bar => bar.Low);
        var newerHigh = newer.Max(static bar => bar.High);
        var newerLow = newer.Min(static bar => bar.Low);
        return (
            newerHigh > olderHigh && newerLow > olderLow,
            newerHigh < olderHigh && newerLow < olderLow);
    }
}
