namespace ZettaTrade.UI.Charting;

using System;
using System.Collections.Generic;
using SkiaSharp;
using ZettaTrade.Core.Entities;

/// <summary>
/// Pure multi-series helpers behind the symbol-compare overlay (T-124):
/// the open-time alignment of a compare layer against the main series'
/// visible window, the layer's own percent anchor, and the axis-domain
/// candidates the shared price scale unions. The layers never mutate;
/// the alignment materializes null gaps — a window slot the layer's own
/// fetch did not span draws nothing there instead of fabricating a
/// neighbor value, the chart's honest-gap rule.
/// </summary>
public static class ChartCompareSeries
{
    /// <summary>
    /// How many compare layers the chart keeps — the legend chip strip's
    /// whole row budget: the ViewModel refuses a fifth pick, and the
    /// renderer defensively slices its resolves to the same cap.
    /// </summary>
    public const int MaxCompareLayers = 4;

    /// <summary>
    /// The theme resource keys the legend colors resolve through, in
    /// layer order: the palettes override them per layer; a missing key
    /// (a theme that predates the compare feature) falls back to
    /// <see cref="FallbackColors"/>. The key count IS the layer cap — the
    /// two arrays modulo the same list index, so one test pins both.
    /// </summary>
    public static readonly string[] ColorKeys = ["Compare1", "Compare2", "Compare3", "Compare4"];

    /// <summary>
    /// The fallback legend colors, in layer order (cyan, violet, pink,
    /// yellow): distinct against the built-in dark candles and every
    /// theme's text, readable inside the plot and over the volume
    /// profile's translucent bars alike.
    /// </summary>
    public static readonly SKColor[] FallbackColors =
    [
        new(34, 211, 238),
        new(167, 139, 250),
        new(244, 114, 182),
        new(250, 204, 21),
    ];

    /// <summary>
    /// Align one compare layer onto the main series' visible window: the
    /// layer's own close for every visible bar sharing the EXACT open
    /// time, null elsewhere — an unshared open time is a gap, never a
    /// nearest-neighbor substitute. Both inputs ascend by open time; the
    /// walk advances two pointers once over each of them.
    /// </summary>
    /// <param name="layerBars">The compare layer's own fetched series (ascending open times).</param>
    /// <param name="visibleBars">The main series' visible window (ascending open times).</param>
    /// <returns>One slot per visible bar; the slot carries the layer's close or null.</returns>
    public static decimal?[] Align(IReadOnlyList<Bar> layerBars, IReadOnlyList<Bar> visibleBars)
    {
        var aligned = new decimal?[visibleBars.Count];
        var cursor = 0;

        for (var i = 0; i < visibleBars.Count; i++)
        {
            var time = visibleBars[i].Time.Ticks;

            while (cursor < layerBars.Count && layerBars[cursor].Time.Ticks < time)
            {
                cursor++;
            }

            aligned[i] = cursor < layerBars.Count && layerBars[cursor].Time.Ticks == time
                ? layerBars[cursor].Close
                : null;
        }

        return aligned;
    }

    /// <summary>
    /// The layer's own percent anchor: ITS first aligned close — each
    /// series rebases to its own zero (the (100→110) ≡ (1000→1100) = +10%
    /// equivalence), the window's first visible slot even. An alignment
    /// without any shared slot has no anchor; the fallback keeps the
    /// transform defined while the resolve pass drops the layer wholesale.
    /// </summary>
    /// <param name="aligned">The layer's alignment against the visible window (see <see cref="Align"/>).</param>
    /// <returns>The first non-null close, or zero.</returns>
    public static decimal AxisAnchor(IReadOnlyList<decimal?> aligned)
    {
        foreach (var value in aligned)
        {
            if (value is { } close)
            {
                return close;
            }
        }

        return 0m;
    }

    /// <summary>
    /// One layer's extended axis-domain candidates (the spread the shared
    /// padded price scale must cover beyond the main series): the closes
    /// mapped through the layer's OWN anchor in the percent mode, and the
    /// raw closes transformed by the shared log domain in the logarithmic
    /// mode (the anchor is meaningless there); the linear mode keeps the
    /// closes themselves. Null gaps never contribute.
    /// </summary>
    /// <param name="aligned">The layer's alignment against the visible window.</param>
    /// <param name="mode">The standing price-scale mode (T-090).</param>
    /// <param name="anchor">The layer's own percent anchor (<see cref="AxisAnchor"/>).</param>
    /// <param name="floor">The unioned positive floor the shared transforms clamp to.</param>
    /// <returns>The layer's axis-domain candidates, one per shared slot.</returns>
    public static List<decimal> LayerAxisCandidates(
        IReadOnlyList<decimal?> aligned,
        PriceScaleMode mode,
        decimal anchor,
        decimal floor)
    {
        var candidates = new List<decimal>(aligned.Count);
        foreach (var value in aligned)
        {
            if (value is not { } close)
            {
                continue;
            }

            candidates.Add(mode switch
            {
                PriceScaleMode.Percent => ChartPriceTransform.ToAxisValue(close, mode, anchor, floor),
                PriceScaleMode.Logarithmic => ChartPriceTransform.ToAxisValue(close, mode, anchor: 0m, floor),
                _ => close,
            });
        }

        return candidates;
    }
}
