namespace ZettaTrade.Charting;

/// <summary>
/// The measure ruler's readout math (T-200): the move one drawing frames,
/// read against the ticket's stop context. The entry anchor prices the
/// move's start, the exit anchor its end; the readout carries the raw
/// price delta, the percentage move off the entry, and the R-multiple —
/// the move divided by the entry-to-stop risk distance. A missing stop
/// context or a zero risk distance (a stop ON the entry) reads no
/// R-multiple: the division never substitutes a default, the readout just
/// omits the R row.
/// </summary>
public static class ChartMeasureReading
{
    /// <summary>
    /// Read the framed move: the price delta (exit minus entry, signed),
    /// the percentage off the entry (null on a zero entry price — the
    /// division is refused, never defaulted), and the signed R-multiple
    /// against the stop context (a move against the stop direction reads
    /// negative; null on a missing stop or a zero risk distance).
    /// </summary>
    public static ChartMeasureReadingResult Read(
        ChartDrawingAnchor entry,
        ChartDrawingAnchor exit,
        decimal? stopPrice)
    {
        var delta = exit.Price - entry.Price;
        decimal? percent = entry.Price == 0
            ? null
            : delta * 100m / entry.Price;

        decimal? risk = stopPrice is null ? null : Math.Abs(entry.Price - stopPrice.Value);
        decimal? multiple = risk is null or 0m ? null : delta / risk.Value;

        return new ChartMeasureReadingResult(delta, percent, multiple);
    }
}

/// <summary>The measure ruler's readout rows (T-200): the framed move's facts.</summary>
/// <param name="DeltaPrice">The signed price move (exit minus entry).</param>
/// <param name="DeltaPercent">The percentage move off the entry; null on a zero entry price.</param>
/// <param name="RMultiple">The signed R-multiple against the stop context; null on a missing stop or zero risk.</param>
public readonly record struct ChartMeasureReadingResult(
    decimal DeltaPrice,
    decimal? DeltaPercent,
    decimal? RMultiple);
