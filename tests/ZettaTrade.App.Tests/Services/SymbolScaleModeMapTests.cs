namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.UI.Charting;

/// <summary>
/// Pins for the persisted per-symbol Y-axis scale mode map (T-090): the
/// stored document shape, the fail-safe parse (a malformed document or an
/// unparseable symbol or mode name drops its entry), the strict mode parse
/// (no silent default substitution), the inverse serialization with a stable
/// ordinal order, and the WithEntry patch that touches only its own symbol.
/// Pure facts.
/// </summary>
public sealed class SymbolScaleModeMapTests
{
    /// <summary>
    /// The exact parse golden: the persisted shape maps normalized symbols
    /// to the PriceScaleMode enum names; unrecognized symbol names
    /// re-normalize (case and spacing) into the canonical form.
    /// </summary>
    [Fact]
    public void Parse_AStoredDocument_YieldsTheTypedMap()
    {
        var map = SymbolScaleModeMap.Parse("""{"btc/usdt":"Logarithmic","ETH/BTC":"Percent"}""");

        map.Should().HaveCount(2, "both entries parse");
        map["BTC/USDT"].Should().Be(PriceScaleMode.Logarithmic, "the symbol normalizes; the mode parses exactly");
        map["ETH/BTC"].Should().Be(PriceScaleMode.Percent);
    }

    /// <summary>The mode parse is case-insensitive but strictly defined.</summary>
    [Theory]
    [InlineData("linear", PriceScaleMode.Linear)]
    [InlineData("LOGARITHMIC", PriceScaleMode.Logarithmic)]
    [InlineData("Percent", PriceScaleMode.Percent)]
    public void ParseStoredMode_DefinedNamesParse_CaseInsensitive(string stored, PriceScaleMode expected)
    {
        SymbolScaleModeMap.ParseStoredMode(stored).Should().Be(expected);
    }

    /// <summary>
    /// The trading-safety rule on stored preferences: an unrecognized mode
    /// name never substitutes a default — the value is null and the entry
    /// drops.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("1")]
    public void ParseStoredMode_AnUnrecognizedName_YieldsNothing(string stored)
    {
        SymbolScaleModeMap.ParseStoredMode(stored).Should().BeNull();
    }

    /// <summary>
    /// Every unusable piece drops and the usable remainder survives: a
    /// malformed document, a non-object root, a non-string value and an
    /// unrecognized mode name each cost at most their own entry, never the
    /// parse (the fail-safe shape the chart types keep).
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
        SymbolScaleModeMap.Parse(json).Should().BeEmpty("nothing usable was stored");
    }

    [Fact]
    public void Parse_IndividualEntriesDrop_TheRemainderSurvives()
    {
        var json = """
            {
                "btc/usdt": "Logarithmic",
                "eth/btc": "No Such Mode",
                "": "Percent",
                "sol/usdc": "Linear"
            }
            """;

        var map = SymbolScaleModeMap.Parse(json);

        map.Should().HaveCount(2, "the blank key and the unrecognized mode drop individually");
        map.Should().ContainKey("BTC/USDT").WhoseValue.Should().Be(PriceScaleMode.Logarithmic);
        map.Should().ContainKey("SOL/USDC").WhoseValue.Should().Be(PriceScaleMode.Linear);
    }

    /// <summary>
    /// The serialization is the exact inverse and stable: ordinal symbol
    /// order, the enum name back, and an empty map to the empty shell.
    /// </summary>
    [Fact]
    public void Serialize_IsTheInverse_InStableOrdinalOrder()
    {
        var map = SymbolScaleModeMap.Parse("""{"sol/usdc":"Percent","btc/usdt":"Logarithmic"}""");

        SymbolScaleModeMap.Serialize(map).Should()
            .Be("""{"BTC/USDT":"Logarithmic","SOL/USDC":"Percent"}""", "ordinal order, normalized names, exact inverse");

        SymbolScaleModeMap.Serialize(new Dictionary<string, PriceScaleMode>(StringComparer.Ordinal)).Should()
            .Be("{}", "an empty map serializes to the empty shell");
    }

    /// <summary>
    /// The parse-serialize round trip keeps every usable entry (fail-safe
    /// documents never grow garbage back).
    /// </summary>
    [Fact]
    public void Serialize_OfAParsedDocument_RoundTrips()
    {
        const string json = """{"BTC/USDT":"Logarithmic","SOL/USDC":"Percent"}""";

        SymbolScaleModeMap.Serialize(SymbolScaleModeMap.Parse(json)).Should().Be(json);
    }

    /// <summary>
    /// The WithEntry patch writes exactly its own symbol's mode and passes
    /// every other symbol's entry through untouched (the whole-map
    /// persistence keeps its shape).
    /// </summary>
    [Fact]
    public void WithEntry_TouchesOnlyItsOwnSymbol()
    {
        var current = SymbolScaleModeMap.Parse("""{"BTC/USDT":"Logarithmic","ETH/BTC":"Percent"}""");

        var updated = SymbolScaleModeMap.WithEntry(current, "ETH/BTC", PriceScaleMode.Linear);

        updated.Should().HaveCount(2);
        updated["BTC/USDT"].Should().Be(PriceScaleMode.Logarithmic, "the other symbols keep their entries");
        updated["ETH/BTC"].Should().Be(PriceScaleMode.Linear, "the addressed symbol's entry becomes exactly the new pick");
        current["ETH/BTC"].Should().Be(PriceScaleMode.Percent, "the input map is never mutated");
    }

    /// <summary>
    /// Even a pick equal to the linear default persists: the user chose it,
    /// the entry exists (the WithEntry contract).
    /// </summary>
    [Fact]
    public void WithEntry_TheLinearDefault_StillPersists()
    {
        var updated = SymbolScaleModeMap.WithEntry(
            new Dictionary<string, PriceScaleMode>(StringComparer.Ordinal),
            "BTC/USDT",
            PriceScaleMode.Linear);

        updated.Should().ContainKey("BTC/USDT").WhoseValue.Should().Be(PriceScaleMode.Linear);
    }
}
