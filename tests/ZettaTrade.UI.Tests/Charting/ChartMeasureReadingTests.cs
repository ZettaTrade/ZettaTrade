namespace ZettaTrade.UI.Tests.Charting;

using System;
using FluentAssertions;
using Xunit;
using ZettaTrade.Charting;

/// <summary>
/// The measure ruler's readout goldens (T-200): the framed move reads its
/// delta, percent and R-multiple off the anchors and the ticket's stop —
/// a long reads positive R, a short negative, and the division refuses
/// loudly (never a default substitution): no stop or a stop on the entry
/// reads no R row, a zero entry price reads no percent row.
/// </summary>
public sealed class ChartMeasureReadingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = new(2026, 11, 1, 12, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LongMove_StopBelowEntry_ReadsPositiveR()
    {
        var entry = new ChartDrawingAnchor(T0, 100m);
        var exit = new ChartDrawingAnchor(T1, 105m);

        var reading = ChartMeasureReading.Read(entry, exit, stopPrice: 98m);

        reading.DeltaPrice.Should().Be(5m);
        reading.DeltaPercent.Should().Be(5m, "five off a hundred entry is exactly five percent.");
        reading.RMultiple.Should().Be(2.5m, "the move divides by the entry-to-stop risk (100−98 = 2).");
    }

    [Fact]
    public void ShortMove_StopAboveEntry_ReadsNegativeR()
    {
        var entry = new ChartDrawingAnchor(T0, 100m);
        var exit = new ChartDrawingAnchor(T1, 95m);

        var reading = ChartMeasureReading.Read(entry, exit, stopPrice: 102m);

        reading.DeltaPrice.Should().Be(-5m);
        reading.DeltaPercent.Should().Be(-5m);
        reading.RMultiple.Should().Be(-2.5m, "the move against the stop direction reads negative.");
    }

    [Fact]
    public void MissingStopContext_ReadsNoRMultiple_KeepsThePriceRows()
    {
        var entry = new ChartDrawingAnchor(T0, 100m);
        var exit = new ChartDrawingAnchor(T1, 105m);

        var reading = ChartMeasureReading.Read(entry, exit, stopPrice: null);

        reading.DeltaPrice.Should().Be(5m);
        reading.DeltaPercent.Should().Be(5m);
        reading.RMultiple.Should().BeNull("without a stop the risk distance is unknown, not zero.");
    }

    [Fact]
    public void StopOnTheEntry_ZeroRisk_ReadsNoRMultiple()
    {
        var entry = new ChartDrawingAnchor(T0, 100m);
        var exit = new ChartDrawingAnchor(T1, 105m);

        var reading = ChartMeasureReading.Read(entry, exit, stopPrice: 100m);

        reading.RMultiple.Should().BeNull(
            "a zero risk distance would explode the division; the readout omits the R row instead of defaulting.");
    }

    [Fact]
    public void ZeroEntryPrice_ReadsNoPercent_KeepsDeltaAndR()
    {
        var entry = new ChartDrawingAnchor(T0, 0m);
        var exit = new ChartDrawingAnchor(T1, 5m);

        var reading = ChartMeasureReading.Read(entry, exit, stopPrice: 1m);

        reading.DeltaPrice.Should().Be(5m);
        reading.DeltaPercent.Should().BeNull("the percentage division refuses on a zero entry.");
        reading.RMultiple.Should().Be(5m, "the risk distance (|0−1| = 1) still prices the R row.");
    }

    [Fact]
    public void SmallPrecisionMove_RoundsThroughTheDivision_Honestly()
    {
        var entry = new ChartDrawingAnchor(T0, 100m);
        var exit = new ChartDrawingAnchor(T1, 100.5m);

        var reading = ChartMeasureReading.Read(entry, exit, stopPrice: 99.75m);

        reading.DeltaPrice.Should().Be(0.5m);
        reading.RMultiple.Should().Be(2m, "0.5 over a 0.25 risk is exactly 2 R.");
    }
}
