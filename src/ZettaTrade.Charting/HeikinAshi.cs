namespace ZettaTrade.Charting;

using System;
using System.Collections.Generic;
using ZettaTrade.Core.Entities;

/// <summary>
/// The client-side Heikin-Ashi transform (T-089): a left-to-right pass over
/// the loaded candle series that produces the smoothed HA series in the
/// board's canonical formula:
/// <list type="bullet">
/// <item><c>HA-Close = (O + H + L + C) / 4</c></item>
/// <item><c>HA-Open = (prevHA-Open + prevHA-Close) / 2</c>; the first bar is
/// seeded with <c>(O + C) / 2</c></item>
/// <item><c>HA-High = max(H, HA-Open, HA-Close)</c></item>
/// <item><c>HA-Low = min(L, HA-Open, HA-Close)</c></item>
/// </list>
/// <para>
/// The open times and volumes pass through unchanged, so the transformed
/// series stays aligned with its source by index: the chart slices it with
/// the visible window exactly like an overlay value series (the slot axis,
/// the volume-at-price histogram and the indicator/VWAP alignment keep
/// working over it untouched).
/// </para>
/// <para>
/// The transform runs once per delivered series (the chart's ViewModel
/// publishes the result), never per draw pass: each HA open depends on the
/// previous HA bar, so the chain must always start at the series' head — a
/// window-local transform would shift the first visible bar's values on
/// every pan.
/// </para>
/// </summary>
public static class HeikinAshi
{
    /// <summary>
    /// Compute the Heikin-Ashi series for the FULL loaded series. Pure:
    /// nothing outside the input is read, the input is never mutated.
    /// </summary>
    /// <param name="bars">The ascending open-time candle series the chart holds.</param>
    /// <returns>The HA series aligned with <paramref name="bars"/> by index
    /// (an empty input yields an empty result).</returns>
    public static IReadOnlyList<Bar> Compute(IReadOnlyList<Bar> bars)
    {
        var count = bars.Count;
        if (count == 0)
        {
            return [];
        }

        var transformed = new Bar[count];

        var first = bars[0];
        var firstClose = (first.Open + first.High + first.Low + first.Close) / 4m;
        var firstOpen = (first.Open + first.Close) / 2m;
        transformed[0] = new Bar(
            first.Time,
            firstOpen,
            Math.Max(Math.Max(first.High, firstOpen), firstClose),
            Math.Min(Math.Min(first.Low, firstOpen), firstClose),
            firstClose,
            first.Volume);

        for (var i = 1; i < count; i++)
        {
            var bar = bars[i];
            var previous = transformed[i - 1];

            var close = (bar.Open + bar.High + bar.Low + bar.Close) / 4m;
            var open = (previous.Open + previous.Close) / 2m;
            transformed[i] = new Bar(
                bar.Time,
                open,
                Math.Max(Math.Max(bar.High, open), close),
                Math.Min(Math.Min(bar.Low, open), close),
                close,
                bar.Volume);
        }

        return transformed;
    }
}
