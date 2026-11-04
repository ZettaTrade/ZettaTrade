namespace ZettaTrade.App.Tests.Services;

using System;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;

/// <summary>
/// The persisted marks-map parse/serialize contract: garbage stored values
/// (a malformed document, a non-object root, a non-array exchange entry, a
/// non-string or unparseable symbol entry) drop instead of throwing — the
/// fail-safe the picker depends on — and a usable document round-trips
/// through serialize without reshaping the data.
/// </summary>
public sealed class SymbolMarkMapTests
{
    [Fact]
    public void Parse_AUsableDocument_RestoresTheMap()
    {
        var marks = SymbolMarkMap.Parse("""{"demo":["BTC/USDT","ETH/BTC"],"bybit":["SOL/USDT"]}""");

        marks.Should().HaveCount(2);
        marks["demo"].Should().BeEquivalentTo(["BTC/USDT", "ETH/BTC"]);
        marks["bybit"].Should().BeEquivalentTo(["SOL/USDT"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    public void Parse_GarbageStoredValues_ProduceAnEmptyMap(string? json)
    {
        SymbolMarkMap.Parse(json).Should().BeEmpty("a malformed document drops, it never throws");
    }

    [Fact]
    public void Parse_ANonArrayExchangeEntry_DropsThatExchangeAndKeepsTheRest()
    {
        var marks = SymbolMarkMap.Parse("""{"demo":"BTC/USDT","bybit":["ETH/USDT"]}""");

        marks.Keys.Should().BeEquivalentTo(["bybit"]);
    }

    /// <summary>
    /// Non-string and blank entries drop; duplicates dedupe. A non-blank
    /// token the SYMBOL grammar accepts (e.g. a free-form stock-like name
    /// no exchange lists) stays in the map by design: it matches nothing in
    /// any list, so it is inert — the picker never resurrects it.
    /// </summary>
    [Fact]
    public void Parse_NonStringAndBlankEntries_DropAndTheUsableOnesSurvive()
    {
        var marks = SymbolMarkMap.Parse(
            """{"demo":[42,null,"","   ","BTC/USDT","NOT A SYMBOL!","ETH/BTC","ETH/BTC"]}""");

        marks["demo"].Should().BeEquivalentTo(
            ["BTC/USDT", "NOT A SYMBOL!", "ETH/BTC"]);
    }

    [Fact]
    public void Parse_AnEmptyExchangeArray_DropsTheExchange()
    {
        SymbolMarkMap.Parse("""{"demo":[]}""").Should().BeEmpty();
    }

    [Fact]
    public void Serialize_AMap_ProducesTheStoredShape()
    {
        var marks = SymbolMarkMap.Parse("""{"demo":["BTC/USDT","ETH/BTC"]}""");

        SymbolMarkMap.Serialize(marks).Should().Be("""{"demo":["BTC/USDT","ETH/BTC"]}""");
    }

    [Fact]
    public void Serialize_AnEmptyMap_ProducesAnEmptyObject()
    {
        SymbolMarkMap.Serialize(SymbolMarkMap.Parse(null)).Should().Be("{}");
    }

    [Fact]
    public void Serialize_OmitsEmptyExchanges()
    {
        var marks = SymbolMarkMap.Parse("""{"demo":["BTC/USDT"]}""");

        SymbolMarkMap.Serialize(marks).Should().NotContain("[]");
    }

    [Fact]
    public void Serialize_ThenParse_RoundTripsTheMap()
    {
        var marks = SymbolMarkMap.Parse("""{"demo":["BTC/USDT","ETH/BTC"],"bybit":["SOL/USDT"]}""");

        var roundTripped = SymbolMarkMap.Parse(SymbolMarkMap.Serialize(marks));

        roundTripped.Keys.Should().BeEquivalentTo(marks.Keys);
        foreach (var exchange in marks.Keys)
        {
            roundTripped[exchange].Should().BeEquivalentTo(marks[exchange]);
        }
    }

    [Fact]
    public void WithToggled_AnUnmarkedSymbol_JoinsTheMap()
    {
        var marks = SymbolMarkMap.Parse("""{"bybit":["SOL/USDT"],"fake":["ETH/BTC"]}""");

        var toggled = SymbolMarkMap.WithToggled(marks, "fake", "BTC/USDT");

        toggled["fake"].Should().BeEquivalentTo(["ETH/BTC", "BTC/USDT"]);
        toggled["bybit"].Should().BeEquivalentTo(
            ["SOL/USDT"],
            "the other exchange's entry passes through untouched");
        toggled.Should().HaveCount(2);
    }

    [Fact]
    public void WithToggled_AMarkedSymbol_DropsAndTheEmptyEntryLeaves()
    {
        var marks = SymbolMarkMap.Parse("""{"fake":["BTC/USDT"],"bybit":["SOL/USDT"]}""");

        var toggled = SymbolMarkMap.WithToggled(marks, "fake", "BTC/USDT");

        toggled.Keys.Should().BeEquivalentTo(
            ["bybit"],
            "a cleared exchange's entry never stays behind as an empty shell");
    }

    [Fact]
    public void WithToggled_AnUnknownExchange_CreatesTheEntry()
    {
        var marks = SymbolMarkMap.Parse("""{"bybit":["SOL/USDT"]}""");

        var toggled = SymbolMarkMap.WithToggled(marks, "demo", "BTC/USDT");

        toggled["demo"].Should().ContainSingle().Which.Should().Be("BTC/USDT");
    }

    [Fact]
    public void WithToggled_NeverMutatesTheInputMap()
    {
        var marks = SymbolMarkMap.Parse("""{"fake":["BTC/USDT"]}""");
        var before = string.Join(',', marks["fake"]);

        _ = SymbolMarkMap.WithToggled(marks, "fake", "ETH/BTC");

        string.Join(',', marks["fake"]).Should().Be(before, "the input map is immutable by contract");
    }

    [Fact]
    public void WithSet_ReplacesTheWholeExchangeEntry()
    {
        var marks = SymbolMarkMap.Parse("""{"demo":["SOL/USDC"],"bybit":["ETH/BTC"]}""");

        var replaced = SymbolMarkMap.WithSet(marks, "demo", ["BTC/USDT", "ETH/BTC"]);

        replaced["demo"].Should().BeEquivalentTo(["BTC/USDT", "ETH/BTC"]);
        replaced["bybit"].Should().BeEquivalentTo(
            ["ETH/BTC"],
            "the other exchange's entry passes through untouched");
    }

    [Fact]
    public void WithSet_AnEmptySet_DropsTheExchange()
    {
        var marks = SymbolMarkMap.Parse("""{"demo":["SOL/USDC"],"bybit":["ETH/BTC"]}""");

        var replaced = SymbolMarkMap.WithSet(marks, "demo", Array.Empty<string>());

        replaced.Keys.Should().BeEquivalentTo(
            ["bybit"],
            "a cleared exchange's entry never stays behind as an empty shell");
    }

    [Fact]
    public void WithSet_AnUnknownExchange_CreatesTheEntry()
    {
        var marks = SymbolMarkMap.Parse("""{"bybit":["SOL/USDT"]}""");

        var replaced = SymbolMarkMap.WithSet(marks, "demo", ["BTC/USDT"]);

        replaced["demo"].Should().ContainSingle().Which.Should().Be("BTC/USDT");
    }

    [Fact]
    public void WithSet_NeverMutatesTheInputMap()
    {
        var marks = SymbolMarkMap.Parse("""{"demo":["SOL/USDC"]}""");

        _ = SymbolMarkMap.WithSet(marks, "demo", ["ETH/BTC"]);

        marks["demo"].Should().BeEquivalentTo(
            ["SOL/USDC"],
            "the input map is immutable by contract");
    }

    [Fact]
    public void Serialize_SortsEachExchangeNamesInNormalizedOrder()
    {
        var marks = SymbolMarkMap.Parse("""{"demo":["SOL/USDC","BTC/USDT"]}""");

        SymbolMarkMap.Serialize(marks).Should().Be(
            """{"demo":["BTC/USDT","SOL/USDC"]}""",
            "a stable document for the same marks: no save-order noise");
    }
}
