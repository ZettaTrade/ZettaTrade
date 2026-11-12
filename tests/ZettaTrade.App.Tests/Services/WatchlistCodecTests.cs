namespace ZettaTrade.App.Tests.Services;

using System;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;

/// <summary>
/// The watchlist codec's facts (T-196, the alerts codec's discipline): the
/// starter document's exact shape and its decode/encode round trip, the
/// uniform root fault (a malformed payload, a wrong shape, a missing or
/// foreign version — one ArgumentException to the caller, never a
/// fabricated document), the per-entry skips (a blank name, a non-string
/// symbol, a missing member), and the symbol texts' raw carriage.
/// </summary>
public sealed class WatchlistCodecTests
{
    [Fact]
    public void TheStarterDocument_CarriesTheMajorsUnderTheAnchorName()
    {
        var starter = WatchlistCodec.StarterDocument();

        starter.ActiveList.Should().Be(WatchlistSettingsKeys.StarterListName);
        starter.Lists.Should().HaveCount(1);
        starter.Lists[0].Name.Should().Be(WatchlistSettingsKeys.StarterListName);
        starter.Lists[0].Symbols.Should().Equal(WatchlistCodec.StarterSymbols);
        WatchlistCodec.StarterSymbols.Should().HaveCount(10, "the majors roster is the starter's body");
    }

    [Fact]
    public void TheStarterDocument_MatchesTheLockedShape()
    {
        var starter = WatchlistCodec.StarterDocument();

        starter.Should().BeEquivalentTo(
            new WatchlistDocument(
                "Majors",
                [new WatchlistList("Majors",
                [
                    "BTC/USDT",
                    "ETH/USDT",
                    "SOL/USDT",
                    "XRP/USDT",
                    "ADA/USDT",
                    "DOGE/USDT",
                    "BNB/USDT",
                    "LINK/USDT",
                    "AVAX/USDT",
                    "LTC/USDT",
                ])]),
            "the starter's exact shape is the panel's first-run contract");
    }

    [Fact]
    public void EncodeThenDecode_RoundTripsTheDocumentExactly()
    {
        var document = new WatchlistDocument(
            "Alts",
            [
                new WatchlistList("Majors", ["BTC/USDT", "ETH/USDT"]),
                new WatchlistList("Alts", ["TRX/USDT"]),
                new WatchlistList("Empty", []),
            ]);

        var decoded = WatchlistCodec.Decode(WatchlistCodec.Encode(document));

        decoded.SkippedCount.Should().Be(0);
        decoded.Document.Should().BeEquivalentTo(document, "the codec's own payload decodes to the same document");
    }

    [Fact]
    public void TheEncodedPayload_IsTheVersionedWrapperShape()
    {
        var payload = WatchlistCodec.Encode(WatchlistCodec.StarterDocument());

        payload.Should().StartWith("""{"version":1,"active":"Majors","lists":[{""");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"version":1,"lists":[]}""")]
    [InlineData("""{"version":2,"active":"Majors","lists":[]}""")]
    public void AnUnreadableRoot_ThrowsTheUniformFault(string payload)
    {
        var decode = () => WatchlistCodec.Decode(payload);

        decode.Should().Throw<ArgumentException>()
            .WithParameterName("payload");
    }

    [Fact]
    public void AVersionlessRoot_ThrowsTheUniformFault()
    {
        var decode = () => WatchlistCodec.Decode("""{"active":"Majors","lists":[]}""");

        decode.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AnEntryWithABlankName_IsSkippedAndCounted()
    {
        var decoded = WatchlistCodec.Decode(
            """{"version":1,"active":"Majors","lists":[{"name":"   ","symbols":[]},{"name":"Majors","symbols":["BTC/USDT"]}]}""");

        decoded.SkippedCount.Should().Be(1);
        decoded.Document.Lists.Select(list => list.Name).Should().Equal(["Majors"]);
    }

    [Fact]
    public void AnEntryWithANonStringSymbol_IsSkippedInFull()
    {
        var decoded = WatchlistCodec.Decode(
            """{"version":1,"active":"Majors","lists":[{"name":"Majors","symbols":["BTC/USDT",42]},{"name":"Alts","symbols":["TRX/USDT"]}]}""");

        decoded.SkippedCount.Should().Be(1, "a partially-read roster is never fabricated — the entry skips in full");
        decoded.Document.Lists.Select(list => list.Name).Should().Equal(["Alts"]);
    }

    [Fact]
    public void AnEntryWithoutSymbols_IsSkippedAndCounted()
    {
        var decoded = WatchlistCodec.Decode(
            """{"version":1,"active":"Majors","lists":[{"name":"Majors"},{"name":"Alts","symbols":[]}]}""");

        decoded.SkippedCount.Should().Be(1);
        decoded.Document.Lists.Select(list => list.Name).Should().Equal(["Alts"]);
    }

    [Fact]
    public void TheSymbolTexts_RideRaw()
    {
        var document = new WatchlistDocument(
            "Odd",
            [new WatchlistList("Odd", ["btc-usdt", "  ", "X Y Z"])]);

        var decoded = WatchlistCodec.Decode(WatchlistCodec.Encode(document));

        decoded.Document.Lists[0].Symbols.Should().Equal(["btc-usdt", "  ", "X Y Z"],
            "the codec parses nothing — the feed is the parse-guard");
    }

    [Fact]
    public void AnEmptyRoster_DecodesAsAnEmptyDocument()
    {
        var decoded = WatchlistCodec.Decode("""{"version":1,"active":"Majors","lists":[]}""");

        decoded.SkippedCount.Should().Be(0);
        decoded.Document.Lists.Should().BeEmpty("an emptied roster is shape-valid — the catalog answers with the starter");
    }

    [Fact]
    public void TheMembers_ReadCaseInsensitively()
    {
        var decoded = WatchlistCodec.Decode(
            """{"VERSION":1,"Active":"Majors","LISTS":[{"NAME":"Majors","SYMBOLS":["BTC/USDT"]}]}""");

        decoded.Document.ActiveList.Should().Be("Majors");
        decoded.Document.Lists.Should().HaveCount(1);
        decoded.Document.Lists[0].Symbols.Should().Equal(["BTC/USDT"]);
    }
}
