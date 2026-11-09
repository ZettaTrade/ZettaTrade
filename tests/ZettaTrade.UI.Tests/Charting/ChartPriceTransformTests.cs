namespace ZettaTrade.UI.Tests.Charting;

using System;
using System.Collections.Generic;
using FluentAssertions;
using Xunit;
using ZettaTrade.UI.Charting;

/// <summary>
/// Pins for the chart's Y-axis scale-mode transforms (T-090): the log and
/// percent domain reads, their inverse edges (the hover pill and the axis
/// labels live there), and the strict positive-domain guards — an invalid
/// price clamps VISIBLY to the floor, never throws and never substitutes
/// another mode. Pure facts — the canvas itself is UIA-invisible.
/// </summary>
public sealed class ChartPriceTransformTests
{
    #region Positive Floor

    /// <summary>
    /// The log domain's floor is the smallest positive price among the
    /// visible candidates, so the smallest real candle's prices map at the
    /// honest bottom of the log scale.
    /// </summary>
    [Fact]
    public void PositiveFloor_TakesTheSmallestPositivePrice()
    {
        var floor = ChartPriceTransform.PositiveFloor([120m, 0m, 45.5m, 310m, -7m]);

        floor.Should().Be(45.5m, "the smallest positive candidate owns the domain floor");
    }

    /// <summary>
    /// A window with NO positive price (every candle corrupt) keeps the log
    /// domain defined on the fallback floor — the transform stays total.
    /// </summary>
    [Fact]
    public void PositiveFloor_WithoutPositivePrices_FallsBack()
    {
        var floor = ChartPriceTransform.PositiveFloor([0m, -5m, 0m]);

        floor.Should().Be(ChartPriceTransform.LogFallbackFloor, "log10(1) = 0 keeps every transform defined");
    }

    #endregion

    #region Log Mode

    /// <summary>
    /// Log golden values: powers of ten map onto exact integers, so a
    /// decade reads as one equal vertical step everywhere on the axis.
    /// </summary>
    [Theory]
    [InlineData(10, 1)]
    [InlineData(100, 2)]
    [InlineData(1000, 3)]
    public void LogMode_MapsPowersOfTenOntoExactIntegers(decimal price, decimal expected)
    {
        var value = ChartPriceTransform.ToAxisValue(price, PriceScaleMode.Logarithmic, anchor: 0m, floor: 1m);

        value.Should().Be(expected, "base-10 log is the log mode's domain");
    }

    /// <summary>
    /// A non-positive price CLAMPS to the floor instead of NaN-ing or
    /// dropping the candle: the corrupted candle draws at the clamped
    /// position — the visible clamp — and the mode stays log.
    /// </summary>
    [Fact]
    public void LogMode_ClampsNonPositivePricesOntoTheFloorVisibly()
    {
        var clamped = ChartPriceTransform.ToAxisValue(0m, PriceScaleMode.Logarithmic, anchor: 0m, floor: 2.5m);
        var negative = ChartPriceTransform.ToAxisValue(-40m, PriceScaleMode.Logarithmic, anchor: 0m, floor: 2.5m);

        clamped.Should().Be((decimal)Math.Log10(2.5d), "zero clamps up to the floor");
        negative.Should().Be(clamped, "a negative price clamps to the same visible floor position");
    }

    /// <summary>
    /// The inverse reconstructs the raw price (the hover pill reads prices,
    /// not domain values), and the round trip is exact on the golden grid.
    /// </summary>
    [Fact]
    public void LogMode_InverseRecoversThePrice()
    {
        var value = ChartPriceTransform.ToAxisValue(350m, PriceScaleMode.Logarithmic, anchor: 0m, floor: 1m);
        var price = ChartPriceTransform.ToPrice(value, PriceScaleMode.Logarithmic, anchor: 0m, floor: 1m);

        price.Should().BeApproximately(350m, 0.0001m, "10^(log10(p)) = p");
    }

    #endregion

    #region Percent Mode

    /// <summary>
    /// Percent golden: +10% and -10% read symmetric around the anchor
    /// close, and the anchor itself reads zero.
    /// </summary>
    [Fact]
    public void PercentMode_ReadsRelativeMovementOverTheAnchor()
    {
        const decimal anchor = 200m;

        ChartPriceTransform.ToAxisValue(220m, PriceScaleMode.Percent, anchor, floor: 1m)
            .Should().Be(10m, "220 over a 200 anchor is plus ten percent");
        ChartPriceTransform.ToAxisValue(180m, PriceScaleMode.Percent, anchor, floor: 1m)
            .Should().Be(-10m, "180 over a 200 anchor is minus ten percent");
        ChartPriceTransform.ToAxisValue(200m, PriceScaleMode.Percent, anchor, floor: 1m)
            .Should().Be(0m, "the anchor close is the zero of the percent axis");
    }

    /// <summary>
    /// A corrupt anchor (zero or negative) clamps to the floor — the
    /// percentages stay defined and visibly extreme; the mode never
    /// silently falls back to linear.
    /// </summary>
    [Fact]
    public void PercentMode_ClampsACorruptAnchorOntoTheFloor()
    {
        var dead = ChartPriceTransform.ToAxisValue(300m, PriceScaleMode.Percent, anchor: 0m, floor: 50m);

        dead.Should().Be(500m, "300 over the clamped 50 anchor reads plus five hundred percent");
    }

    /// <summary>
    /// The inverse returns to the raw price space over the same anchor.
    /// </summary>
    [Fact]
    public void PercentMode_InverseRecoversThePrice()
    {
        var value = ChartPriceTransform.ToAxisValue(165m, PriceScaleMode.Percent, anchor: 150m, floor: 1m);
        var price = ChartPriceTransform.ToPrice(value, PriceScaleMode.Percent, anchor: 150m, floor: 1m);

        price.Should().Be(165m, "the anchored inverse undoes the percent read");
    }

    #endregion

    #region Linear Mode

    /// <summary>
    /// The linear mode is the identity in both directions — the historical
    /// pipeline behavior is untouched when no mode is set.
    /// </summary>
    [Fact]
    public void LinearMode_IsTheIdentity()
    {
        ChartPriceTransform.ToAxisValue(123.45m, PriceScaleMode.Linear, anchor: 0m, floor: 1m)
            .Should().Be(123.45m);
        ChartPriceTransform.ToPrice(123.45m, PriceScaleMode.Linear, anchor: 0m, floor: 1m)
            .Should().Be(123.45m);
    }

    #endregion

    #region Window Guarantee

    /// <summary>
    /// The mode-switch window guarantee's transform half: switching the
    /// mode never throws and never changes the BAR window — the transforms
    /// are stateless functions of the window's data, so the same visible
    /// bars re-map in place (the pixel re-read is the only thing that
    /// changes) and the pan window's indices are mode-foreign state.
    /// </summary>
    [Fact]
    public void ModeSwitch_IsAStatelessRemapOfTheSameWindow()
    {
        var prices = new List<decimal> { 42m, 0m, 97.5m, 310m };

        var linear = ChartPriceTransform.ToAxisValue(97.5m, PriceScaleMode.Linear, 50m, 42m);
        var log = ChartPriceTransform.ToAxisValue(97.5m, PriceScaleMode.Logarithmic, 50m, 42m);
        var percent = ChartPriceTransform.ToAxisValue(97.5m, PriceScaleMode.Percent, 50m, 42m);
        ChartPriceTransform.PositiveFloor(prices).Should().Be(42m);

        linear.Should().Be(97.5m, "the same window re-maps in place, the modes share its data");
        log.Should().BeGreaterThan(0m);
        percent.Should().Be(95m, "97.5 over a 50 anchor is plus ninety-five percent");
    }

    #endregion
}
