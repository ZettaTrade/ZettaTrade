namespace ZettaTrade.UI.Tests.Charting;

using FluentAssertions;
using Xunit;
using ZettaTrade.Charting;
using ZettaTrade.UI.Charting;

/// <summary>
/// Pins for the footprint legend's pure composition (T-122): the rows list
/// exactly what the current frame actually DRAWS, each shape once, in the
/// stable draw order (entry, tag, orders, fills); the entry row's key
/// names the direction it drew; nothing drawn means no legend at all.
/// </summary>
public sealed class ChartFootprintLegendTests
{
    /// <summary>Every drawn shape composes one row each, in the stable draw order.</summary>
    [Fact]
    public void EveryDrawnShape_ComposesOneRow_InStableOrder()
    {
        var rows = ChartFootprintLegend.Rows(PositionDirection.Long, hasPnlTag: true, hasOrderLines: true, hasFillMarkers: true);

        rows.Select(static r => r.Shape).Should().Equal(
            FootprintLegendShape.EntryLine,
            FootprintLegendShape.PnlTag,
            FootprintLegendShape.OrderLine,
            FootprintLegendShape.FillMarker);
        rows.Select(static r => r.Key).Should().Equal(
            "Chart.Footprint.Legend.EntryLineLong",
            "Chart.Footprint.Legend.PnlTag",
            "Chart.Footprint.Legend.OrderLine",
            "Chart.Footprint.Legend.FillMarker");
    }

    /// <summary>The entry row's explanation names the direction the line actually drew — short is its own key.</summary>
    [Fact]
    public void TheEntryRow_NamesTheDirectionItDrew()
    {
        ChartFootprintLegend.Rows(PositionDirection.Short, hasPnlTag: false, hasOrderLines: false, hasFillMarkers: false)
            .Should().ContainSingle().Which.Key.Should().Be("Chart.Footprint.Legend.EntryLineShort");
    }

    /// <summary>Only the drawn shapes ride the legend — a hidden level explains nothing.</summary>
    [Fact]
    public void HiddenShapes_ComposeNothing()
    {
        ChartFootprintLegend.Rows(null, hasPnlTag: false, hasOrderLines: false, hasFillMarkers: false)
            .Should().BeEmpty("nothing drawn means no legend");
        ChartFootprintLegend.Rows(null, hasPnlTag: false, hasOrderLines: true, hasFillMarkers: false)
            .Should().ContainSingle().Which.Shape.Should().Be(FootprintLegendShape.OrderLine);
    }
}
