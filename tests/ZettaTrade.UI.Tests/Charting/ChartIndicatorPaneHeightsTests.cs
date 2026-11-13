namespace ZettaTrade.UI.Tests.Charting;

using System;
using System.Collections.Generic;
using FluentAssertions;
using Xunit;
using ZettaTrade.Charting;

/// <summary>
/// Pins for the oscillator panes' persisted heights codec (T-199): the read
/// is tolerant (numbers ride as JSON numbers or numeric strings, unusable
/// rows are skipped, a garbage value serves the empty map — never thrown),
/// the write is the compact camel-keyed map in ordinal key order, and the
/// round trip is lossless for the map the settle pipeline stores.
/// </summary>
public sealed class ChartIndicatorPaneHeightsTests
{
    #region Parse

    /// <summary>A fresh install's empty stored value parses the empty map — every pane rides the layout default.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_AnAbsentStoredValue_ServesTheEmptyMap(string? stored)
    {
        ChartIndicatorPaneHeights.Parse(stored).Should().BeEmpty();
    }

    /// <summary>A garbage stored value (truncated JSON, a scalar root, an array root) serves the empty map — never throws.</summary>
    [Theory]
    [InlineData("{\"RSI\":")]
    [InlineData("42")]
    [InlineData("[140]")]
    [InlineData("not json at all")]
    public void Parse_AGarbageStoredValue_ServesTheEmptyMap(string stored)
    {
        ChartIndicatorPaneHeights.Parse(stored).Should().BeEmpty();
    }

    /// <summary>The canonical shape: whole-pixel numbers keyed by indicator name.</summary>
    [Fact]
    public void Parse_Numbers_LandKeyedByName()
    {
        var heights = ChartIndicatorPaneHeights.Parse("{\"RSI\":140,\"MACD\":200}");

        heights.Should().HaveCount(2);
        heights["RSI"].Should().Be(140);
        heights["MACD"].Should().Be(200);
    }

    /// <summary>The tolerant read: numeric strings ride too (an external writer's shape), fractional values keep their precision.</summary>
    [Fact]
    public void Parse_NumericStrings_AreAccepted()
    {
        var heights = ChartIndicatorPaneHeights.Parse("{\"RSI\":\"140\",\"MACD\":138.5}");

        heights["RSI"].Should().Be(140);
        heights["MACD"].Should().Be(138.5);
    }

    /// <summary>Unusable rows (unparsable values, non-positive heights) are skipped, never thrown — the usable siblings still land.</summary>
    [Fact]
    public void Parse_UnusableRows_AreSkippedAndTheSiblingsSurvive()
    {
        var heights = ChartIndicatorPaneHeights.Parse(
            "{\"RSI\":140,\"MACD\":\"wide\",\"BOLL\":0,\"VWAP\":-3}");

        heights.Keys.Should().BeEquivalentTo("RSI");
    }

    #endregion

    #region Write

    /// <summary>The compact write: ordinal key order, no spaces, whole pixels.</summary>
    [Fact]
    public void Write_IsCompactAndOrdinalOrdered()
    {
        var json = ChartIndicatorPaneHeights.Write(new Dictionary<string, double>
        {
            ["MACD"] = 138.4,
            ["RSI"] = 140,
        });

        json.Should().Be("{\"MACD\":138,\"RSI\":140}");
    }

    /// <summary>An empty map writes the empty object (the fresh-install shape).</summary>
    [Fact]
    public void Write_AnEmptyMap_IsTheEmptyObject()
    {
        ChartIndicatorPaneHeights.Write(new Dictionary<string, double>()).Should().Be("{}");
    }

    /// <summary>A key with JSON-special characters survives the round trip.</summary>
    [Fact]
    public void Write_ASpecialCharacterKey_SurvivesTheRoundTrip()
    {
        var json = ChartIndicatorPaneHeights.Write(new Dictionary<string, double>
        {
            ["RSI \"fast\""] = 140,
        });

        ChartIndicatorPaneHeights.Parse(json)["RSI \"fast\""].Should().Be(140);
    }

    /// <summary>The round trip is lossless for the settle pipeline's whole-pixel map.</summary>
    [Fact]
    public void WriteThenParse_IsLossless()
    {
        var stored = new Dictionary<string, double>
        {
            ["RSI"] = 140,
            ["MACD"] = 200,
        };

        ChartIndicatorPaneHeights.Parse(ChartIndicatorPaneHeights.Write(stored))
            .Should().BeEquivalentTo(stored);
    }

    #endregion
}
