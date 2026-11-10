namespace ZettaTrade.App.Tests.Services;

using System;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;

/// <summary>
/// The stored detached-panel record (T-103): a camelCase JSON array under
/// <c>UI.DetachedPanels</c> whose parse is fail-safe by contract — blank,
/// malformed or unusable documents answer empty (never an exception, never
/// a fabricated panel), unusable entries drop, one entry survives per
/// panel (the fresher duplicate), and the round-trip is exact.
/// </summary>
public sealed class DetachedPanelsContractTests
{
    [Fact]
    public void Serialize_CarriesTheCamelCaseShape()
    {
        var json = DetachedPanelsContract.Serialize(
        [
            new DetachedPanelEntry(
                PanelDetachId.Chart,
                new PanelDisplaySample(0, 0, 1920, 1080, 1.5),
                new PanelWindowPlacement(false, 0.1, 0.2, 0.5, 0.5)),
        ]);

        json.Should().Contain("\"panel\":0");
        json.Should().Contain("\"display\":{");
        json.Should().Contain("\"workingLeft\":0");
        json.Should().Contain("\"workingWidth\":1920");
        json.Should().Contain("\"placement\":{");
        json.Should().Contain("\"leftFraction\":0.1");
        json.Should().Contain("\"isMaximized\":false");
    }

    [Fact]
    public void Serialize_Parse_RoundTripsTheRecord()
    {
        var entries = new[]
        {
            new DetachedPanelEntry(
                PanelDetachId.Chart,
                new PanelDisplaySample(-1920, 0, 2560, 1440, 1.25),
                new PanelWindowPlacement(false, 0.25, 0.125, 0.6, 0.75)),
            new DetachedPanelEntry(
                PanelDetachId.Assistant,
                new PanelDisplaySample(3840, 2160, 1920, 1080, 2.0),
                new PanelWindowPlacement(true, 0, 0, 0.4, 0.6)),
        };

        DetachedPanelsContract.Parse(DetachedPanelsContract.Serialize(entries))
            .Should().BeEquivalentTo(entries);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("42")]
    [InlineData("\"a string root\"")]
    [InlineData("{\"panel\":0}")]
    public void Parse_BlankMalformedOrWrongRoot_AnswersEmpty(string stored)
    {
        DetachedPanelsContract.Parse(stored).Should().BeEmpty();
    }

    [Fact]
    public void Parse_AnAbsentDocument_AnswersEmpty()
    {
        DetachedPanelsContract.Parse(null).Should().BeEmpty();
    }

    [Fact]
    public void Parse_DropsUnusableEntries()
    {
        var json = DetachedPanelsContract.Serialize(
        [
            new DetachedPanelEntry(
                PanelDetachId.Book,
                new PanelDisplaySample(0, 0, 0, 1080, 1.0),
                new PanelWindowPlacement(false, 0.1, 0.1, 0.5, 0.5)),
        ]);

        DetachedPanelsContract.Parse(json).Should().BeEmpty(
            "a zero-sized display cannot carry the fraction math");
    }

    [Fact]
    public void Parse_DropsUnknownPanelIds()
    {
        const string json =
            """[{"panel":99,"display":{"workingLeft":0,"workingTop":0,"workingWidth":1920,"workingHeight":1080,"scale":1.5},"placement":{"isMaximized":false,"leftFraction":0.1,"topFraction":0.1,"widthFraction":0.5,"heightFraction":0.5}}]""";

        DetachedPanelsContract.Parse(json).Should().BeEmpty(
            "an unknown panel id is a fabricated panel — never a default substitution");
    }

    [Fact]
    public void Parse_KeepsTheFresherDuplicatePerPanel_InCanonicalOrder()
    {
        var stale = new DetachedPanelEntry(
            PanelDetachId.Chart,
            new PanelDisplaySample(0, 0, 1920, 1080, 1.5),
            new PanelWindowPlacement(false, 0.1, 0.1, 0.3, 0.3));
        var fresh = new DetachedPanelEntry(
            PanelDetachId.Chart,
            new PanelDisplaySample(0, 0, 1920, 1080, 1.5),
            new PanelWindowPlacement(false, 0.2, 0.2, 0.4, 0.4));
        var assistant = new DetachedPanelEntry(
            PanelDetachId.Assistant,
            new PanelDisplaySample(1920, 0, 1920, 1080, 1.5),
            new PanelWindowPlacement(false, 0, 0, 0.3, 0.5));

        var parsed = DetachedPanelsContract.Parse(DetachedPanelsContract.Serialize(
            [assistant, stale, fresh]));

        parsed.Should().HaveCount(2);
        parsed.Select(entry => entry.Panel).Should().ContainInConsecutiveOrder(
            PanelDetachId.Chart,
            PanelDetachId.Assistant);
        parsed[0].Should().BeEquivalentTo(fresh, "the later record is the fresher one");
    }
}
