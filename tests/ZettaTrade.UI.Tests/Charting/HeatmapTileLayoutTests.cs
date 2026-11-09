namespace ZettaTrade.UI.Tests.Charting;

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using FluentAssertions;
using Xunit;
using ZettaTrade.UI.Charting;

/// <summary>
/// Pins for the heatmap's pure squarified layout (T-121): the placed set
/// keeps the input (rank) order, unverifiable weights own no area, the
/// plan fills the plot edge to edge, the areas follow the weights
/// proportionally, and the same input plans the same output on every
/// call (the ticker storm re-plans deterministically).
/// </summary>
public sealed class HeatmapTileLayoutTests
{
    /// <summary>The plot the facts share (pixels).</summary>
    private static readonly Rect Plot = new(0, 0, 600.0, 400.0);

    private static HeatmapTile Tile(string symbol, decimal? weight) =>
        new(symbol, null, weight);

    #region Empty states

    /// <summary>An empty tile list owns no placement — no fabricated rect.</summary>
    [Fact]
    public void AnEmptyTileset_ResolvesToAnEmptyPage()
    {
        HeatmapTileLayout.Layout([], Plot)
            .Should().BeEmpty("nothing ranked owns nothing drawn");
    }

    /// <summary>A zero-area plot owns no placement either.</summary>
    [Fact]
    public void AZeroAreaPlot_ResolvesToAnEmptyPage()
    {
        HeatmapTileLayout.Layout(
                [Tile("BTC/USDT", 10m)],
                new Rect(0, 0, 0.0, 0.0))
            .Should().BeEmpty("a plot with no pixels maps no tiles onto it");
    }

    /// <summary>A degenerate all-unpriced set stays an empty page.</summary>
    [Fact]
    public void AnAllUnpricedSet_ResolvesToAnEmptyPage()
    {
        HeatmapTileLayout.Layout(
                [Tile("BTC/USDT", null), Tile("ETH/USDT", 0m)],
                Plot)
            .Should().BeEmpty("null and non-positive weights invent no area");
    }

    /// <summary>Unpriced tiles are skipped; the placed ones keep their rank order.</summary>
    [Fact]
    public void UnpricedTiles_AreSkipped_ThePlacedKeepTheRankOrder()
    {
        var placed = HeatmapTileLayout.Layout(
            [
                Tile("BTC/USDT", 30m),
                Tile("ZERO/USDT", 0m),
                Tile("ETH/USDT", null),
                Tile("SOL/USDT", 10m),
            ],
            Plot);

        placed.Select(static tile => tile.SymbolText)
            .Should().Equal(
                (IEnumerable<string>)["BTC/USDT", "SOL/USDT"],
                "the layout never re-sorts behind the feed's rank");
    }

    #endregion Empty states

    #region Geometry

    /// <summary>One tile fills the whole plot.</summary>
    [Fact]
    public void ASingleTile_FillsThePlot()
    {
        var placed = HeatmapTileLayout.Layout([Tile("BTC/USDT", 1m)], Plot);

        placed.Should().HaveCount(1);
        var bounds = placed[0].Bounds;
        bounds.X.Should().BeApproximately(Plot.X, 1e-9);
        bounds.Y.Should().BeApproximately(Plot.Y, 1e-9);
        bounds.Width.Should().BeApproximately(Plot.Width, 1e-9);
        bounds.Height.Should().BeApproximately(Plot.Height, 1e-9);
    }

    /// <summary>The placed set tiles the plot edge to edge: the plan leaves no crumb.</summary>
    [Fact]
    public void ThePlacements_TileThePlotEdgeToEdge()
    {
        var tiles = new List<HeatmapTile>();
        var weights = new decimal[] { 400m, 260m, 130m, 85m, 40m, 12m, 7m };
        for (var index = 0; index < weights.Length; index++)
        {
            tiles.Add(Tile($"P{index}/USDT", weights[index]));
        }

        var placed = HeatmapTileLayout.Layout(tiles, Plot);

        double sum = 0;
        foreach (var tile in placed)
        {
            sum += tile.Bounds.Width * tile.Bounds.Height;
        }

        sum.Should().BeApproximately(Plot.Width * Plot.Height, 1e-6,
            "the weights' areas sum to the whole plot - no hairline crumb survives");
    }

    /// <summary>The largest weight owns the largest area, proportionally.</summary>
    [Fact]
    public void Weights_MapToAreasProportionally()
    {
        var placed = HeatmapTileLayout.Layout(
            [Tile("BIG/USDT", 30m), Tile("SMALL/USDT", 10m)],
            Plot);

        var big = placed[0].Bounds.Width * placed[0].Bounds.Height;
        var small = placed[1].Bounds.Width * placed[1].Bounds.Height;
        big.Should().BeGreaterThan(small, "the rank's weight sizes the tile");
        (big / small).Should().BeApproximately(3.0, 1e-6,
            "areas follow the weights' ratio");
    }

    /// <summary>The same input plans the same output on every call.</summary>
    [Fact]
    public void TheSameInput_PlansTheSameOutputTwice()
    {
        var tiles = new HeatmapTile[]
        {
            Tile("BTC/USDT", 15m), Tile("ETH/USDT", 9m), Tile("SOL/USDT", 3m),
        };
        var first = HeatmapTileLayout.Layout(tiles, Plot);
        var second = HeatmapTileLayout.Layout(tiles, Plot);

        second.Count.Should().Be(first.Count);
        for (var index = 0; index < first.Count; index++)
        {
            second[index].SymbolText.Should().Be(first[index].SymbolText);
            second[index].Bounds.Should().Be(first[index].Bounds);
        }
    }

    #endregion Geometry
}
