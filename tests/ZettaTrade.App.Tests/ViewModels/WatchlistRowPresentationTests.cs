namespace ZettaTrade.App.Tests.ViewModels;

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.ViewModels;

/// <summary>
/// The watchlist row's presentation facts (T-120, over an invariant
/// culture): the adaptive price precision, the signed percent shape, the
/// compact volume scale, the sparkline's point mapping, and an unpriced
/// row's empty bindings.
/// </summary>
public sealed class WatchlistRowPresentationTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    #region Formatting

    [Theory]
    [InlineData(65432.10, "65,432.10")]
    [InlineData(100, "100.00")]
    [InlineData(2.5, "2.500")]
    [InlineData(0.123456789, "0.123457")]
    public void FormatPrice_ScalesItsPrecision_WithThePriceRange(double price, string expected)
    {
        WatchlistRowPresentation.FormatPrice((decimal)price, Culture).Should().Be(expected);
    }

    [Theory]
    [InlineData(1.5, "+1.50%")]
    [InlineData(-2.25, "-2.25%")]
    [InlineData(0, "0.00%")]
    public void FormatSignedPercent_PrintsTheChangeShape(double changePercent, string expected)
    {
        WatchlistRowPresentation.FormatSignedPercent((decimal)changePercent, Culture).Should().Be(expected);
    }

    [Theory]
    [InlineData(950, "950.00")]
    [InlineData(1520000, "1.52M")]
    [InlineData(5234567, "5.23M")]
    [InlineData(750000000000, "750.00B")]
    public void FormatVolume_CompactsTheBigOnes(double volume, string expected)
    {
        WatchlistRowPresentation.FormatVolume((decimal)volume, Culture).Should().Be(expected);
    }

    #endregion

    #region Sparkline

    [Fact]
    public void BuilderOfPoints_MapsTheRisingRing_TopAndBottom()
    {
        var points = WatchlistRowPresentation.BuildSparklinePoints(
            new decimal[] { 10m, 0m },
            96,
            28);

        points.Should().HaveCount(2);
        points[0].Should().Be(new Point(0, 0), "the highest price rides the top of the canvas");
        points[1].Should().Be(new Point(96, 28), "the lowest price rides the bottom");
    }

    [Fact]
    public void BuilderOfPoints_MapsAFlatRing_OntoTheMidline()
    {
        var points = WatchlistRowPresentation.BuildSparklinePoints(
            new decimal[] { 5m, 5m, 5m },
            96,
            28);

        points.Select(point => point.Y).Should().AllBeEquivalentTo(14.0);
        points.Select(point => point.X).Should().Equal(new System.Collections.Generic.List<double> { 0, 48, 96 });
    }

    [Fact]
    public void BuilderOfPoints_ReturnsEmpty_ForTooFewPoints()
    {
        WatchlistRowPresentation.BuildSparklinePoints(new decimal[] { }, 96, 28).Should().BeEmpty();
        WatchlistRowPresentation.BuildSparklinePoints(new decimal[] { 5m }, 96, 28).Should().BeEmpty();
    }

    #endregion
}
