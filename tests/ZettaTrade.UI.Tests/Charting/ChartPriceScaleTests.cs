namespace ZettaTrade.UI.Tests.Charting;

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using FluentAssertions;
using Xunit;
using ZettaTrade.UI.Charting;

/// <summary>
/// Pins for the chart's vertical price-scale math: the padded spread of the
/// visible window and the two-way price↔pixel mapping that candles, grid
/// lines, axis labels, the hover crosshair and the hover price pill share.
/// Pure facts — the canvas itself is UIA-invisible.
/// </summary>
public sealed class ChartPriceScaleTests
{
    /// <summary>Plot rect used across the facts (pixels).</summary>
    private static readonly Rect Plot = new(0, 0, 1200.0, 600.0);

    #region Padded Spread

    /// <summary>
    /// The padded spread grows the raw candle spread by the padding ratio
    /// on BOTH ends: the scale keeps the extreme candles (and their labels)
    /// clear of the plot edges.
    /// </summary>
    [Fact]
    public void PaddedSpread_GrowsBothEndsByTheRatio()
    {
        var (min, max, range) = ChartPriceScale.PaddedSpread(100m, 200m);

        min.Should().Be(90m, "ten percent of the raw spread pads the minimum");
        max.Should().Be(210m, "ten percent of the raw spread pads the maximum");
        range.Should().Be(120m, "the padded extremes own the mapping's span");
    }

    /// <summary>
    /// A flat visible window (every bar prints one price — a fresh
    /// illiquid listing) still owns a scale: the substituted unit raw range
    /// is padded, so the single price maps LOW on the axis instead of a
    /// degenerate zero span, and the mapping never divides by zero.
    /// </summary>
    [Fact]
    public void PaddedSpread_AFlatWindowKeepsAWorkingScale()
    {
        var (min, max, range) = ChartPriceScale.PaddedSpread(42m, 42m);

        min.Should().Be(41.9m, "the unit raw range pads to the minimum");
        max.Should().Be(42.1m, "the unit raw range pads to the maximum");
        range.Should().Be(0.2m);
    }

    /// <summary>
    /// A one-bar window rides the same padding: its high-low spread (zero
    /// for a doji) maps by the flat-window rule.
    /// </summary>
    [Fact]
    public void PaddedSpread_AOneBarWindowUsesTheFlatRule()
    {
        var (min, _, range) = ChartPriceScale.PaddedSpread(7.5m, 7.5m);

        min.Should().Be(7.4m);
        range.Should().Be(0.2m);
    }

    #endregion

    #region Price Mapping

    /// <summary>
    /// The headroom anchors: the padded minimum maps a headroom ABOVE the
    /// plot's bottom, the padded maximum a headroom BELOW the plot's top —
    /// both keep their labels and centered pill glyphs inside.
    /// </summary>
    [Fact]
    public void PriceToY_TheHeadroomAnchorsStayInsideThePlot()
    {
        ChartPriceScale.PriceToY(Plot, 90m, 90m, 120m)
            .Should().BeApproximately(588f, 1e-3f, "the padded minimum rides above the bottom headroom");
        ChartPriceScale.PriceToY(Plot, 210m, 90m, 120m)
            .Should().BeApproximately(12f, 1e-3f, "the padded maximum rides below the top headroom");
    }

    /// <summary>
    /// Top-to-bottom price ordering: a higher price always maps at most the
    /// same y, and the midpoint of the scale maps to the plot's vertical
    /// middle.
    /// </summary>
    [Fact]
    public void PriceToY_HigherPricesMapHigherOnTheScreen()
    {
        var topY = ChartPriceScale.PriceToY(Plot, 210m, 90m, 120m);
        var midY = ChartPriceScale.PriceToY(Plot, 150m, 90m, 120m);
        var bottomY = ChartPriceScale.PriceToY(Plot, 90m, 90m, 120m);

        topY.Should().BeLessThan(midY);
        midY.Should().BeLessThan(bottomY);
        midY.Should().BeApproximately(300f, 1f, "the scale's midpoint maps to the plot's vertical middle");
    }

    /// <summary>
    /// The two directions invert each other on the whole mapped band: the
    /// hover crosshair's pill must read the true price under the cursor.
    /// </summary>
    [Theory]
    [InlineData(90)]
    [InlineData(100)]
    [InlineData(135.25)]
    [InlineData(150)]
    [InlineData(180.75)]
    [InlineData(210)]
    public void TheMappingRoundtrips_PriceToYThenYToPrice(decimal price)
    {
        var y = ChartPriceScale.PriceToY(Plot, price, 90m, 120m);

        ChartPriceScale.YToPrice(y, Plot, 90m, 120m)
            .Should().BeApproximately(price, 0.01m, "the inverse mapping reads the price the cursor sits on");
    }

    /// <summary>
    /// The inverse never throws outside the mapped band and extrapolates
    /// linearly: above the plot reads above the padded maximum, below reads
    /// below the padded minimum — the hover pill stays honest in the
    /// headroom strips instead of clamping or failing.
    /// </summary>
    [Fact]
    public void YToPrice_OutsideTheMappedBand_ExtrapolatesWithoutThrowing()
    {
        Action above = () =>
        {
            var price = ChartPriceScale.YToPrice(-500.0, Plot, 90m, 120m);
            price.Should().BeGreaterThan(210m, "a y above the top reads a price above the padded maximum");
        };
        Action below = () =>
        {
            var price = ChartPriceScale.YToPrice(1400.0, Plot, 90m, 120m);
            price.Should().BeLessThan(90m, "a y below the bottom reads a price below the padded minimum");
        };

        above.Should().NotThrow();
        below.Should().NotThrow();
    }

    /// <summary>
    /// A plot sliver (window shorter than both headrooms) must not throw
    /// either direction and must keep the top-down price ordering — the
    /// degenerate map clamps the usable span instead of inverting it.
    /// </summary>
    [Fact]
    public void TheMapping_ADegeneratePlotSliver_StaysAliveAndOrdered()
    {
        var sliver = new Rect(0, 0, 300.0, 5.0);

        Action both = () =>
        {
            var minY = ChartPriceScale.PriceToY(sliver, 41.9m, 41.9m, 0.2m);
            var maxY = ChartPriceScale.PriceToY(sliver, 42.1m, 41.9m, 0.2m);

            maxY.Should().BeLessThan(minY, "a higher price keeps its place above the lower one");
            ChartPriceScale.YToPrice(minY, sliver, 41.9m, 0.2m).Should().BeApproximately(41.9m, 0.01m);
            ChartPriceScale.YToPrice(maxY, sliver, 41.9m, 0.2m).Should().BeApproximately(42.1m, 0.01m);
        };

        both.Should().NotThrow();
    }

    #endregion
}
