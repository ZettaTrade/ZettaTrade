namespace ZettaTrade.Charting;

using ZettaTrade.Core.Entities;

/// <summary>
/// The data state the VWAP overlay reports, so the chart presents the honest
/// empty state instead of fabricating values (T-093).
/// </summary>
public enum VwapOverlayState
{
    /// <summary>At least one loaded bar carries volume: the sessions with
    /// volume carry values, the sessions without carry nulls.</summary>
    Computed,

    /// <summary>No loaded bar carries volume: every value is null and the
    /// chart draws the localized no-volume state instead of lines.</summary>
    NoVolumeData,
}

/// <summary>
/// The session-anchored VWAP overlay with ±1σ bands (T-093): value series
/// aligned with the chart's bars by index, exactly like
/// <see cref="ChartIndicatorLine"/>. The center is the volume-weighted
/// average of the session's typical prices (H+L+C)/3 — the weights are the
/// bar volumes; the bands carry the center ± one volume-weighted standard
/// deviation of the same typical price (±1σ). Sessions break per
/// <see cref="VwapSessionAnchor"/>. A value is null while the owning session
/// has accumulated no volume yet; a zero-volume bar inside a running session
/// contributes a zero share, so it leaves the accumulated value standing (a
/// zero share is the bar's real weight, never a fabrication); a session that
/// accumulates no volume at all holds nulls for the whole session.
/// </summary>
public sealed record VwapOverlay(
    VwapOverlayState State,
    IReadOnlyList<decimal?> Center,
    IReadOnlyList<decimal?> Upper,
    IReadOnlyList<decimal?> Lower,
    IReadOnlyList<int> SessionStarts)
{
    /// <summary>
    /// Compute the overlay over the FULL loaded series (the chart slices it
    /// with the visible window exactly like an indicator line - see
    /// <see cref="Slice"/>). Pure: nothing outside the input is read, the
    /// input is never mutated.
    /// </summary>
    /// <param name="bars">The ascending open-time candle series the chart holds.</param>
    /// <returns>The overlay aligned with <paramref name="bars"/> by index.</returns>
    public static VwapOverlay Compute(IReadOnlyList<Bar> bars)
    {
        if (bars.Count == 0)
        {
            return new VwapOverlay(
                VwapOverlayState.NoVolumeData,
                [],
                [],
                [],
                []);
        }

        List<decimal?> center = new(bars.Count);
        List<decimal?> upper = new(bars.Count);
        List<decimal?> lower = new(bars.Count);
        List<int> sessionStarts = [0];

        // The session accumulation state: reset at every session break. A
        // zero-volume bar contributes nothing to the sums but still renders
        // the accumulated value standing so far.
        decimal volumeSum = 0m;
        decimal weightedPriceSum = 0m;
        decimal weightedSquaredPriceSum = 0m;
        Bar previous = default;
        var hasPrevious = false;
        var anyVolume = false;

        for (var i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            if (hasPrevious && VwapSessionAnchor.OpensNewSession(previous, bar))
            {
                sessionStarts.Add(i);
                volumeSum = 0m;
                weightedPriceSum = 0m;
                weightedSquaredPriceSum = 0m;
            }

            var typical = (bar.High + bar.Low + bar.Close) / 3m;
            if (bar.Volume > 0m)
            {
                anyVolume = true;
                volumeSum += bar.Volume;
                weightedPriceSum += bar.Volume * typical;
                weightedSquaredPriceSum += bar.Volume * typical * typical;
            }

            if (volumeSum > 0m)
            {
                var vwap = weightedPriceSum / volumeSum;

                // The volume-weighted variance of the typical price. A real
                // variance can never turn negative - a negative remainder is
                // pure decimal truncation - so it clamps to a zero sigma (the
                // bands collapse onto the center, the honest zero-width state
                // the accumulation actually produced).
                var variance = (weightedSquaredPriceSum / volumeSum) - (vwap * vwap);
                var sigma = variance <= 0m ? 0m : Sqrt(variance);

                center.Add(vwap);
                upper.Add(vwap + sigma);
                lower.Add(vwap - sigma);
            }
            else
            {
                center.Add(null);
                upper.Add(null);
                lower.Add(null);
            }

            previous = bar;
            hasPrevious = true;
        }

        var state = anyVolume ? VwapOverlayState.Computed : VwapOverlayState.NoVolumeData;
        return new VwapOverlay(state, center, upper, lower, sessionStarts);
    }

    /// <summary>
    /// Slice the overlay with the visible bar window [start, end): the value
    /// lists re-slice by index and the session starts rebase into the window
    /// (every session start inside [start, end) keeps its slot relative to
    /// the slice, so the drawn polyline breaks exactly at the boundary).
    /// The chart slices with the SAME [start, end) window the bars slice
    /// uses, only after the alignment check (a stale publish races the new
    /// series and the chart skips it wholesale).
    /// </summary>
    /// <param name="start">The window's first bar index.</param>
    /// <param name="end">The index one past the window's last bar.</param>
    /// <returns>The window-scoped overlay.</returns>
    public VwapOverlay Slice(int start, int end)
    {
        if (start < 0 || start > end || end > Center.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(start), start, "The window is not inside the overlay's value range.");
        }

        var count = end - start;
        return new VwapOverlay(
            State,
            CopyRange(Center, start, count),
            CopyRange(Upper, start, count),
            CopyRange(Lower, start, count),
            SessionStarts
                .Where(value => value >= start && value < end)
                .Select(value => value - start)
                .ToList());
    }

    /// <summary>Copy [start, start + count) of a value list into a fresh list.</summary>
    private static List<T> CopyRange<T>(IReadOnlyList<T> values, int start, int count)
    {
        List<T> copy = new(count);
        for (var i = 0; i < count; i++)
        {
            copy.Add(values[start + i]);
        }

        return copy;
    }

    /// <summary>
    /// Decimal square root: decimal carries no native root, so the value
    /// evaluates through the correctly-rounded double sqrt (deterministic
    /// across platforms; the band pixels never need more precision).
    /// </summary>
    private static decimal Sqrt(decimal value) => (decimal)Math.Sqrt((double)value);
}
