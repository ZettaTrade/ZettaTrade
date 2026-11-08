namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Charting;

/// <summary>
/// Pins for the persisted per-symbol chart-type map (T-089): the stored
/// document shape, the fail-safe parse (a malformed document or an
/// unparseable symbol or type name drops its entry), the strict type parse
/// (no silent default substitution), the inverse serialization with a stable
/// ordinal order, and the WithEntry patch that touches only its own symbol.
/// Pure facts.
/// </summary>
public sealed class SymbolChartTypeMapTests
{
    /// <summary>
    /// The exact parse golden: the persisted shape maps normalized symbols
    /// to the ChartSeriesType enum names; unrecognized symbol names
    /// re-normalize (case and spacing) into the canonical form.
    /// </summary>
    [Fact]
    public void Parse_AStoredDocument_YieldsTheTypedMap()
    {
        var map = SymbolChartTypeMap.Parse("""{"btc/usdt":"HeikinAshi","ETH/BTC":"Area"}""");

        map.Should().HaveCount(2, "both entries parse");
        map["BTC/USDT"].Should().Be(ChartSeriesType.HeikinAshi, "the symbol normalizes; the type parses exactly");
        map["ETH/BTC"].Should().Be(ChartSeriesType.Area);
    }

    /// <summary>The type parse is case-insensitive but strictly defined.</summary>
    [Theory]
    [InlineData("heikinashi", ChartSeriesType.HeikinAshi)]
    [InlineData("AREA", ChartSeriesType.Area)]
    [InlineData("candlesticks", ChartSeriesType.Candlesticks)]
    [InlineData("Bars", ChartSeriesType.Bars)]
    [InlineData("Line", ChartSeriesType.Line)]
    public void ParseStoredType_DefinedNamesParse_CaseInsensitive(string stored, ChartSeriesType expected)
    {
        SymbolChartTypeMap.ParseStoredType(stored).Should().Be(expected);
    }

    /// <summary>
    /// The trading-safety rule on stored preferences: an unrecognized type
    /// name never substitutes a default — the value is null and the entry
    /// drops.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("1")]
    public void ParseStoredType_AnUnrecognizedName_YieldsNothing(string stored)
    {
        SymbolChartTypeMap.ParseStoredType(stored).Should().BeNull();
    }

    /// <summary>
    /// Every unusable piece drops and the usable remainder survives: a
    /// malformed document, a non-object root, a non-string value, a blank
    /// key and an unrecognized type name each cost at most their own entry,
    /// never the parse. A loose key ("not a symbol") stays as tolerable as
    /// the symbol marks keep — the symbol value object normalizes it into a
    /// Stock symbol the picker would never show, so the entry is inert, never
    /// a crash; no silent substitution happens anywhere.
    /// </summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("[\"BTC/USDT\"]")]
    [InlineData("garbage")]
    public void Parse_AStoredGarbage_YieldsAnEmptyMap(string json)
    {
        SymbolChartTypeMap.Parse(json).Should().BeEmpty("nothing usable was stored");
    }

    [Fact]
    public void Parse_IndividualEntriesDrop_TheRemainderSurvives()
    {
        var json = """
            {
                "btc/usdt": "HeikinAshi",
                "not a symbol": "Line",
                "eth/btc": "No Such Type",
                "": "Bars",
                "   ": "Bars",
                "sol/usdc": "Area"
            }
            """;

        var map = SymbolChartTypeMap.Parse(json);

        map.Should().HaveCount(3, "the blank keys and the unrecognized type drop individually");
        map.Should().ContainKey("BTC/USDT").WhoseValue.Should().Be(ChartSeriesType.HeikinAshi);
        map.Should().ContainKey("NOT A SYMBOL").WhoseValue.Should()
            .Be(ChartSeriesType.Line, "a tolerable key normalizes like the marks map keeps");
        map.Should().ContainKey("SOL/USDC").WhoseValue.Should().Be(ChartSeriesType.Area);
    }

    /// <summary>
    /// The serialization is the exact inverse and stable: ordinal symbol
    /// order, the enum name back, and an empty map to the empty shell.
    /// </summary>
    [Fact]
    public void Serialize_IsTheInverse_InStableOrdinalOrder()
    {
        var map = SymbolChartTypeMap.Parse("""{"sol/usdc":"Area","btc/usdt":"HeikinAshi"}""");

        SymbolChartTypeMap.Serialize(map).Should()
            .Be("""{"BTC/USDT":"HeikinAshi","SOL/USDC":"Area"}""", "ordinal order, normalized names, exact inverse");

        SymbolChartTypeMap.Serialize(new Dictionary<string, ChartSeriesType>(StringComparer.Ordinal)).Should()
            .Be("{}", "an empty map serializes to the empty shell");
    }

    /// <summary>
    /// The parse-serialize round trip keeps every usable entry (fail-safe
    /// documents never grow garbage back).
    /// </summary>
    [Fact]
    public void Serialize_OfAParsedDocument_RoundTrips()
    {
        const string json = """{"BTC/USDT":"HeikinAshi","SOL/USDC":"Area"}""";

        SymbolChartTypeMap.Serialize(SymbolChartTypeMap.Parse(json)).Should().Be(json);
    }

    /// <summary>
    /// The WithEntry patch writes exactly its own symbol's type and passes
    /// every other symbol's entry through untouched (the whole-map
    /// persistence keeps its shape).
    /// </summary>
    [Fact]
    public void WithEntry_TouchesOnlyItsOwnSymbol()
    {
        var current = SymbolChartTypeMap.Parse("""{"BTC/USDT":"HeikinAshi","ETH/BTC":"Line"}""");

        var updated = SymbolChartTypeMap.WithEntry(current, "ETH/BTC", ChartSeriesType.Bars);

        updated.Should().HaveCount(2);
        updated["BTC/USDT"].Should().Be(ChartSeriesType.HeikinAshi, "the other symbols keep their entries");
        updated["ETH/BTC"].Should().Be(ChartSeriesType.Bars, "the addressed symbol's entry becomes exactly the new pick");
        current["ETH/BTC"].Should().Be(ChartSeriesType.Line, "the input map is never mutated");
    }

    /// <summary>
    /// Even a pick equal to the candles default persists: the user chose it,
    /// the entry exists (the WithEntry contract).
    /// </summary>
    [Fact]
    public void WithEntry_TheCandlesDefault_StillPersists()
    {
        var updated = SymbolChartTypeMap.WithEntry(
            new Dictionary<string, ChartSeriesType>(StringComparer.Ordinal),
            "BTC/USDT",
            ChartSeriesType.Candlesticks);

        updated.Should().ContainKey("BTC/USDT").WhoseValue.Should().Be(ChartSeriesType.Candlesticks);
    }
}
