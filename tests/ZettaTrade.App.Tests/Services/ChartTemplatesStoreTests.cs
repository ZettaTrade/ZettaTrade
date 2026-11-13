namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Charting;
using ZettaTrade.UI.Charting;

/// <summary>
/// Pins for the stored named-chart-templates envelope (T-202): the stored
/// shape, the fail-safe parse (a malformed document answers an unreadable
/// version, an unusable entry drops and the remainder survives, a
/// duplicate name keeps the last), the strict enum-name parse (no silent
/// default substitution, a numeric artifact is not a name), the version
/// contract (a foreign version carries back with no templates — the
/// manager's mutations refuse on it, the file is never clobbered), and the
/// inverse serialization with a stable ordinal order. Pure facts.
/// </summary>
public sealed class ChartTemplatesStoreTests
{
    /// <summary>The parse golden: camelCase keys, enum names, verbatim indicators.</summary>
    [Fact]
    public void AStoredEnvelope_ParsesIntoTheTypedTemplates()
    {
        var document = ChartTemplatesStore.Parse(
            """
            {"version":1,"templates":[
              {"name":"Swing","chartType":"Line","scaleMode":"Percent","indicators":"[]","showVwap":false,"showVolumeProfile":true},
              {"name":"Scalp","chartType":"HeikinAshi","scaleMode":"Logarithmic","indicators":"[{\"name\":\"SMA\",\"enabled\":true,\"period\":20}]","showVwap":true,"showVolumeProfile":false}
            ]}
            """);

        document.Version.Should().Be(ChartTemplatesStore.CurrentVersion, "the current envelope parses as current");
        document.Templates.Should().HaveCount(2, "both entries parse");
        document.Templates[0].Name.Should().Be("Scalp", "the rows land in their name-ordinal order regardless of the stored order");
        document.Templates[0].Should().Be(
            new ChartTemplate(
                "Scalp",
                ChartSeriesType.HeikinAshi,
                PriceScaleMode.Logarithmic,
                """[{"name":"SMA","enabled":true,"period":20}]""",
                true,
                false),
            "every facet carries back exactly as stored — the indicators verbatim");
        document.Templates[1].Name.Should().Be("Swing");
        document.Templates[1].Should().Be(
            new ChartTemplate("Swing", ChartSeriesType.Line, PriceScaleMode.Percent, "[]", false, true));
    }

    /// <summary>A foreign version carries back with no templates: the manager's mutations refuse, the file is never clobbered.</summary>
    [Fact]
    public void AForeignVersion_CarriesBack_WithNoTemplates()
    {
        var document = ChartTemplatesStore.Parse(
            """{"version":7,"templates":[{"name":"Scalp","chartType":"Line","scaleMode":"Linear","indicators":"[]","showVwap":true,"showVolumeProfile":true}]}""");

        document.Version.Should().Be(7, "the stored version rides back for the refusal guard");
        document.Templates.Should().BeEmpty("a foreign envelope's entries never become this build's templates");
    }

    /// <summary>A malformed document answers an unreadable version (0): the manager refuses, the file stands as stored.</summary>
    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"version\":\"one\",\"templates\":[]}")]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    public void AMalformedDocument_AnswersAnUnreadableVersion(string json)
    {
        var document = ChartTemplatesStore.Parse(json);

        document.Version.Should().Be(0, "an unreadable version refuses every mutation");
        document.Templates.Should().BeEmpty("nothing usable was read");
    }

    /// <summary>An absent or blank document is not garbage: it is the current version's empty store (a fresh profile's shape).</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankDocument_AnswersTheCurrentEmptyDocument(string json)
    {
        var document = ChartTemplatesStore.Parse(json);

        document.Version.Should().Be(ChartTemplatesStore.CurrentVersion);
        document.Templates.Should().BeEmpty();
    }

    [Fact]
    public void AnAbsentDocument_AnswersTheCurrentEmptyDocument()
    {
        var document = ChartTemplatesStore.Parse(null);

        document.Version.Should().Be(ChartTemplatesStore.CurrentVersion);
        document.Templates.Should().BeEmpty();
    }

    /// <summary>
    /// Every unusable piece drops and the usable remainder survives: a
    /// missing or blank name, an unknown or numeric enum name, a missing
    /// indicators document and a missing or non-bool toggle each cost at
    /// most their own entry, never the parse.
    /// </summary>
    [Theory]
    [InlineData("""{"version":1,"templates":[{"chartType":"Line","scaleMode":"Linear","indicators":"[]","showVwap":true,"showVolumeProfile":true},{"name":"Keep","chartType":"Line","scaleMode":"Linear","indicators":"[]","showVwap":true,"showVolumeProfile":true}]}""")]
    [InlineData("""{"version":1,"templates":[{"name":"   ","chartType":"Line","scaleMode":"Linear","indicators":"[]","showVwap":true,"showVolumeProfile":true},{"name":"Keep","chartType":"Line","scaleMode":"Linear","indicators":"[]","showVwap":true,"showVolumeProfile":true}]}""")]
    [InlineData("""{"version":1,"templates":[{"name":"Drop","chartType":"Candles","scaleMode":"Linear","indicators":"[]","showVwap":true,"showVolumeProfile":true},{"name":"Keep","chartType":"Line","scaleMode":"Linear","indicators":"[]","showVwap":true,"showVolumeProfile":true}]}""")]
    [InlineData("""{"version":1,"templates":[{"name":"Drop","chartType":"1","scaleMode":"Linear","indicators":"[]","showVwap":true,"showVolumeProfile":true},{"name":"Keep","chartType":"Line","scaleMode":"Linear","indicators":"[]","showVwap":true,"showVolumeProfile":true}]}""")]
    [InlineData("""{"version":1,"templates":[{"name":"Drop","chartType":"Line","scaleMode":"Wide","indicators":"[]","showVwap":true,"showVolumeProfile":true},{"name":"Keep","chartType":"Line","scaleMode":"Linear","indicators":"[]","showVwap":true,"showVolumeProfile":true}]}""")]
    [InlineData("""{"version":1,"templates":[{"name":"Drop","chartType":"Line","scaleMode":"Linear","showVwap":true,"showVolumeProfile":true},{"name":"Keep","chartType":"Line","scaleMode":"Linear","indicators":"[]","showVwap":true,"showVolumeProfile":true}]}""")]
    [InlineData("""{"version":1,"templates":[{"name":"Drop","chartType":"Line","scaleMode":"Linear","indicators":"   ","showVwap":true,"showVolumeProfile":true},{"name":"Keep","chartType":"Line","scaleMode":"Linear","indicators":"[]","showVwap":true,"showVolumeProfile":true}]}""")]
    [InlineData("""{"version":1,"templates":[{"name":"Drop","chartType":"Line","scaleMode":"Linear","indicators":"[]","showVwap":"yes","showVolumeProfile":true},{"name":"Keep","chartType":"Line","scaleMode":"Linear","indicators":"[]","showVwap":true,"showVolumeProfile":true}]}""")]
    [InlineData("""{"version":1,"templates":[{"name":"Drop","chartType":"Line","scaleMode":"Linear","indicators":"[]","showVwap":true},{"name":"Keep","chartType":"Line","scaleMode":"Linear","indicators":"[]","showVwap":true,"showVolumeProfile":true}]}""")]
    public void AnUnusableEntry_Drops_AndTheRemainderSurvives(string json)
    {
        var document = ChartTemplatesStore.Parse(json);

        document.Templates.Should().ContainSingle("the unusable entry drops, the neighbor survives")
            .Which.Name.Should().Be("Keep");
    }

    /// <summary>A duplicated name keeps the LAST entry: the later record is the fresher one.</summary>
    [Fact]
    public void ADuplicateName_KeepsTheLastEntry()
    {
        var document = ChartTemplatesStore.Parse(
            """
            {"version":1,"templates":[
              {"name":"Scalp","chartType":"Line","scaleMode":"Linear","indicators":"[]","showVwap":false,"showVolumeProfile":false},
              {"name":"Scalp","chartType":"HeikinAshi","scaleMode":"Logarithmic","indicators":"[]","showVwap":true,"showVolumeProfile":true}
            ]}
            """);

        document.Templates.Should().ContainSingle().Which.Should().Be(
            new ChartTemplate("Scalp", ChartSeriesType.HeikinAshi, PriceScaleMode.Logarithmic, "[]", true, true),
            "the later record wins");
    }

    /// <summary>A stored name trims into its canonical shape.</summary>
    [Fact]
    public void AStoredName_Trims()
    {
        var document = ChartTemplatesStore.Parse(
            """{"version":1,"templates":[{"name":"  Scalp  ","chartType":"Line","scaleMode":"Linear","indicators":"[]","showVwap":true,"showVolumeProfile":true}]}""");

        document.Templates.Single().Name.Should().Be("Scalp");
    }

    /// <summary>The serializer writes the current version's camelCase envelope in its name-ordinal order — a stable document for the same store.</summary>
    [Fact]
    public void TheSerializer_WritesTheCurrentVersion_CamelCase_InOrdinalOrder()
    {
        var serialized = ChartTemplatesStore.Serialize(
        [
            new ChartTemplate("Swing", ChartSeriesType.Line, PriceScaleMode.Percent, "[]", false, true),
            new ChartTemplate("Scalp", ChartSeriesType.HeikinAshi, PriceScaleMode.Logarithmic, "[{}]", true, false),
        ]);

        serialized.Should().StartWith("""{"version":1,"templates":[""", "the envelope is the current version's");
        serialized.Should().Contain("""{"name":"Scalp","chartType":"HeikinAshi","scaleMode":"Logarithmic","indicators":"[{}]","showVwap":true,"showVolumeProfile":false}""", "the camelCase shape the stored file keeps");
        serialized.IndexOf("Scalp", StringComparison.Ordinal)
            .Should().BeLessThan(serialized.IndexOf("Swing", StringComparison.Ordinal), "the name-ordinal order is the stored order");
    }

    /// <summary>A store with no templates serializes to the empty current envelope.</summary>
    [Fact]
    public void AnEmptyStore_SerializesToTheEmptyEnvelope()
    {
        ChartTemplatesStore.Serialize([])
            .Should().Be("""{"version":1,"templates":[]}""");
    }

    /// <summary>The round-trip closes: parse(Serialize(list)) reads back the same templates, normalized.</summary>
    [Fact]
    public void AStore_RoundTripsThroughTheStore()
    {
        List<ChartTemplate> templates =
        [
            new("Swing", ChartSeriesType.Area, PriceScaleMode.Percent, "[]", true, true),
            new("Scalp", ChartSeriesType.Bars, PriceScaleMode.Linear, "[{}]", false, false),
        ];

        var roundTripped = ChartTemplatesStore.Parse(ChartTemplatesStore.Serialize(templates));

        roundTripped.Version.Should().Be(ChartTemplatesStore.CurrentVersion);
        roundTripped.Templates.Should().Equal(
            [
                new ChartTemplate("Scalp", ChartSeriesType.Bars, PriceScaleMode.Linear, "[{}]", false, false),
                new ChartTemplate("Swing", ChartSeriesType.Area, PriceScaleMode.Percent, "[]", true, true),
            ],
            "the round-trip reads back the same faces in the stored order");
    }
}
