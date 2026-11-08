namespace ZettaTrade.UI.Charting;

using System.Collections.Generic;
using ZettaTrade.Charting;

/// <summary>
/// The legend's one row: the shape swatch it draws and the localization
/// resx key its explanation text resolves through. The chart draws the
/// swatch itself; the key rides the control's own resource lookup.
/// </summary>
/// <param name="Shape">The swatch the row paints.</param>
/// <param name="Key">The resx key the row's explanation text resolves through.</param>
public readonly record struct FootprintLegendRow(FootprintLegendShape Shape, string Key);

/// <summary>The swatch shapes the legend can paint (one per drawn footprint shape).</summary>
public enum FootprintLegendShape
{
    /// <summary>A solid entry line with a direction arrow.</summary>
    EntryLine = 0,

    /// <summary>A floating price tag carrying the unrealized PnL.</summary>
    PnlTag = 1,

    /// <summary>A dashed working-order price line.</summary>
    OrderLine = 2,

    /// <summary>A small execution diamond.</summary>
    FillMarker = 3,
}

/// <summary>
/// Pure legend composition for the footprint overlays (T-122): GIVEN what
/// the current frame actually DRAWS (a window may hide a level — only
/// drawn shapes are explained), the rows list every drawn shape EXACTLY
/// once in a stable order — the legend explains every drawn shape and
/// nothing else. The chart's draw pass calls this per frame; keys resolve
/// through the resource lookup the no-data states already use.
/// </summary>
public static class ChartFootprintLegend
{
    /// <summary>
    /// The legend rows for what the frame draws: an entry line when the
    /// position line rendered (the row's explanation names the direction),
    /// the PnL tag row when the floating tag renders, one working-order
    /// row for any drawn dashed line, one fills row for any drawn diamond.
    /// Empty input draws no legend at all.
    /// </summary>
    /// <param name="position">The drawn entry line's direction, or null when none drew.</param>
    /// <param name="hasPnlTag">Whether the floating PnL tag drew.</param>
    /// <param name="hasOrderLines">Whether at least one dashed order line drew.</param>
    /// <param name="hasFillMarkers">Whether at least one fill diamond drew.</param>
    /// <returns>The rows in their stable draw order (entry, tag, orders, fills).</returns>
    public static IReadOnlyList<FootprintLegendRow> Rows(
        PositionDirection? position,
        bool hasPnlTag,
        bool hasOrderLines,
        bool hasFillMarkers)
    {
        var rows = new List<FootprintLegendRow>();

        if (position is { } direction)
        {
            rows.Add(new FootprintLegendRow(
                FootprintLegendShape.EntryLine,
                direction == PositionDirection.Long
                    ? FootprintLegendKeys.EntryLineLongKey
                    : FootprintLegendKeys.EntryLineShortKey));
        }

        if (hasPnlTag)
        {
            rows.Add(new FootprintLegendRow(FootprintLegendShape.PnlTag, FootprintLegendKeys.PnlTagKey));
        }

        if (hasOrderLines)
        {
            rows.Add(new FootprintLegendRow(FootprintLegendShape.OrderLine, FootprintLegendKeys.OrderLineKey));
        }

        if (hasFillMarkers)
        {
            rows.Add(new FootprintLegendRow(FootprintLegendShape.FillMarker, FootprintLegendKeys.FillMarkerKey));
        }

        return rows;
    }
}

/// <summary>The resx keys the legend rows resolve through (the Chart module's prefix).</summary>
internal static class FootprintLegendKeys
{
    /// <summary>The long entry line's explanation.</summary>
    public const string EntryLineLongKey = "Chart.Footprint.Legend.EntryLineLong";

    /// <summary>The short entry line's explanation.</summary>
    public const string EntryLineShortKey = "Chart.Footprint.Legend.EntryLineShort";

    /// <summary>The floating PnL tag's explanation.</summary>
    public const string PnlTagKey = "Chart.Footprint.Legend.PnlTag";

    /// <summary>The dashed working-order line's explanation.</summary>
    public const string OrderLineKey = "Chart.Footprint.Legend.OrderLine";

    /// <summary>The execution diamond's explanation.</summary>
    public const string FillMarkerKey = "Chart.Footprint.Legend.FillMarker";
}
